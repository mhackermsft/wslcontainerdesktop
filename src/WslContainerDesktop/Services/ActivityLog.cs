// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.Services;

/// <summary>
/// File-backed <see cref="IActivityLog"/>. Events live in
/// <c>%LOCALAPPDATA%\WslContainerDesktop\activity.json</c> next to the other app state and are
/// capped to the most recent <see cref="MaxEvents"/> entries. All mutation happens on the UI
/// thread: <see cref="Attach"/> subscribes to <see cref="StatusMonitor.StatusChanged"/> (which the
/// monitor already raises on the dispatcher) and the images view model records pull/build outcomes
/// from UI-thread command handlers. Load/persist failures never crash the app.
/// </summary>
public sealed class ActivityLog : IActivityLog
{
    private const int MaxEvents = 500;

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string ActivityFile = Path.Combine(SettingsDirectory, "activity.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly StatusMonitor _monitor;
    private readonly ILogger<ActivityLog> _logger;

    // Baseline for snapshot diffing: last-seen state per container id, and last engine health.
    private readonly Dictionary<string, ContainerState> _lastContainerStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastContainerNames = new(StringComparer.Ordinal);
    private EngineHealth? _lastHealth;
    private bool _attached;
    private bool _seeded;

    public ObservableCollection<ActivityEvent> Events { get; } = new();

    public ActivityLog(StatusMonitor monitor, ILogger<ActivityLog> logger)
    {
        _monitor = monitor;
        _logger = logger;
        Load();
    }

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _monitor.StatusChanged += OnStatusChanged;

        // Seed the baseline from the latest snapshot (if any) without emitting events, so the
        // first real transition after launch is what surfaces rather than a burst of "started".
        if (_monitor.Latest is { } latest)
        {
            SeedBaseline(latest);
            _seeded = true;
        }
    }

    public void Record(ActivityEvent evt)
    {
        if (evt is null)
        {
            return;
        }

        Events.Insert(0, evt);
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }

