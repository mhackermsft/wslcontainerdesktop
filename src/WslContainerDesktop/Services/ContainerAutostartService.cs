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

namespace WslContainerDesktop.Services;

/// <summary>Restores only explicitly enrolled, session-qualified automatic-start history.</summary>
public sealed class ContainerAutostartService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(45);
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly ILogger<ContainerAutostartService> _logger;
    private readonly WslcSessionContext _sessionContext;
    private readonly AutostartStateStore _store;
    private readonly RestartSuppressionState _suppression;
    private readonly SessionRemovalGuard? _removalGuard;
    private readonly Func<AutomationSessionOwner, IWslcService> _createClient;
    private readonly Func<CancellationToken, Task<IReadOnlyList<WslcSessionInfo>>> _discoverSessions;
    private readonly Func<WslcSessionInfo, string, CancellationToken, Task> _verifySession;
    private readonly TimeSpan _retryInterval;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private readonly List<AutostartEntry> _pending = [];
    private readonly List<AutostartProblem> _problems = [];
    private readonly CancellationTokenSource _shutdown = new();
    private DateTimeOffset _lastRetry = DateTimeOffset.MinValue;
    private int _statusRetryScheduled;
    private bool _tracking;
    private bool _disposed;

    /// <summary>Reasons automatic start is still waiting for the person or the engine.</summary>
    public enum ProblemKind { EngineChanged, SessionUnavailable, EngineUnavailable, ContainerMissing, NotStartable, StartFailed, StorageFailed }

    /// <summary>A session and the reason its automatic-start work is incomplete.</summary>
    public sealed record AutostartProblem(AutomationSessionOwner Owner, ProblemKind Kind, string? Detail = null);

    /// <summary>Raised when the pending-problem list changes.</summary>
    public event EventHandler? ProblemsChanged;

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates a coordinator using immutable session-bound engine clients.</summary>
    public ContainerAutostartService(IWslcService discovery, StatusMonitor monitor, ISettingsService settings,
        ILogger<ContainerAutostartService> logger, WslcSessionContext sessionContext,
        WslcSessionClientFactory clients, WslcSessionInventoryReader reader, AutostartStateStore store,
        RestartSuppressionState suppression, SessionRemovalGuard removalGuard)
        : this(discovery, monitor, settings, logger, sessionContext, store,
            owner => clients.Create(new WslcSessionTarget(owner.SessionName), owner.ExecutablePath, managed: false),
            reader.DiscoverAsync,
            (session, executable, ct) => WslcSessionActionGuard.VerifyAsync(
                discovery, () => settings.WslcPath, session, executable, ct), suppression: suppression,
            removalGuard: removalGuard)
    {
    }
