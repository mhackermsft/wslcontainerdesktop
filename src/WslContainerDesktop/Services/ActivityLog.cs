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

    private readonly string _settingsDirectory;
    private readonly string _activityFile;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly StatusMonitor _monitor;
    private readonly IEngineEventStream _events;
    private readonly ILogger<ActivityLog> _logger;
    private readonly WslcSessionContext _sessionContext;

    // Baseline for snapshot diffing: last-seen state per container id, and last engine health.
    private readonly Dictionary<string, ContainerState> _lastContainerStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastContainerNames = new(StringComparer.Ordinal);
    private EngineHealth? _lastHealth;
    private AutomationSessionOwner? _lastOwner;
    private bool _attached;
    private bool _seeded;

    /// <summary>
    /// Gets the recent activity events displayed by the activity log UI.
    /// </summary>
    public ObservableCollection<ActivityEvent> Events { get; } = new();

    /// <summary>
    /// Initializes a new <c>ActivityLog</c> with the collaborators it needs from dependency injection.
    /// </summary>
    public ActivityLog(StatusMonitor monitor, IEngineEventStream events, ILogger<ActivityLog> logger,
        WslcSessionContext sessionContext)
        : this(monitor, events, logger, sessionContext, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslContainerDesktop"))
    {
    }

    internal ActivityLog(StatusMonitor monitor, IEngineEventStream events, ILogger<ActivityLog> logger,
        WslcSessionContext sessionContext, string settingsDirectory)
    {
        _monitor = monitor;
        _events = events;
        _logger = logger;
        _sessionContext = sessionContext;
        _settingsDirectory = settingsDirectory;
        _activityFile = Path.Combine(settingsDirectory, "activity.json");
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
        _sessionContext.SelectionChanged += OnSessionChanged;
        _sessionContext.EngineSettingChanged += OnSessionChanged;

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
        if (ActivityEventBatch.Insert(Events, evt, MaxEvents))
            Persist();
    }

    /// <summary>
    /// Adds an activity entry for an image pull.
    /// </summary>
    public void RecordImagePull(string reference, bool success, string? error = null, AutomationSessionOwner? owner = null)
    {
        var name = string.IsNullOrWhiteSpace(reference) ? "image" : reference.Trim();
        Record(new ActivityEvent
        {
            Category = ActivityCategory.Image,
            Kind = ActivityKind.ImagePulled,
            Title = success ? $"Pulled {name}" : $"Pull failed: {name}",
            Detail = success ? null : Trim(error),
            IsError = !success,
            SessionOwner = owner,
        });
    }

    /// <summary>
    /// Adds an activity entry for an image build.
    /// </summary>
    public void RecordImageBuild(string tag, bool success, string? error = null, AutomationSessionOwner? owner = null)
    {
        var name = string.IsNullOrWhiteSpace(tag) ? "image" : tag.Trim();
        Record(new ActivityEvent
        {
            Category = ActivityCategory.Image,
            Kind = ActivityKind.ImageBuilt,
            Title = success ? $"Built {name}" : $"Build failed: {name}",
            Detail = success ? null : Trim(error),
            IsError = !success,
            SessionOwner = owner,
        });
    }

    /// <summary>
    /// Adds an activity entry translated from a raw engine event.
    /// </summary>
    public void RecordEngineEvent(EngineEvent evt)
    {
        if (evt is not null)
            RecordEngineEvents([evt]);
    }

    /// <summary>Records multiple session-qualified engine events with one persistence operation.</summary>
    public void RecordEngineEvents(IReadOnlyList<EngineEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var dispatcher = _monitor.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            var snapshot = events.ToArray();
            if (!dispatcher.TryEnqueue(() => RecordEngineEventsOnDispatcher(snapshot)))
                _logger.LogDebug("Activity events dropped: the UI dispatcher is no longer accepting work.");
            return;
        }
        RecordEngineEventsOnDispatcher(events);
    }

    private void RecordEngineEventsOnDispatcher(IReadOnlyList<EngineEvent> events)
    {
        if (ActivityEventBatch.Apply(Events, events, MaxEvents))
            Persist();
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

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        void ClearBaseline()
        {
            _seeded = false;
            _lastHealth = null;
            _lastOwner = null;
            _lastContainerStates.Clear();
            _lastContainerNames.Clear();
        }

        var dispatcher = _monitor.Dispatcher;
        if (dispatcher is null || dispatcher.HasThreadAccess)
            ClearBaseline();
        else if (!dispatcher.TryEnqueue(ClearBaseline))
            _logger.LogDebug("Could not clear activity baseline because the UI dispatcher is unavailable.");
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
            var owner = OwnerFor(snapshot);
            if (owner is not null)
                _lastOwner = owner;

            // Engine up/down transitions. Treat healthy/degraded as "up".
            var isUp = snapshot.Health is EngineHealth.Healthy or EngineHealth.Degraded;
            if (_lastHealth is { } prevHealth)
            {
                var wasUp = prevHealth is EngineHealth.Healthy or EngineHealth.Degraded;
                if (wasUp && snapshot.Health == EngineHealth.Down)
                {
                    Events.Insert(0, EngineEvent(ActivityKind.EngineDown,
                        "Default CLI session became unreachable", _lastOwner, isError: true));
                    changed = true;
                }
                else if (prevHealth == EngineHealth.Down && isUp)
                {
                    Events.Insert(0, EngineEvent(ActivityKind.EngineUp, "Default CLI session is reachable", _lastOwner));
                    changed = true;
                }
            }

            _lastHealth = snapshot.Health;

            // Only diff containers while the engine stays up; a down/up bounce would otherwise
            // report every container as removed then re-created.
            if (isUp)
            {
                changed |= DiffContainers(snapshot.Containers, _lastOwner, emitEvents: !_events.IsConnected);
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

    private bool DiffContainers(IReadOnlyList<ContainerInfo> containers, AutomationSessionOwner? owner, bool emitEvents)
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
                    Events.Insert(0, ContainerEvent(kind, $"{name} {verb}", c.Id, c.ShortId, owner));
                    changed = true;
                }
            }
            else if (prev != c.State)
            {
                if (c.State == ContainerState.Running && prev != ContainerState.Running)
                {
                    if (emitEvents)
                    {
                        Events.Insert(0, ContainerEvent(ActivityKind.ContainerStarted, $"{name} started", c.Id, c.ShortId, owner));
                        changed = true;
                    }
                }
                else if (prev == ContainerState.Running && c.State != ContainerState.Running)
                {
                    if (emitEvents)
                    {
                        Events.Insert(0, ContainerEvent(ActivityKind.ContainerStopped, $"{name} stopped", c.Id, c.ShortId, owner));
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
                Events.Insert(0, ContainerEvent(ActivityKind.ContainerRemoved, $"{name} removed", id, shortId, owner));
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
        _lastOwner = OwnerFor(snapshot);
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

    private AutomationSessionOwner? OwnerFor(EngineStatusSnapshot snapshot)
    {
        if (snapshot.Session is null || string.IsNullOrWhiteSpace(snapshot.ExecutablePath))
            return null;
        var target = _sessionContext.Capture();
        var name = target.Name ?? WslcSessionActionGuard.ResolveDefaultSessionName();
        return string.Equals(name, snapshot.Session.Name, StringComparison.Ordinal)
            ? AutomationSessionOwner.Capture(snapshot.ExecutablePath, target) : null;
    }

    private static ActivityEvent EngineEvent(ActivityKind kind, string title, AutomationSessionOwner? owner, bool isError = false) => new()
    {
        Category = ActivityCategory.Engine,
        Kind = kind,
        Title = title,
        SessionOwner = owner,
        IsError = isError,
    };

    private static ActivityEvent ContainerEvent(ActivityKind kind, string title, string id, string shortId, AutomationSessionOwner? owner) => new()
    {
        Category = ActivityCategory.Container,
        Kind = kind,
        Title = title,
        Detail = shortId,
        ContainerId = id,
        SessionOwner = owner,
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
            if (!File.Exists(_activityFile))
            {
                return;
            }

            var json = File.ReadAllText(_activityFile);
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
            _logger.LogWarning(ex, "Failed to load activity log from {Path}; starting empty.", _activityFile);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var json = JsonSerializer.Serialize(Events.ToList(), SerializerOptions);
            File.WriteAllText(_activityFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save activity log to {Path}.", _activityFile);
        }
    }
}