        Persist();
    }

    public void RecordImagePull(string reference, bool success, string? error = null)
    {
        var name = string.IsNullOrWhiteSpace(reference) ? "image" : reference.Trim();
        Record(new ActivityEvent
        {
            Category = ActivityCategory.Image,
            Kind = ActivityKind.ImagePulled,
            Title = success ? $"Pulled {name}" : $"Pull failed: {name}",
            Detail = success ? null : Trim(error),
            IsError = !success,
        });
    }

    public void RecordImageBuild(string tag, bool success, string? error = null)
    {
        var name = string.IsNullOrWhiteSpace(tag) ? "image" : tag.Trim();
        Record(new ActivityEvent
        {
            Category = ActivityCategory.Image,
            Kind = ActivityKind.ImageBuilt,
            Title = success ? $"Built {name}" : $"Build failed: {name}",
            Detail = success ? null : Trim(error),
            IsError = !success,
        });
    }

    public void Clear()
    {
        Events.Clear();
        Persist();
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot snapshot)
    {
        try
        {
            // The first snapshot we observe only establishes a baseline; emitting events for
            // everything already running/up at launch would spam the timeline.
            if (!_seeded)
            {
                SeedBaseline(snapshot);
                _seeded = true;
                return;
            }

            var changed = false;

            // Engine up/down transitions. Treat healthy/degraded as "up".
            var isUp = snapshot.Health is EngineHealth.Healthy or EngineHealth.Degraded;
            if (_lastHealth is { } prevHealth)
            {
                var wasUp = prevHealth is EngineHealth.Healthy or EngineHealth.Degraded;
                if (wasUp && snapshot.Health == EngineHealth.Down)
                {
                    Events.Insert(0, EngineEvent(ActivityKind.EngineDown, "Engine became unreachable", isError: true));
                    changed = true;
                }
                else if (prevHealth == EngineHealth.Down && isUp)
                {
                    Events.Insert(0, EngineEvent(ActivityKind.EngineUp, "Engine is running"));
                    changed = true;
                }
            }

            _lastHealth = snapshot.Health;

            // Only diff containers while the engine stays up; a down/up bounce would otherwise
            // report every container as removed then re-created.
            if (isUp)
            {
                changed |= DiffContainers(snapshot.Containers);
            }
            // While the engine is down we neither diff nor clear the baseline. Keeping the
            // last-known container states means the first healthy snapshot after recovery is
            // compared against them, so still-running containers produce no spurious "started"
            // events (only genuine changes during the outage are reported).

            if (changed)
            {
                while (Events.Count > MaxEvents)
                {
                    Events.RemoveAt(Events.Count - 1);
                }

                Persist();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process activity snapshot.");
        }
    }

    private bool DiffContainers(IReadOnlyList<ContainerInfo> containers)
    {
        var changed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in containers)
        {
            if (string.IsNullOrEmpty(c.Id))
            {
                continue;
            }

            seen.Add(c.Id);
            var name = string.IsNullOrWhiteSpace(c.Name) ? c.ShortId : c.Name;
            _lastContainerNames[c.Id] = name;

            if (!_lastContainerStates.TryGetValue(c.Id, out var prev))
            {
                // Newly observed container. Running => started; anything else => created.
                var kind = c.State == ContainerState.Running ? ActivityKind.ContainerStarted : ActivityKind.ContainerCreated;
                var verb = kind == ActivityKind.ContainerStarted ? "started" : "created";
                Events.Insert(0, ContainerEvent(kind, $"{name} {verb}", c.ShortId));
                changed = true;
            }
            else if (prev != c.State)
            {
                if (c.State == ContainerState.Running && prev != ContainerState.Running)
                {
                    Events.Insert(0, ContainerEvent(ActivityKind.ContainerStarted, $"{name} started", c.ShortId));
                    changed = true;
                }
                else if (prev == ContainerState.Running && c.State != ContainerState.Running)
                {
                    Events.Insert(0, ContainerEvent(ActivityKind.ContainerStopped, $"{name} stopped", c.ShortId));
                    changed = true;
                }
            }

            _lastContainerStates[c.Id] = c.State;
        }

        // Any id present last time but gone now was removed.
        foreach (var id in _lastContainerStates.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var name = _lastContainerNames.TryGetValue(id, out var n) ? n : (id.Length > 12 ? id[..12] : id);
            var shortId = id.Length > 12 ? id[..12] : id;
            Events.Insert(0, ContainerEvent(ActivityKind.ContainerRemoved, $"{name} removed", shortId));
            changed = true;
            _lastContainerStates.Remove(id);
            _lastContainerNames.Remove(id);
        }

        return changed;
    }

    private void SeedBaseline(EngineStatusSnapshot snapshot)
    {
        _lastHealth = snapshot.Health;
        _lastContainerStates.Clear();
        _lastContainerNames.Clear();
        if (snapshot.Health is EngineHealth.Healthy or EngineHealth.Degraded)
        {
            foreach (var c in snapshot.Containers)
            {
                if (string.IsNullOrEmpty(c.Id))
                {
                    continue;
                }

                _lastContainerStates[c.Id] = c.State;
                _lastContainerNames[c.Id] = string.IsNullOrWhiteSpace(c.Name) ? c.ShortId : c.Name;
            }
        }
    }

    private static ActivityEvent EngineEvent(ActivityKind kind, string title, bool isError = false) => new()
    {
        Category = ActivityCategory.Engine,
        Kind = kind,
        Title = title,
        IsError = isError,
    };

    private static ActivityEvent ContainerEvent(ActivityKind kind, string title, string shortId) => new()
    {
        Category = ActivityCategory.Container,
        Kind = kind,
        Title = title,
        Detail = shortId,
    };

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ActivityFile))
            {
                return;
            }

            var json = File.ReadAllText(ActivityFile);
            var loaded = JsonSerializer.Deserialize<List<ActivityEvent>>(json, SerializerOptions);
            if (loaded is null)
            {
                return;
            }

            // File is stored newest-first; keep that order and cap.
            foreach (var evt in loaded.Take(MaxEvents))
            {
                if (evt is not null)
                {
                    Events.Add(evt);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load activity log from {Path}; starting empty.", ActivityFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(Events.ToList(), SerializerOptions);
            File.WriteAllText(ActivityFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save activity log to {Path}.", ActivityFile);
        }
    }
}
