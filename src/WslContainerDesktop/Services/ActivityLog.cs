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
/// capped to the most recent <see cref="MaxEvents"/> entries. <see cref="Events"/> is bound to the
/// Activity page, so every mutation is marshalled to the UI thread by <see cref="Record"/> itself —
/// background callers such as the assistant's tool callback do not need to marshal first.
/// Load/persist failures never crash the app.
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
    private readonly IEngineEventStream _events;
    private readonly ILogger<ActivityLog> _logger;

    // Baseline for snapshot diffing: last-seen state per container id, and last engine health.
    private readonly Dictionary<string, ContainerState> _lastContainerStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastContainerNames = new(StringComparer.Ordinal);
    private EngineHealth? _lastHealth;
    private bool _attached;
    private bool _seeded;

    /// <summary>
    /// Gets the recent activity events displayed by the activity log UI.
    /// </summary>
    public ObservableCollection<ActivityEvent> Events { get; } = new();

    /// <summary>
    /// Initializes a new <c>ActivityLog</c> with the collaborators it needs from dependency injection.
    /// </summary>
    public ActivityLog(StatusMonitor monitor, IEngineEventStream events, ILogger<ActivityLog> logger)
    {
        _monitor = monitor;
        _events = events;
        _logger = logger;
        Load();
    }

    /// <summary>
    /// Subscribes the activity log to monitor and engine event notifications.
    /// </summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _monitor.StatusChanged += OnStatusChanged;
        _events.EventReceived += OnEngineEventReceived;

        // Seed the baseline from the latest snapshot (if any) without emitting events, so the
        // first real transition after launch is what surfaces rather than a burst of "started".
        if (_monitor.Latest is { } latest)
        {
            SeedBaseline(latest);
            _seeded = true;
        }
    }

    /// <summary>
    /// Adds a general activity entry to the in-memory and persisted activity history.
    /// </summary>
    public void Record(ActivityEvent evt)
    {
        if (evt is null)
        {
            return;
        }

        // Events is bound directly to the Activity page, so mutating it off the UI thread raises
        // CollectionChanged into XAML on the wrong thread and throws a message-free COMException.
        // The assistant records tool approvals and outcomes from its background tool callback, so
        // marshal here rather than relying on every caller to remember.
        var dispatcher = _monitor.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            if (dispatcher.TryEnqueue(() => RecordOnDispatcher(evt)))
            {
                return;
            }

            // The UI is gone (shutting down); keep the event out of the bound collection.
            _logger.LogDebug("Activity event dropped: the UI dispatcher is no longer accepting work.");
            return;
        }

        RecordOnDispatcher(evt);
    }

    private void RecordOnDispatcher(ActivityEvent evt)
    {
        // Keep the list newest-first by event time, not arrival time: the Activity page back-fills
        // the last hour and the live stream replays after a reconnect, so older events can arrive
        // after newer ones. Ordinary live events still go to index 0.
        var index = ActivityEvent.NewestFirstInsertIndex(Events, evt.Timestamp);

        if (index >= MaxEvents)
        {
            return; // Older than everything retained in a full log.
        }

        Events.Insert(index, evt);
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }

        Persist();
    }

    /// <summary>
    /// Adds an activity entry for an image pull.
    /// </summary>
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

    /// <summary>
    /// Adds an activity entry for an image build.
    /// </summary>
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

    /// <summary>
    /// Adds an activity entry translated from a raw engine event.
    /// </summary>
    public void RecordEngineEvent(EngineEvent evt)
    {
        if (evt is null || Events.Any(e => string.Equals(e.SourceEventKey, evt.StableKey, StringComparison.Ordinal)))
        {
            return;
        }

        Record(ToActivityEvent(evt));
    }

    /// <summary>
    /// Clears the in-memory and persisted activity history.
    /// </summary>
    public void Clear()
    {
        Events.Clear();
        Persist();
    }

    private void OnEngineEventReceived(object? sender, EngineEvent e) => RecordEngineEvent(e);

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
                changed |= DiffContainers(snapshot.Containers, emitEvents: !_events.IsConnected);
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

    private bool DiffContainers(IReadOnlyList<ContainerInfo> containers, bool emitEvents)
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
                if (emitEvents)
                {
                    var kind = c.State == ContainerState.Running ? ActivityKind.ContainerStarted : ActivityKind.ContainerCreated;
                    var verb = kind == ActivityKind.ContainerStarted ? "started" : "created";
                    Events.Insert(0, ContainerEvent(kind, $"{name} {verb}", c.ShortId));
                    changed = true;
                }
            }
            else if (prev != c.State)
            {
                if (c.State == ContainerState.Running && prev != ContainerState.Running)
                {
                    if (emitEvents)
                    {
                        Events.Insert(0, ContainerEvent(ActivityKind.ContainerStarted, $"{name} started", c.ShortId));
                        changed = true;
                    }
                }
                else if (prev == ContainerState.Running && c.State != ContainerState.Running)
                {
                    if (emitEvents)
                    {
                        Events.Insert(0, ContainerEvent(ActivityKind.ContainerStopped, $"{name} stopped", c.ShortId));
                        changed = true;
                    }
                }
            }

            _lastContainerStates[c.Id] = c.State;
        }

        // Any id present last time but gone now was removed.
        foreach (var id in _lastContainerStates.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var name = _lastContainerNames.TryGetValue(id, out var n) ? n : (id.Length > 12 ? id[..12] : id);
            var shortId = id.Length > 12 ? id[..12] : id;
            if (emitEvents)
            {
                Events.Insert(0, ContainerEvent(ActivityKind.ContainerRemoved, $"{name} removed", shortId));
                changed = true;
            }

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

    private static ActivityEvent ToActivityEvent(EngineEvent evt)
    {
        var category = evt.Type.ToLowerInvariant() switch
        {
            "container" => ActivityCategory.Container,
            "image" => ActivityCategory.Image,
            "network" => ActivityCategory.Network,
            _ => ActivityCategory.Engine,
        };
        var kind = (category, evt.Action.ToLowerInvariant()) switch
        {
            (ActivityCategory.Container, "create") => ActivityKind.ContainerCreated,
            (ActivityCategory.Container, "start") => ActivityKind.ContainerStarted,
            (ActivityCategory.Container, "stop" or "die" or "kill") => ActivityKind.ContainerStopped,
            (ActivityCategory.Container, "destroy" or "remove") => ActivityKind.ContainerRemoved,
            (ActivityCategory.Network, "create") => ActivityKind.NetworkCreated,
            (ActivityCategory.Network, "connect") => ActivityKind.NetworkConnected,
            (ActivityCategory.Network, "disconnect") => ActivityKind.NetworkDisconnected,
            (ActivityCategory.Network, "destroy" or "remove") => ActivityKind.NetworkRemoved,
            (ActivityCategory.Image, _) => ActivityKind.ImagePulled,
            _ => ActivityKind.EngineUp,
        };

        var attrs = evt.Attributes.Count == 0
            ? null
            : string.Join(", ", evt.Attributes
                .Where(kvp => kvp.Key is "image" or "exitCode" or "network" or "name" or "container" or "type")
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var name = evt.DisplayName;
        return new ActivityEvent
        {
            Timestamp = evt.Timestamp,
            Category = category,
            Kind = kind,
            Title = $"{evt.Type} {evt.Action}: {name}",
            Detail = string.IsNullOrWhiteSpace(attrs) ? evt.ActorId : attrs,
            IsError = evt.ExitCode is > 0,
            SourceEventKey = evt.StableKey,
            SourceType = evt.Type,
            SourceAction = evt.Action,
            ActorId = evt.ActorId,
            ContainerId = evt.ContainerId,
            Attributes = evt.Attributes,
        };
    }

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

            // Stored newest-first. Sort anyway (stable, so equal times keep their order) to repair
            // files written before events were inserted by time.
            foreach (var evt in loaded.Where(e => e is not null).OrderByDescending(e => e.Timestamp).Take(MaxEvents))
            {
                Events.Add(evt);
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