#endif

    internal ContainerAutostartService(IWslcService discovery, StatusMonitor monitor, ISettingsService settings,
        ILogger<ContainerAutostartService> logger, WslcSessionContext sessionContext, AutostartStateStore store,
        Func<AutomationSessionOwner, IWslcService> createClient,
        Func<CancellationToken, Task<IReadOnlyList<WslcSessionInfo>>> discoverSessions,
        Func<WslcSessionInfo, string, CancellationToken, Task> verifySession,
        TimeSpan? retryInterval = null, RestartSuppressionState? suppression = null,
        SessionRemovalGuard? removalGuard = null)
    {
        _monitor = monitor;
        _settings = settings;
        _logger = logger;
        _sessionContext = sessionContext;
        _store = store;
        _suppression = suppression ?? new RestartSuppressionState();
        _removalGuard = removalGuard;
        _suppression.Suppressed += OnManualStop;
        _createClient = createClient;
        _discoverSessions = discoverSessions;
        _verifySession = verifySession;
        _retryInterval = retryInterval ?? RetryInterval;
    }

    /// <summary>Returns a stable list of unresolved automatic-start problems.</summary>
    public IReadOnlyList<AutostartProblem> Problems
    {
        get { lock (_gate) return _problems.ToArray(); }
    }

    /// <summary>Loads pending work, begins tracking, and attempts only assigned sessions.</summary>
    public async Task RestoreThenAttachAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_settings.HasLoadError && !_store.HasLoadError && _store.Snapshot().Count == 0 &&
                !string.IsNullOrWhiteSpace(_settings.WslcPath))
            {
                _store.EnableTracking(AutomationSessionOwner.Capture(_settings.WslcPath, WslcSessionTarget.Default));
            }
            lock (_gate)
            {
                _pending.Clear();
                _pending.AddRange(_store.Snapshot().Where(entry => !entry.TracksSession && entry.SessionOwner?.IsValid == true));
            }
            Attach();
            await RetryPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Application shutdown cancels outstanding startup work.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not initialize automatic-start history.");
            ReportStorageFailure(ex);
            Attach();
        }
    }

    /// <summary>Retries a legacy entry after its owner has been durably assigned.</summary>
    public async Task ResumeAssignedAsync(AutostartEntry entry, AutomationSessionOwner owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            var assigned = entry with { SessionOwner = owner };
            if (!_pending.Any(existing => SameEntry(existing, assigned)))
                _pending.Add(assigned);
        }
        await RetryPendingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Retries pending assigned entries without touching unassigned history.</summary>
    public async Task RetryPendingAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _restoreGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
                _lastRetry = DateTimeOffset.UtcNow;
            if (_settings.HasLoadError || !_settings.RestartRunningContainersOnLaunch)
                return;
            List<AutomationSessionOwner> owners = [];
            lock (_gate)
            {
                foreach (var owner in _pending.Select(entry => entry.SessionOwner).Where(owner => owner?.IsValid == true))
                    if (!owners.Any(existing => AutostartStateStore.SameOwner(existing, owner)))
                        owners.Add(owner!);
            }

            using var concurrency = new SemaphoreSlim(2);
            await Task.WhenAll(owners.Select(async owner =>
            {
                await concurrency.WaitAsync(linked.Token).ConfigureAwait(false);
                try { await RestoreOwnerAsync(owner, linked.Token).ConfigureAwait(false); }
                finally { concurrency.Release(); }
            })).ConfigureAwait(false);
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    /// <summary>Explicitly forgets one owner's automatic-start history, without changing its data.</summary>
    public async Task ForgetOwnerAsync(AutomationSessionOwner owner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _removalGuard?.Begin(owner, null);
        try { await QuiesceAndForgetOwnerAsync(owner, ct).ConfigureAwait(false); }
        finally { _removalGuard?.Complete(owner, null); }
    }

    internal async Task QuiesceAndForgetOwnerAsync(AutomationSessionOwner owner, CancellationToken ct = default)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool changed;
            lock (_gate)
            {
                _store.ForgetOwner(owner);
                _pending.RemoveAll(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner));
                changed = _problems.RemoveAll(problem => AutostartStateStore.SameOwner(problem.Owner, owner)) > 0;
            }
            if (changed) ProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _restoreGate.Release(); }
    }

    internal async Task WaitForCurrentRestoreAsync(CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        _restoreGate.Release();
    }

    internal async Task PauseOwnerAsync(AutomationSessionOwner owner, CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.HasLoadError || _settings.HasLoadError)
                return;
            lock (_gate)
                foreach (var entry in _store.Snapshot().Where(entry => !entry.TracksSession &&
                             AutostartStateStore.SameOwner(entry.SessionOwner, owner)))
                    if (!_pending.Any(existing => SameEntry(existing, entry)))
                        _pending.Add(entry);
        }
        finally { _restoreGate.Release(); }
    }

    internal async Task ResumeOwnerAsync(AutomationSessionOwner owner, CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
                if (!_pending.Any(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner)))
                    return;
            if (!_settings.HasLoadError && _settings.RestartRunningContainersOnLaunch &&
                !_store.HasLoadError)
                await RestoreOwnerAsync(owner, ct).ConfigureAwait(false);
        }
        finally { _restoreGate.Release(); }
    }

    private async Task RestoreOwnerAsync(AutomationSessionOwner owner, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            if (_removalGuard?.IsBlocked(owner, null, DateTimeOffset.UtcNow) == true)
                return;
            if (!string.Equals(owner.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            {
                SetProblem(owner, ProblemKind.EngineChanged);
                return;
            }
            WslcSessionInfo? generation = null;
            if (owner.SessionName is not null)
            {
                var sessions = await _discoverSessions(timeout.Token).ConfigureAwait(false);
                generation = sessions.SingleOrDefault(session =>
                    string.Equals(session.Name, owner.SessionName, StringComparison.Ordinal));
                if (generation is null || generation.ID <= 0 || generation.CreatorPid <= 0)
                {
                    SetProblem(owner, ProblemKind.SessionUnavailable);
                    return;
                }
                await _verifySession(generation, owner.ExecutablePath, timeout.Token).ConfigureAwait(false);
            }

            var client = _createClient(owner);
            if (!await client.IsEngineAvailableAsync(timeout.Token).ConfigureAwait(false))
            {
                SetProblem(owner, ProblemKind.EngineUnavailable);
                return;
            }
            var containers = await client.ListContainersAsync(all: true, timeout.Token).ConfigureAwait(false);
            if (containers.Any(container => string.IsNullOrWhiteSpace(container.Id) || string.IsNullOrWhiteSpace(container.Name)) ||
                containers.Select(container => container.Id).Distinct(StringComparer.Ordinal).Count() != containers.Count)
            {
                SetProblem(owner, ProblemKind.StartFailed, UiText.Get("Automation_AmbiguousInventory",
                    "The container inventory is incomplete or ambiguous. Refresh before retrying."));
                return;
            }
            var byId = containers.ToDictionary(container => container.Id, StringComparer.Ordinal);
            List<AutostartEntry> entries;
            lock (_gate)
                entries = _pending.Where(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner)).ToList();
            var missing = new List<string>();
            var notStartable = new List<string>();
            foreach (var entry in entries)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_removalGuard?.IsBlocked(owner, generation, DateTimeOffset.UtcNow) == true)
                    return;
                var container = ResolveContainer(byId, entry);
                if (container is null)
                {
                    missing.Add(string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name);
                    continue;
                }
                if (_suppression.IsSuppressed(container.Name, owner))
                {
                    _store.ForgetContainer(owner, container.Name, entry.Id);
                    RemovePending(entry);
                    continue;
                }
                if (container.State == ContainerState.Running)
                {
                    RemovePending(entry);
                    continue;
                }
                if (container.State is not (ContainerState.Created or ContainerState.Stopped))
                {
                    notStartable.Add(string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name);
                    continue;
                }

                using var selection = _sessionContext.LockSelection();
                if (!string.Equals(owner.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
                {
                    SetProblem(owner, ProblemKind.EngineChanged);
                    return;
                }
                if (generation is not null)
                    await _verifySession(generation, owner.ExecutablePath, timeout.Token).ConfigureAwait(false);
                if (_removalGuard?.IsBlocked(owner, generation, DateTimeOffset.UtcNow) == true)
                    return;
                if (_suppression.IsSuppressed(container.Name, owner))
                {
                    _store.ForgetContainer(owner, container.Name, entry.Id);
                    RemovePending(entry);
                    continue;
                }
                var result = await client.StartContainerAsync(container.Id, timeout.Token, explicitStart: false).ConfigureAwait(false);
                if (!result.Success)
                {
                    SetProblem(owner, ProblemKind.StartFailed, result.ErrorText);
                    return;
                }
                _monitor.RequestRefresh();
            }
            if (missing.Count > 0)
                SetProblem(owner, ProblemKind.ContainerMissing, string.Join(", ", missing));
            else if (notStartable.Count > 0)
                SetProblem(owner, ProblemKind.NotStartable, string.Join(", ", notStartable));
            else
                ClearProblem(owner);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            SetProblem(owner, ProblemKind.EngineUnavailable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Automatic start could not complete for session {Session}.", owner.SessionName);
            SetProblem(owner, ProblemKind.StartFailed, ex.Message);
        }
    }

    private void Attach()
    {
        if (_tracking || _disposed)
            return;
        _tracking = true;
        _monitor.StatusChanged += OnStatusChanged;
        _monitor.AutomationSessionsChanged += OnAutomationSessionsChanged;
    }

    internal void OnStatusChanged(object? sender, EngineStatusSnapshot snapshot)
    {
        if (_settings.HasLoadError)
            return;
        bool retry;
        lock (_gate)
            retry = _pending.Count > 0 && DateTimeOffset.UtcNow - _lastRetry >= _retryInterval;
        if (retry && Interlocked.Exchange(ref _statusRetryScheduled, 1) == 0)
            _ = RetryFromStatusAsync();

        if (snapshot.Health is not (EngineHealth.Healthy or EngineHealth.Degraded) ||
            string.IsNullOrWhiteSpace(_settings.WslcPath))
            return;
        var owner = AutomationSessionOwner.Capture(_settings.WslcPath, _sessionContext.Capture());
        TrackObservation(owner, snapshot.Containers, snapshot.Session, snapshot.ObservedAt);
    }

    internal void OnAutomationSessionsChanged(object? sender, WslcAutomationInventory batch)
    {
        if (_settings.HasLoadError || batch.Error is not null ||
            !string.Equals(batch.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            return;
        string defaultName;
        try { defaultName = WslcSessionActionGuard.ResolveDefaultSessionName(); }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not identify the default session for automatic-start tracking.");
            return;
        }
        foreach (var observation in batch.Sessions.Where(item => item.IsComplete && item.Session.CreatorPid > 0 &&
                     (item.ObservedAt is null ||
                      DateTimeOffset.UtcNow - item.ObservedAt <= AutomationSessionScopePlanner.ObservationMaxAge)))
        {
            var target = string.Equals(observation.Session.Name, defaultName, StringComparison.Ordinal)
                ? WslcSessionTarget.Default : new WslcSessionTarget(observation.Session.Name);
            TrackObservation(AutomationSessionOwner.Capture(batch.ExecutablePath, target), observation.Containers.Items!,
                observation.Session, observation.ObservedAt ?? DateTimeOffset.UtcNow);
        }
    }

    private void TrackObservation(AutomationSessionOwner owner, IReadOnlyList<ContainerInfo> containers,
        WslcSessionInfo? generation, DateTimeOffset observedAt)
    {
        if (!_settings.RestartRunningContainersOnLaunch)
            return;
        try
        {
            if (containers.Any(container => container is null ||
                string.IsNullOrWhiteSpace(container.Id) || string.IsNullOrWhiteSpace(container.Name)))
            {
                SetProblem(owner, ProblemKind.StartFailed, UiText.Get("Automation_AmbiguousInventory",
                    "The container inventory is incomplete or ambiguous. Refresh before retrying."));
                return;
            }
            lock (_gate)
            {
                if (_removalGuard?.IsBlocked(owner, generation, observedAt) == true)
                    return;
                if (!_store.Tracks(owner))
                    _store.EnableTracking(owner);
                var legacy = _store.Snapshot().Where(entry => !entry.TracksSession && entry.SessionOwner?.IsValid != true).ToArray();
                var running = containers.Where(container => container.State == ContainerState.Running &&
                        !_suppression.IsSuppressed(container.Name, owner))
                    .Select(container => new AutostartEntry
                    {
                        Id = container.Id, Name = container.Name.TrimStart('/'), SessionOwner = owner,
                    }).Where(entry => !legacy.Any(unassigned => SameContainer(entry, unassigned) ||
                        !string.IsNullOrWhiteSpace(unassigned.Name) &&
                        string.Equals(entry.Name, unassigned.Name, StringComparison.Ordinal)))
                    .OrderBy(entry => entry.Id, StringComparer.Ordinal).ToList();
                _pending.RemoveAll(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner) &&
                    !string.IsNullOrWhiteSpace(entry.Name) && _suppression.IsSuppressed(entry.Name, owner));
                foreach (var entry in _pending.Where(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner)))
                    if (!running.Any(current => SameContainer(entry, current)))
                        running.Add(entry);
                _store.ReplaceOwned(owner, running);
                _pending.RemoveAll(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner) &&
                    containers.Any(container => container.State == ContainerState.Running &&
                        SameContainer(entry, new AutostartEntry { Id = container.Id, Name = container.Name.TrimStart('/') })));
            }
            ClearStorageProblem(owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not save automatic-start history.");
            SetProblem(owner, ProblemKind.StorageFailed, ex.Message);
        }
    }

    private void OnManualStop(string name, AutomationSessionOwner? owner, string? id)
    {
        if (owner?.IsValid != true)
            return;
        try
        {
            lock (_gate)
            {
                _store.ForgetContainer(owner, name, id);
                _pending.RemoveAll(entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner) &&
                    (string.Equals(entry.Name.TrimStart('/'), name, StringComparison.Ordinal) ||
                     !string.IsNullOrWhiteSpace(id) && string.Equals(entry.Id, id, StringComparison.Ordinal)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not save manual-stop intent for session {Session}.", owner.SessionName);
            SetProblem(owner, ProblemKind.StorageFailed, ex.Message);
        }
    }

    private async Task RetryFromStatusAsync()
    {
        try { await RetryPendingAsync(_shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic-start retry failed.");
            ReportStorageFailure(ex);
        }
        finally
        {
            Volatile.Write(ref _statusRetryScheduled, 0);
        }
    }

    private void RemovePending(AutostartEntry entry)
    {
        lock (_gate)
            _pending.RemoveAll(current => SameEntry(current, entry));
    }

    private static bool SameEntry(AutostartEntry left, AutostartEntry right) =>
        AutostartStateStore.SameOwner(left.SessionOwner, right.SessionOwner) && SameContainer(left, right);

    private static bool SameContainer(AutostartEntry left, AutostartEntry right) =>
        ContainerIdentity.ResolveId([left.Id], right.Id) is not null ||
        string.IsNullOrWhiteSpace(left.Id) && string.IsNullOrWhiteSpace(right.Id) &&
        !string.IsNullOrWhiteSpace(left.Name) && string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private static ContainerInfo? ResolveContainer(IReadOnlyDictionary<string, ContainerInfo> byId, AutostartEntry entry)
    {
        var resolved = ContainerIdentity.ResolveId(byId.Keys, entry.Id);
        return resolved is not null && byId.TryGetValue(resolved, out var match) ? match : null;
    }

    private void SetProblem(AutomationSessionOwner owner, ProblemKind kind, string? detail = null)
    {
        bool changed;
        lock (_gate)
        {
            var storage = kind == ProblemKind.StorageFailed;
            var existing = _problems.FirstOrDefault(problem => AutostartStateStore.SameOwner(problem.Owner, owner) &&
                (problem.Kind == ProblemKind.StorageFailed) == storage);
            var next = new AutostartProblem(owner, kind, detail);
            changed = existing != next;
            if (changed)
            {
                _problems.RemoveAll(problem => AutostartStateStore.SameOwner(problem.Owner, owner) &&
                    (problem.Kind == ProblemKind.StorageFailed) == storage);
                _problems.Add(next);
            }
        }
        if (changed)
            ProblemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearProblem(AutomationSessionOwner owner)
    {
        int removed;
        lock (_gate)
            removed = _problems.RemoveAll(problem => AutostartStateStore.SameOwner(problem.Owner, owner) &&
                problem.Kind != ProblemKind.StorageFailed);
        if (removed > 0)
            ProblemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearStorageProblem(AutomationSessionOwner owner)
    {
        int removed;
        lock (_gate)
            removed = _problems.RemoveAll(problem => AutostartStateStore.SameOwner(problem.Owner, owner) &&
                problem.Kind == ProblemKind.StorageFailed);
        if (removed > 0)
            ProblemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReportStorageFailure(Exception ex)
    {
        if (!string.IsNullOrWhiteSpace(_settings.WslcPath))
            SetProblem(AutomationSessionOwner.Capture(_settings.WslcPath, WslcSessionTarget.Default),
                ProblemKind.StorageFailed, ex.Message);
    }

    /// <summary>Stops retries and detaches from the shared status monitor.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        _suppression.Suppressed -= OnManualStop;
        if (_tracking)
        {
            _monitor.StatusChanged -= OnStatusChanged;
            _monitor.AutomationSessionsChanged -= OnAutomationSessionsChanged;
        }
    }
}
