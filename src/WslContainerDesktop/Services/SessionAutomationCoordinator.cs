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

using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;
using WslContainerDesktop.Tray;
#if WSLCD_TEST_NO_DISPATCHER
using ScopedHealthWatchdog = WslContainerDesktop.Services.HealthWatchdogSessionHarness;
#else
using ScopedHealthWatchdog = WslContainerDesktop.Services.HealthWatchdog;
#endif

namespace WslContainerDesktop.Services;

/// <summary>Runs session-qualified watchdogs using observations from the shared status monitor.</summary>
#if WSLCD_TEST_NO_DISPATCHER
public sealed class SessionAutomationCoordinatorHarness : IDisposable
#else
public sealed class SessionAutomationCoordinator : IDisposable
#endif
{
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly WslcSessionContext _selection;
    private readonly WslcSessionClientFactory _clients;
    private readonly IWslcService _discovery;
    private readonly RestartSuppressionState _suppression;
    private readonly LegacyPolicyOwnershipStore _owners;
    private readonly ILoggerFactory _logs;
    private readonly ComposeProjectSupervisor _compose;
    private readonly HealthWatchdog _selectedHealth;
    private readonly SessionRemovalGuard? _removalGuard;
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedSessions = new(StringComparer.Ordinal);
    private HashSet<string>? _supervisedSessions;
    private readonly Dictionary<string, (AutomationSessionOwner Owner, DateTimeOffset ObservedAt)> _trackingObservations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sessionRevisions = new(StringComparer.Ordinal);
    private string? _observationError;
    private long _selectedRevision;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised when session supervision availability changes.</summary>
    public event EventHandler? Changed;
    /// <summary>Raised when a supervised session's health observations change.</summary>
    public event EventHandler? HealthChanged;
    /// <summary>Raised for a watchdog notification, with its originating session.</summary>
    public event Action<string, string, string>? NotificationRequested;
    /// <summary>Most recent discovery failure, if any.</summary>
    public string? Error => _observationError ??
        _workers.Values.Select(worker => worker.Failure).FirstOrDefault(failure => failure is not null) ??
        ObservationError();

    private string? ObservationError()
    {
        var names = _failedSessions.Concat(_workers.Values
                .Where(worker => worker.Initialized && !worker.IsFresh)
                .Select(worker => worker.Generation.Name))
            .Distinct(StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? null : UiText.Get("Automation_SessionObservationFailed",
            "Could not safely observe these sessions: {0}", string.Join(", ", names));
    }

    private long SessionRevision(string name)
    {
        if (!_sessionRevisions.TryGetValue(name, out var revision))
            _sessionRevisions[name] = revision = 0;
        return revision;
    }

    /// <summary>Creates a coordinator without starting any independent engine pollers.</summary>
#if WSLCD_TEST_NO_DISPATCHER
    public SessionAutomationCoordinatorHarness(StatusMonitor monitor, ISettingsService settings,
#else
    public SessionAutomationCoordinator(StatusMonitor monitor, ISettingsService settings,
#endif
        WslcSessionContext selection, WslcSessionClientFactory clients, IWslcService discovery,
        RestartSuppressionState suppression, LegacyPolicyOwnershipStore owners, ILoggerFactory logs,
        ComposeProjectSupervisor compose, HealthWatchdog selectedHealth, SessionRemovalGuard? removalGuard = null)
    {
        _monitor = monitor;
        _settings = settings;
        _selection = selection;
        _clients = clients;
        _discovery = discovery;
        _suppression = suppression;
        _owners = owners;
        _logs = logs;
        _compose = compose;
        _selectedHealth = selectedHealth;
        _removalGuard = removalGuard;
    }

    /// <summary>Starts consuming the status monitor's session observations.</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _monitor.AutomationSessionsChanged += OnSessionsChanged;
        _monitor.StatusChanged += OnSelectedStatusChanged;
        _selection.SelectionChanged += OnSelectionChanged;
        _selection.EngineSettingChanged += OnEngineChanged;
#if !WSLCD_TEST_NO_DISPATCHER
        if (_monitor.Latest is { } latest)
            OnSelectedStatusChanged(_monitor, latest);
#endif
        _monitor.RequestRefresh();
        _monitor.RequestAutomationRefresh();
    }

    /// <summary>Whether an owner currently has a valid observation.</summary>
    public bool IsObserved(AutomationSessionOwner owner) =>
        string.Equals(owner.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase) &&
        (_workers.Values.Any(worker => worker.Initialized && worker.IsFresh &&
            AutostartStateStore.SameOwner(worker.Owner, owner)) ||
         _trackingObservations.Values.Any(item => AutostartStateStore.SameOwner(item.Owner, owner) &&
             DateTimeOffset.UtcNow - item.ObservedAt <= AutomationSessionScopePlanner.ObservationMaxAge));

    /// <summary>Whether a fresh, successful observation is being initialized for this owner.</summary>
    public bool HasFreshObservationInProgress(AutomationSessionOwner owner) =>
        string.Equals(owner.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase) &&
        _workers.Values.Any(worker => worker.Initializing && worker.Failure is null &&
            worker.PendingContainers is not null && !_failedSessions.Contains(worker.Generation.Name) &&
            DateTimeOffset.UtcNow - worker.LastObservedAt <= AutomationSessionScopePlanner.ObservationMaxAge &&
            AutostartStateStore.SameOwner(worker.Owner, owner));

    /// <summary>Returns health only for the currently observed session generation.</summary>
    public HealthSnapshot? GetHealth(WslcSessionInfo generation, string executable) =>
        _workers.TryGetValue(generation.Name, out var worker) && worker.Initialized && worker.IsFresh &&
        worker.Matches(generation, executable) ? worker.Health.Latest : null;

    /// <summary>Returns the last safe container observation for an available owner.</summary>
    public IReadOnlyList<ContainerInfo>? GetContainers(AutomationSessionOwner owner) =>
        _workers.Values.FirstOrDefault(worker => worker.Initialized && worker.IsFresh &&
            AutostartStateStore.SameOwner(worker.Owner, owner))?.Health.ObservedContainers;

    /// <summary>Rechecks a newly assigned project's session before resuming its supervision.</summary>
    public void RequestReconciliation(AutomationSessionOwner owner)
    {
        foreach (var name in _workers.Where(pair => AutostartStateStore.SameOwner(pair.Value.Owner, owner))
                     .Select(pair => pair.Key).ToArray())
            RemoveWorker(name);
        UpdateSelectedHealth();
        _monitor.RequestRefresh();
        _monitor.RequestAutomationRefresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SuspendForRemoval(AutomationSessionOwner owner)
    {
        foreach (var name in _trackingObservations.Where(pair => AutostartStateStore.SameOwner(pair.Value.Owner, owner))
                     .Select(pair => pair.Key).ToArray())
            _trackingObservations.Remove(name);
        foreach (var name in _workers.Where(pair => AutostartStateStore.SameOwner(pair.Value.Owner, owner))
                     .Select(pair => pair.Key).ToArray())
        {
            RemoveWorker(name);
            _sessionRevisions[name] = SessionRevision(name) + 1;
        }
        UpdateSelectedHealth();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gets a Compose scope without changing the app's management session.</summary>
    public async Task<ComposeSessionScope> GetComposeScopeAsync(WslcSessionSelection selection, CancellationToken ct = default)
    {
        if (!_started || _settings.HasLoadError)
            throw new InvalidOperationException(UiText.Get("Automation_EngineUnavailable",
                "The engine or its settings are unavailable."));
        if (selection.Generation is not { } generation)
            throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                "The session changed or is no longer available; refresh before taking action."));
        await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath,
            generation, selection.ExecutablePath, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var selected = selection.Target == _selection.Capture();
        var observationLease = selected ? null : _monitor.ObserveSessionForOperation(generation, selection.ExecutablePath);
        var requestedAt = selected ? _selectedRevision : SessionRevision(generation.Name);
        var requested = false;
        try
        {
            while (true)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();
                Changed += OnChanged;
                try
                {
                    if (_workers.TryGetValue(generation.Name, out var worker) &&
                        worker.Matches(generation, selection.ExecutablePath))
                    {
                        if (_failedSessions.Contains(generation.Name))
                            throw new InvalidOperationException(UiText.Get("Automation_ScopeUnavailable",
                                "This session is not ready for Compose. Check the session and try again."));
                        if (worker.Failure is not null && !worker.Initializing)
                            throw new InvalidOperationException(worker.Failure);
                        if (worker.Initialized && worker.IsFresh)
                        {
                            await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath,
                                generation, selection.ExecutablePath, timeout.Token);
                            return new(selection, worker.Client, worker.Context, worker.Compose, worker.Token, observationLease);
                        }
                        if (!worker.Available && !worker.Initializing && requested &&
                            (selected ? _selectedRevision : SessionRevision(generation.Name)) != requestedAt)
                            throw new InvalidOperationException(UiText.Get("Automation_ScopeUnavailable",
                                "This session is not ready for Compose. Check the session and try again."));
                    }
                    else if (requested && (selected ? _selectedRevision : SessionRevision(generation.Name)) != requestedAt)
                        throw new InvalidOperationException(UiText.Get("Automation_ScopeUnavailable",
                            "This session is not ready for Compose. Check the session and try again."));
                    if (!requested)
                    {
                        requested = true;
                        if (selected) _monitor.RequestRefresh();
                        else _monitor.RequestAutomationRefresh();
                    }
                    await changed.Task.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    throw new TimeoutException(UiText.Get("Automation_ScopeUnavailable",
                        "This session is not ready for Compose. Check the session and try again."));
                }
                finally { Changed -= OnChanged; }
            }
        }
        catch { observationLease?.Dispose(); throw; }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_monitor.Dispatcher.HasThreadAccess)
        {
            if (!_monitor.Dispatcher.TryEnqueue(() => OnSelectionChanged(sender, e)))
                _logs.CreateLogger("SessionAutomationCoordinator").LogWarning("Could not update session supervision after selection changed.");
            return;
        }
        UpdateSelectedHealth();
        _monitor.RequestRefresh();
        _monitor.RequestAutomationRefresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEngineChanged(object? sender, EventArgs e)
    {
        if (!_monitor.Dispatcher.HasThreadAccess)
        {
            if (!_monitor.Dispatcher.TryEnqueue(() => OnEngineChanged(sender, e)))
                _logs.CreateLogger("SessionAutomationCoordinator").LogWarning("Could not clear session supervision after the engine changed.");
            return;
        }
        ClearWorkers();
        _failedSessions.Clear();
        _supervisedSessions = null;
        _trackingObservations.Clear();
        _sessionRevisions.Clear();
        _observationError = null;
        UpdateSelectedHealth();
        _monitor.RequestAutomationRefresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectedStatusChanged(object? sender, EngineStatusSnapshot snapshot)
    {
        if (_disposed || snapshot.Health == EngineHealth.Unknown)
            return;
        _selectedRevision++;
        if (snapshot.Health is not (EngineHealth.Healthy or EngineHealth.Degraded) || snapshot.Session is null ||
            !string.Equals(snapshot.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
        {
            var selected = _selection.Capture();
            if (string.Equals(snapshot.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase) &&
                snapshot.Health == EngineHealth.Down)
                _removalGuard?.CompleteResumeObservation(
                    AutomationSessionOwner.Capture(snapshot.ExecutablePath, selected), snapshot.ObservedAt);
            foreach (var worker in _workers.Values.Where(item => item.Owner.Matches(_settings.WslcPath, selected)))
                worker.Pause();
            UpdateSelectedHealth();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var owner = AutomationSessionOwner.Capture(snapshot.ExecutablePath, _selection.Capture());
        if (_removalGuard?.IsBlocked(owner, snapshot.Session, snapshot.ObservedAt) == true)
        {
            SuspendForRemoval(owner);
            return;
        }
        try
        {
            UpsertWorker(new(snapshot.Session, new(snapshot.Containers), ObservedAt: snapshot.ObservedAt), snapshot.ExecutablePath);
            _failedSessions.Remove(snapshot.Session.Name);
        }
        catch (InvalidOperationException ex)
        {
            _observationError = ex.Message;
            ClearWorkers();
        }
        UpdateSelectedHealth();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionsChanged(object? sender, WslcAutomationInventory batch)
    {
        if (_disposed)
            return;
        if (!string.Equals(batch.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            return;
        _observationError = batch.Error;
        if (batch.Error is not null)
        {
            _trackingObservations.Clear();
            var selected = _selection.Capture();
            foreach (var worker in _workers.Values.Where(item => !item.Owner.Matches(_settings.WslcPath, selected)))
                worker.Pause();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        string defaultName;
        try { defaultName = WslcSessionActionGuard.ResolveDefaultSessionName(); }
        catch (InvalidOperationException ex)
        {
            _observationError = ex.Message;
            ClearWorkers();
            _trackingObservations.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var selectedName = _selection.SelectedSessionName ?? defaultName;
        if (batch.SupervisedSessionNames is { } supervised)
        {
            if (!string.Equals(selectedName, batch.SelectedSessionName, StringComparison.Ordinal))
                return;
            _supervisedSessions = supervised.ToHashSet(StringComparer.Ordinal);
            foreach (var name in _workers.Keys.Where(name =>
                         !string.Equals(name, selectedName, StringComparison.Ordinal) &&
                         !_supervisedSessions.Contains(name)).ToArray())
                RemoveWorker(name);
            if (batch.ActiveSessionNames is { } active)
            {
                var activeNames = active.ToHashSet(StringComparer.Ordinal);
                foreach (var name in _trackingObservations.Keys.Where(name =>
                             !activeNames.Contains(name) || _supervisedSessions.Contains(name)).ToArray())
                    _trackingObservations.Remove(name);
            }
        }
        if (_supervisedSessions is { } required)
        {
            foreach (var item in batch.Sessions.Where(item => !required.Contains(item.Session.Name)))
            {
                _trackingObservations.Remove(item.Session.Name);
                var at = item.ObservedAt ?? DateTimeOffset.UtcNow;
                if (IsExpectedRemoval(item, batch.ExecutablePath, defaultName))
                    continue;
                if (!item.IsComplete || item.Session.CreatorPid <= 0 ||
                    DateTimeOffset.UtcNow - at > AutomationSessionScopePlanner.ObservationMaxAge)
                {
                    _failedSessions.Add(item.Session.Name);
                    continue;
                }
                var target = string.Equals(item.Session.Name, defaultName, StringComparison.Ordinal)
                    ? WslcSessionTarget.Default : new WslcSessionTarget(item.Session.Name);
                var owner = AutomationSessionOwner.Capture(batch.ExecutablePath, target);
                _trackingObservations[item.Session.Name] = (owner, at);
                _removalGuard?.CompleteResumeObservation(owner, at);
                _failedSessions.Remove(item.Session.Name);
            }
            batch = batch with { Sessions = batch.Sessions.Where(item => required.Contains(item.Session.Name)).ToArray() };
        }

        if (batch.IsPartial)
        {
            ApplyPartialBatch(batch, defaultName, selectedName);
            return;
        }

        var unavailable = batch.Sessions.Where(item => (!item.IsComplete || item.Session.CreatorPid <= 0) &&
                !IsExpectedRemoval(item, batch.ExecutablePath, defaultName))
            .Select(item => item.Session.Name).ToArray();
        if (unavailable.Length > 0)
            _observationError = UiText.Get("Automation_SessionObservationFailed",
                "Could not safely observe these sessions: {0}", string.Join(", ", unavailable));
        var available = batch.Sessions.Where(item => item.IsComplete && item.Session.CreatorPid > 0 &&
            !string.Equals(item.Session.Name, selectedName, StringComparison.Ordinal))
            .ToDictionary(item => item.Session.Name, StringComparer.Ordinal);
        foreach (var name in _workers.Keys.ToArray())
        {
            if (string.Equals(name, selectedName, StringComparison.Ordinal) ||
                !string.Equals(batch.SelectedSessionName, selectedName, StringComparison.Ordinal))
                continue;
            if (!available.TryGetValue(name, out var item))
                _workers[name].Pause();
            else if (!_workers[name].Matches(item.Session, batch.ExecutablePath))
                RemoveWorker(name);
        }
        foreach (var item in available.Values)
            try { UpsertWorker(item, batch.ExecutablePath); }
            catch (InvalidOperationException ex)
            {
                _observationError = ex.Message;
                ClearWorkers();
                break;
            }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPartialBatch(WslcAutomationInventory batch, string defaultName, string selectedName)
    {
        if (batch.ActiveSessionNames is { } active &&
            string.Equals(batch.SelectedSessionName, selectedName, StringComparison.Ordinal))
        {
            var names = active.ToHashSet(StringComparer.Ordinal);
            foreach (var name in _workers.Keys.Where(name =>
                         !string.Equals(name, selectedName, StringComparison.Ordinal) && !names.Contains(name)).ToArray())
                _workers[name].Pause();
            _failedSessions.RemoveWhere(name => !names.Contains(name));
            foreach (var name in _sessionRevisions.Keys.Where(name =>
                         !string.Equals(name, selectedName, StringComparison.Ordinal) && !names.Contains(name)).ToArray())
                _sessionRevisions[name]++;
        }
        foreach (var item in batch.Sessions)
        {
            var name = item.Session.Name;
            _sessionRevisions[name] = SessionRevision(name) + 1;
            if (string.Equals(name, selectedName, StringComparison.Ordinal))
                continue;
            if (IsExpectedRemoval(item, batch.ExecutablePath, defaultName))
            {
                if (_workers.TryGetValue(name, out var removed))
                    removed.Pause();
                continue;
            }
            if (!item.IsComplete || item.Session.CreatorPid <= 0 ||
                item.ObservedAt is { } at && DateTimeOffset.UtcNow - at > AutomationSessionScopePlanner.ObservationMaxAge)
            {
                _failedSessions.Add(name);
                if (_workers.TryGetValue(name, out var failed))
                    failed.Pause();
                continue;
            }
            _failedSessions.Remove(name);
            try { UpsertWorker(item, batch.ExecutablePath); }
            catch (InvalidOperationException ex)
            {
                _observationError = ex.Message;
                ClearWorkers();
                break;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsExpectedRemoval(WslcSessionContainerInventory item, string executable, string defaultName)
    {
        if (_removalGuard is null)
            return false;
        var target = string.Equals(item.Session.Name, defaultName, StringComparison.Ordinal)
            ? WslcSessionTarget.Default : new WslcSessionTarget(item.Session.Name);
        var owner = AutomationSessionOwner.Capture(executable, target);
        var observedAt = item.ObservedAt ?? DateTimeOffset.UtcNow;
        if (!item.IsComplete)
            _removalGuard.CompleteResumeObservation(owner, observedAt);
        return _removalGuard.IsExpectedRemovalObservation(owner, item.Session, observedAt) ||
            _removalGuard.IsExpectedStopObservation(owner, observedAt);
    }

    private void UpsertWorker(WslcSessionContainerInventory item, string executable)
    {
        var name = item.Session.Name;
        var target = string.Equals(name, WslcSessionActionGuard.ResolveDefaultSessionName(), StringComparison.Ordinal)
            ? WslcSessionTarget.Default : new WslcSessionTarget(name);
        var owner = AutomationSessionOwner.Capture(executable, target);
        if (_removalGuard?.IsBlocked(owner, item.Session, item.ObservedAt ?? DateTimeOffset.UtcNow) == true)
        {
            SuspendForRemoval(owner);
            return;
        }
        if (_workers.TryGetValue(name, out var existing) && !existing.Matches(item.Session, executable))
            RemoveWorker(name);
        if (!_workers.TryGetValue(name, out var worker))
        {
                var scope = new WslcSessionContext();
                scope.Select(target.Name);
                var client = _clients.CreateGuarded(item.Session, executable, managed: true);
                var restart = new RestartPolicyWatchdog(client, _monitor, _settings,
                    _logs.CreateLogger<RestartPolicyWatchdog>(), _suppression, scope, _owners,
                    item.Session, executable, _discovery);
                var health = new ScopedHealthWatchdog(client, _monitor, _settings,
                    _logs.CreateLogger<ScopedHealthWatchdog>(), restart, scope, _owners,
                    scopedGeneration: item.Session, scopedExecutable: executable, discovery: _discovery);
                var compose = _compose.ForSession(client, scope, health, () => health.ObservedContainers);
                var label = target.Name is null ? UiText.Get("WslEngine_Session_Default", "Default CLI session") : name;
                restart.NotificationRequested += (title, message) => NotificationRequested?.Invoke(label, title, message);
                health.NotificationRequested += (title, message) => NotificationRequested?.Invoke(label, title, message);
                health.HealthChanged += (_, snapshot) =>
                {
                    if (owner.Matches(_settings.WslcPath, _selection.Capture()))
                        _selectedHealth.ApplyMirroredSnapshot(snapshot);
                    HealthChanged?.Invoke(this, EventArgs.Empty);
                };
                worker = new Worker(owner, item.Session, client, scope, restart, health, compose);
                _workers.Add(name, worker);
        }
        worker.PendingContainers = item.Containers.Items!;
        worker.LastObservedAt = item.ObservedAt ?? DateTimeOffset.UtcNow;
        if (worker.Initialized && worker.Available)
        {
            worker.Restart.UpdateScopedObservation(worker.PendingContainers, worker.LastObservedAt);
            worker.Health.UpdateScopedObservation(worker.PendingContainers, worker.LastObservedAt);
            _removalGuard?.CompleteResumeObservation(owner, worker.LastObservedAt);
        }
        else if (!worker.Initializing)
            _ = InitializeWorkerAsync(name, worker);
    }

    private async Task InitializeWorkerAsync(string name, Worker worker)
    {
        worker.Initializing = true;
        string? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(worker.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await worker.Compose.ReconcileAsync(timeout.Token).ConfigureAwait(false);
            if (worker.Compose.ReconciliationWarnings.Count > 0)
                failure = string.Join("; ", worker.Compose.ReconciliationWarnings);
        }
        catch (OperationCanceledException) when (worker.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException or
                                   UnauthorizedAccessException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logs.CreateLogger("SessionAutomationCoordinator").LogWarning(ex,
                "Could not reconcile automatic actions for session {Session}.", name);
            failure = ex.Message;
        }
        if (!_monitor.Dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !_workers.TryGetValue(name, out var current) || !ReferenceEquals(current, worker))
                return;
            worker.Initializing = false;
            _removalGuard?.CompleteResumeObservation(worker.Owner, worker.LastObservedAt);
            worker.Failure = failure is null ? null : UiText.Get("Automation_ComposeSessionFailure",
                "Compose recovery for {0} needs attention: {1}", name, failure);
            if (failure is null && worker.PendingContainers is { } containers &&
                DateTimeOffset.UtcNow - worker.LastObservedAt <= AutomationSessionScopePlanner.ObservationMaxAge)
            {
                worker.Initialized = true;
                worker.Available = true;
                worker.Restart.UpdateScopedObservation(containers, worker.LastObservedAt);
                worker.Health.UpdateScopedObservation(containers, worker.LastObservedAt);
                worker.Restart.Start();
                worker.Health.Start();
                UpdateSelectedHealth();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }))
            _logs.CreateLogger("SessionAutomationCoordinator").LogWarning("Could not publish session recovery status.");
    }

    private void ClearWorkers()
    {
        foreach (var worker in _workers.Values)
            worker.Dispose();
        _workers.Clear();
    }

    private void RemoveWorker(string name)
    {
        if (_workers.Remove(name, out var worker))
            worker.Dispose();
    }

    private void UpdateSelectedHealth()
    {
        var selected = _selection.Capture();
        var worker = _workers.Values.FirstOrDefault(item =>
            item.Initialized && item.IsFresh && item.Owner.Matches(_settings.WslcPath, selected));
        _selectedHealth.ApplyMirroredSnapshot(worker?.Health.Latest ?? new HealthSnapshot());
        HealthChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops all scoped watchdogs and detaches from the shared poller.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _monitor.AutomationSessionsChanged -= OnSessionsChanged;
        _monitor.StatusChanged -= OnSelectedStatusChanged;
        _selection.SelectionChanged -= OnSelectionChanged;
        _selection.EngineSettingChanged -= OnEngineChanged;
        ClearWorkers();
    }

    private sealed class Worker(
        AutomationSessionOwner owner, WslcSessionInfo generation,
        IWslcService client, WslcSessionContext context, RestartPolicyWatchdog restart,
        ScopedHealthWatchdog health, ComposeProjectSupervisor compose) : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public AutomationSessionOwner Owner { get; } = owner;
        public WslcSessionInfo Generation { get; } = generation;
        public IWslcService Client { get; } = client;
        public WslcSessionContext Context { get; } = context;
        public RestartPolicyWatchdog Restart { get; } = restart;
        public ScopedHealthWatchdog Health { get; } = health;
        public ComposeProjectSupervisor Compose { get; } = compose;
        public CancellationToken Token => _stop.Token;
        public bool Initializing { get; set; }
        public bool Initialized { get; set; }
        public bool Available { get; set; }
        public DateTimeOffset LastObservedAt { get; set; }
        public bool IsFresh => Available &&
            DateTimeOffset.UtcNow - LastObservedAt <= AutomationSessionScopePlanner.ObservationMaxAge;
        public IReadOnlyList<ContainerInfo>? PendingContainers { get; set; }
        public string? Failure { get; set; }
        public bool Matches(WslcSessionInfo current, string executable) =>
            Generation.ID == current.ID && Generation.CreatorPid == current.CreatorPid &&
            string.Equals(Generation.Name, current.Name, StringComparison.Ordinal) &&
            string.Equals(Owner.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase);
        public void Pause()
        {
            Available = false;
            Initialized = false;
            PendingContainers = null;
            Health.PauseScopedObservation();
            Restart.PauseScopedObservation();
        }
        public void Dispose()
        {
            _stop.Cancel();
            Health.Dispose();
            Restart.Dispose();
        }
    }
}
