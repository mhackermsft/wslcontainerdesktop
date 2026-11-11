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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.Services;

/// <summary>Snapshot of engine/container health broadcast to the tray and view models.</summary>
public sealed class EngineStatusSnapshot
{
    /// <summary>The selected default session was intentionally stopped in this app.</summary>
    public bool DefaultSessionStoppedByUser { get; init; }
    /// <summary>When this observation was captured, before delivery to the UI.</summary>
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Session generation represented by a healthy inventory.</summary>
    public WslcSessionInfo? Session { get; init; }
    /// <summary>Executable used to obtain this inventory.</summary>
    public string ExecutablePath { get; init; } = string.Empty;
    /// <summary>Overall engine health classification.</summary>
    public EngineHealth Health { get; init; }
    /// <summary>Number of containers currently reported as running.</summary>
    public int RunningCount { get; init; }
    /// <summary>Total number of containers in the latest inventory.</summary>
    public int TotalCount { get; init; }
    /// <summary>Container inventory captured with the snapshot.</summary>
    public IReadOnlyList<ContainerInfo> Containers { get; init; } = Array.Empty<ContainerInfo>();
    /// <summary>Short user-facing summary for status surfaces.</summary>
    public string Summary { get; init; } = string.Empty;
}

/// <summary>Snapshot of Kubernetes (k3s) health for the nav footer indicator.</summary>
public sealed class K8sStatusSnapshot
{
    /// <summary>Current k3s state.</summary>
    public ClusterState State { get; init; } = ClusterState.Unknown;
    /// <summary>Number of pods reported as running.</summary>
    public int PodsRunning { get; init; }
    /// <summary>Total number of pods reported by the cluster.</summary>
    public int PodsTotal { get; init; }
    /// <summary>Short user-facing cluster summary.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Whether the cluster is installed (footer indicator is hidden otherwise).</summary>
    public bool IsInstalled => State is ClusterState.Stopped or ClusterState.Running or ClusterState.Starting;
}

/// <summary>
/// Periodically polls the WSL container engine and raises <see cref="StatusChanged"/>
/// on the UI thread. Acts as the single source of truth for container health so the
/// tray icon and the Containers page do not poll independently.
/// </summary>
public sealed class StatusMonitor : IDisposable, IHealthObservationSource
{
    private readonly IWslcService _wslc;
    private readonly ProcessRunner _runner;
    private readonly WslcSessionContext _sessionContext;
    private readonly IKubernetesService _k8s;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly INotificationService _notifications;
    private readonly IWslRequirementService _requirements;
    private readonly IEngineEventStream _events;
    private readonly IWindowVisibility _visibility;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<StatusMonitor> _logger;
    private readonly WslcSessionInventoryReader _sessionReader;
    private readonly LegacyPolicyOwnershipStore _policyOwners;
    private readonly IComposeProjectStore _composeStore;
    private readonly ConcurrentDictionary<(int Id, int CreatorPid, string Name), NativeHealthMonitor> _sessionHealth = [];
    private readonly object _automationDemandGate = new();
    private readonly Dictionary<(string Executable, string Name), int> _automationDemand = [];
    private readonly NativeHealthMonitor _nativeHealth = new();
    private int _startupRotation;
    private string? _nativeHealthExecutable;
    private WslcSessionInfo? _nativeHealthGeneration;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private DateTimeOffset _lastAzureRefresh = DateTimeOffset.MinValue;

    // Serializes container polls so the loop and any RequestRefresh don't run the
    // read-modify-write of Latest + transition detection concurrently.
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _automationGate = new(1, 1);
    private readonly object _automationSweepGate = new();
    private CancellationTokenSource? _automationSweep;
    private bool _automationRefreshRequested;
    private int _automationRotation;

    // Keeps the Kubernetes footer refresh from overlapping when the window becomes viewable mid-poll.
    private readonly SemaphoreSlim _k8sGate = new(1, 1);

    // Container IDs the app itself just stopped/restarted/killed, so the resulting
    // running->stopped transition is not surfaced as an "exited" toast. Entries are
    // consumed on detection and expire if the stop never lands (e.g. failed command).
    private readonly ConcurrentDictionary<string, DateTimeOffset> _selfInitiatedStops = new(StringComparer.Ordinal);
    private static readonly TimeSpan SelfInitiatedStopTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EventRefreshDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitNotificationThrottle = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPollExitNotifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _eventRefreshGate = new();
    private CancellationTokenSource? _eventRefreshCts;

    /// <summary>Raised on the UI thread when engine health or inventory changes.</summary>
    public event EventHandler<EngineStatusSnapshot>? StatusChanged;
    /// <summary>Raised after a bounded observation of the other active sessions.</summary>
    public event EventHandler<WslcAutomationInventory>? AutomationSessionsChanged;
    /// <summary>Raised on the UI thread when k3s health changes.</summary>
    public event EventHandler<K8sStatusSnapshot>? K8sStatusChanged;

    /// <summary>Most recent engine snapshot, or null before the first poll finishes.</summary>
    public EngineStatusSnapshot? Latest { get; private set; }
    private HealthObservationSnapshot? _healthObservations;
    /// <summary>Returns the latest bounded health observation snapshot used by watchdogs.</summary>
    public HealthObservationSnapshot? GetSnapshot() => Volatile.Read(ref _healthObservations);

    /// <summary>Most recent Kubernetes snapshot, or null before the first poll finishes.</summary>
    public K8sStatusSnapshot? LatestK8s { get; private set; }

    /// <summary>UI dispatcher captured when the monitor is first resolved.</summary>
    public DispatcherQueue Dispatcher => _dispatcher;

    /// <summary>Creates the monitor and captures the current UI dispatcher for event delivery.</summary>
    public StatusMonitor(IWslcService wslc, ProcessRunner runner, WslcSessionContext sessionContext,
        IKubernetesService k8s, RegistryAuthRefresher authRefresher, ISettingsService settings,
        INotificationService notifications, IWslRequirementService requirements, IEngineEventStream events,
        IWindowVisibility visibility, DispatcherQueue dispatcher, ILogger<StatusMonitor> logger,
        WslcSessionInventoryReader sessionReader, LegacyPolicyOwnershipStore policyOwners,
        IComposeProjectStore composeStore)
    {
        _wslc = wslc;
        _runner = runner;
        _sessionContext = sessionContext;
        _k8s = k8s;
        _authRefresher = authRefresher;
        _settings = settings;
        _notifications = notifications;
        _requirements = requirements;
        _events = events;
        _visibility = visibility;
        _dispatcher = dispatcher;
        _logger = logger;
        _sessionReader = sessionReader;
        _policyOwners = policyOwners;
        _composeStore = composeStore;
        _requirements.Changed += OnRequirementChanged;
        _events.EventReceived += OnEngineEventReceived;
        _visibility.Changed += OnVisibilityChanged;
        _sessionContext.EngineSettingChanged += OnEngineSettingChanged;
        _sessionContext.DefaultSessionStopChanged += OnDefaultSessionStopChanged;
    }

    /// <summary>Starts polling and event-stream listening; safe to call once during app launch.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>Forces an immediate refresh outside the normal cadence.</summary>
    public void RequestRefresh()
    {
        _nativeHealth.Invalidate();
        _nativeHealthGeneration = null;
        _ = Task.Run(PollOnceAsync);
    }

    /// <summary>Requests a fresh bounded observation of other active sessions.</summary>
    public void RequestAutomationRefresh()
    {
        var ct = _cts?.Token;
        if (ct is { } token && !token.IsCancellationRequested)
        {
            lock (_automationSweepGate)
            {
                _automationRefreshRequested = true;
                _automationSweep?.Cancel();
            }
            _ = Task.Run(() => PollAutomationSessionsAsync(token));
        }
    }

    internal IDisposable ObserveSessionForOperation(WslcSessionInfo session, string executable)
    {
        var key = (executable, session.Name);
        lock (_automationDemandGate)
            _automationDemand[key] = _automationDemand.GetValueOrDefault(key) + 1;
        return new AutomationDemandLease(this, key);
    }

    private sealed class AutomationDemandLease(StatusMonitor monitor, (string Executable, string Name) key) : IDisposable
    {
        private StatusMonitor? _monitor = monitor;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _monitor, null);
            if (owner is null)
                return;
            lock (owner._automationDemandGate)
            {
                var count = owner._automationDemand[key];
                if (count == 1) owner._automationDemand.Remove(key);
                else owner._automationDemand[key] = count - 1;
            }
        }
    }

    /// <summary>Clears the previous session's observations before requesting the new session.</summary>
    public void SessionSelectionChanged()
        => ClearSessionObservations(UiText.Get("WslcSession_StatusSwitching", "Engine: switching session…"), requestRefresh: true);

    private void OnDefaultSessionStopChanged(object? sender, EventArgs e)
    {
        var target = _sessionContext.Capture();
        if (target.Name is null ||
            string.Equals(target.Name, WslcSessionActionGuard.ResolveDefaultSessionName(), StringComparison.Ordinal))
        {
            var stopped = _sessionContext.IsDefaultSessionStopped(_settings.WslcPath, target);
            ClearSessionObservations(stopped
                ? UiText.Get("ManagedSession_DefaultStatus",
                    "Default CLI session paused in this app. Start or resume it on the WSL engine page.")
                : UiText.Get("WslcSession_StatusSwitching", "Engine: switching session…"),
                requestRefresh: true, defaultStoppedByUser: stopped);
        }
        RequestAutomationRefresh();
    }

    internal async Task WaitForCurrentPollAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        _pollGate.Release();
    }

    private void OnEngineSettingChanged(object? sender, EventArgs e)
        => ClearSessionObservations(UiText.Get("WslcSession_EngineChanged", "Engine: configuration changed…"), requestRefresh: false);

    private void ClearSessionObservations(string summary, bool requestRefresh, bool defaultStoppedByUser = false)
    {
        var selection = _sessionContext.CaptureSnapshot();
        _sessionContext.TryApply(selection, () =>
        {
            Latest = null;
            Volatile.Write(ref _healthObservations, null);
            _nativeHealth.Invalidate();
            _selfInitiatedStops.Clear();
            _lastPollExitNotifications.Clear();
        });
        var pending = new EngineStatusSnapshot
        {
            ExecutablePath = _settings.WslcPath,
            Health = EngineHealth.Unknown,
            Summary = summary,
            DefaultSessionStoppedByUser = defaultStoppedByUser,
        };
        void PublishPending() => _sessionContext.TryApply(selection, () => StatusChanged?.Invoke(this, pending));
        if (_dispatcher.HasThreadAccess)
            PublishPending();
        else
            _dispatcher.TryEnqueue(PublishPending);
        if (requestRefresh)
            RequestRefresh();
    }

    /// <summary>
    /// Marks a container stop as app-initiated so the next running->stopped transition for
    /// it does not raise a "Container stopped" toast. Call before stopping/restarting/killing.
    /// </summary>
    public void SuppressExitNotification(string containerId)
    {
        if (string.IsNullOrEmpty(containerId))
        {
            return;
        }

        // Drop stale entries left behind by stops that never landed (e.g. failed commands).
        var cutoff = DateTimeOffset.UtcNow - SelfInitiatedStopTtl;
        foreach (var kvp in _selfInitiatedStops)
        {
            if (kvp.Value < cutoff)
            {
                _selfInitiatedStops.TryRemove(kvp.Key, out _);
            }
        }

        _selfInitiatedStops[containerId] = DateTimeOffset.UtcNow;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The Kubernetes status only feeds visible UI, so skip it while nobody can see the window.
            // It never starts a WSL distribution either way (issue #126).
            var k8sPoll = _visibility.IsViewable ? PollK8sOnceAsync() : Task.CompletedTask;
            _ = PollAutomationSessionsAsync(ct);
            await Task.WhenAll(PollOnceAsync(), k8sPoll).ConfigureAwait(false);

            // Keep Azure-backed registry tokens fresh in the background so pulls/runs keep
            // working. Runs on a slow cadence since ACR tokens last a few hours.
            await MaybeRefreshAzureTokensAsync().ConfigureAwait(false);

            var interval = Math.Clamp(_settings.RefreshIntervalSeconds,
                AppConstants.RefreshIntervalMinSeconds, AppConstants.RefreshIntervalMaxSeconds);
            if (_settings.HealthChecks.Any(policy => policy.Enabled && policy.IsValid) ||
                _settings.RestartPolicies.Any(policy => policy.Enabled && policy.IsValid) ||
                _composeStore.GetAll().Any(project => !project.ImportedOnly))
                interval = Math.Min(interval, (int)AutomationSessionScopePlanner.ObservationMaxAge.TotalSeconds / 2);
            if (_events.IsConnected)
            {
                interval = Math.Max(interval, Math.Min(30, interval * 3));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAutomationSessionsAsync(CancellationToken ct)
    {
        if (!_requirements.HasCompletedInitialCheck)
            return;
        if (_requirements.Current.State != WslRequirementState.Ok || _settings.HasLoadError)
        {
            PublishAutomationSessions(new(_settings.WslcPath, [], UiText.Get("Automation_EngineUnavailable",
                "The engine or its settings are unavailable.")));
            return;
        }
        try { if (!await _automationGate.WaitAsync(0, ct).ConfigureAwait(false)) return; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        using var sweep = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_automationSweepGate)
        {
            _automationSweep = sweep;
            _automationRefreshRequested = false;
        }
        try { await PollAutomationSessionsCoreAsync(sweep.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (sweep.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure observing automatic actions in other sessions.");
            PublishAutomationSessions(new(_settings.WslcPath, [], UiText.Get("Automation_EngineUnavailable",
                "The engine or its settings are unavailable.")));
        }
        finally
        {
            bool refresh;
            lock (_automationSweepGate)
            {
                _automationSweep = null;
                refresh = _automationRefreshRequested;
            }
            _automationGate.Release();
            if (refresh && !ct.IsCancellationRequested)
                _ = Task.Run(() => PollAutomationSessionsAsync(ct));
        }
    }

    private async Task PollAutomationSessionsCoreAsync(CancellationToken ct)
    {
        var executable = _settings.WslcPath;
        try
        {
            var discovered = await _sessionReader.DiscoverAsync(ct).ConfigureAwait(false);
            var discoveredAt = DateTimeOffset.UtcNow;
            var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
            var selectedName = _sessionContext.Capture().Name ?? defaultName;
            AutomationSessionOwner[] requested;
            lock (_automationDemandGate)
                requested = _automationDemand.Keys.Select(key =>
                    AutomationSessionOwner.Capture(key.Executable, new WslcSessionTarget(key.Name))).ToArray();
            var owners = _settings.HealthChecks.Where(policy => policy.Enabled && policy.IsValid)
                .Select(_policyOwners.OwnerFor)
                .Concat(_settings.RestartPolicies.Where(policy => policy.Enabled && policy.IsValid).Select(_policyOwners.OwnerFor))
                .Concat(_composeStore.GetAll().Where(project => !project.ImportedOnly).Select(project => project.SessionOwner))
                .Concat(requested).ToArray();
            var supervised = AutomationSessionScopePlanner.Select(discovered, defaultName, selectedName,
                executable, trackAll: false, owners).ToHashSet();
            var requestedNames = requested.Where(owner =>
                    string.Equals(owner.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
                .Select(owner => owner.SessionName ?? defaultName).ToHashSet(StringComparer.Ordinal);
            var planned = AutomationSessionScopePlanner.Select(discovered, defaultName, selectedName,
                executable, _settings.RestartRunningContainersOnLaunch, owners);
            var offset = planned.Count == 0 ? 0 : _automationRotation % planned.Count;
            _automationRotation = planned.Count == 0 ? 0 : (offset + 2) % planned.Count;
            var others = planned.Skip(offset).Concat(planned.Take(offset))
                .OrderByDescending(session => requestedNames.Contains(session.Name)).ToArray();
            var activeKeys = supervised.Select(session => (session.ID, session.CreatorPid, session.Name)).ToHashSet();
            foreach (var key in _sessionHealth.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
                _sessionHealth.TryRemove(key, out _);
            PublishAutomationSessions(new(executable, [], SelectedSessionName: selectedName,
                IsPartial: true, ActiveSessionNames: others.Select(session => session.Name).ToArray(),
                SupervisedSessionNames: supervised.Select(session => session.Name).ToArray(),
                DiscoveredSessions: discovered, DiscoveredAt: discoveredAt));
            async Task TrackStartupOnlyAsync()
            {
                var sessions = planned.Where(session => !supervised.Contains(session)).ToArray();
                if (sessions.Length == 0) return;
                var result = await AutomationSessionObservationScheduler.RunBatchedAsync(sessions, _startupRotation,
                    async (batch, stop) =>
                    {
                        var observations = await _sessionReader.ReadContainersAsync(batch, discovered, executable, stop,
                            sessionTimeout: TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        if (!string.Equals(executable, _settings.WslcPath, StringComparison.OrdinalIgnoreCase) ||
                            _requirements.Current.State != WslRequirementState.Ok || _settings.HasLoadError)
                            return;
                        PublishAutomationSessions(new(executable, observations,
                            SelectedSessionName: selectedName, IsPartial: true));
                    }, ct).ConfigureAwait(false);
                _startupRotation = result.NextOffset;
                if (result.TimedOut)
                    _logger.LogDebug("Startup-history observation budget expired; remaining sessions will rotate into the next sweep.");
            }
            var supervisedSessions = planned.Where(supervised.Contains)
                .OrderByDescending(session => requestedNames.Contains(session.Name)).ToArray();
            if (supervisedSessions.Length > 0)
            {
                var batchCount = (supervisedSessions.Length + 1) / 2;
                var sessionReadTimeout = TimeSpan.FromSeconds(Math.Clamp(20d / batchCount, 1.5d, 20d));
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var observations = await _sessionReader.ReadContainersAsync(supervisedSessions, discovered,
                        executable, budget.Token, sessionReadTimeout).ConfigureAwait(false);
                    await AutomationSessionObservationScheduler.RunAsync(observations, async (observation, stop) =>
                    {
                        var current = observation;
                        var session = observation.Session;
                        try
                        {
                            if (current.IsComplete)
                            {
                                var key = (session.ID, session.CreatorPid, session.Name);
                                var health = _sessionHealth.GetOrAdd(key, _ => new NativeHealthMonitor());
                                var runner = new ProcessRunner(_settings, fixedSession: new(session.Name),
                                    fixedExecutablePath: executable);
                                await health.RefreshAsync(current.Containers.Items!,
                                    (id, token) => WslcService.InspectContainerWithRunnerAsync(runner, id, token),
                                    detail => _logger.LogDebug("Session {Session} native health: {Detail}", session.Name, detail),
                                    stop).ConfigureAwait(false);
                            }
                            if (!string.Equals(executable, _settings.WslcPath, StringComparison.OrdinalIgnoreCase) ||
                                _requirements.Current.State != WslRequirementState.Ok || _settings.HasLoadError)
                                return;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or
                                                   ArgumentException or System.Text.Json.JsonException or
                                                   System.ComponentModel.Win32Exception or TimeoutException)
                        {
                            _logger.LogWarning(ex, "Could not observe automatic actions in session {Session}.", session.Name);
                            current = new(session, new(null, Error: ex.Message), ObservedAt: observation.ObservedAt);
                        }
                        PublishAutomationSessions(new(executable, [current], SelectedSessionName: selectedName, IsPartial: true));
                    }, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
                {
                    _logger.LogDebug("Automatic-action observation budget expired before every session was refreshed.");
                }
            }
            await TrackStartupOnlyAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or
                                   ArgumentException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not observe automatic actions in other sessions.");
            PublishAutomationSessions(new(executable, [], ex.Message));
        }
    }

    private void PublishAutomationSessions(WslcAutomationInventory snapshot)
    {
        if (!_dispatcher.TryEnqueue(() => AutomationSessionsChanged?.Invoke(this, snapshot)))
            _logger.LogDebug("The UI dispatcher could not receive session automation observations.");
    }

    private async Task PollK8sOnceAsync()
    {
        if (!await _k8sGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            K8sStatusSnapshot snapshot;
            try
            {
                var status = await _k8s.GetFooterStatusAsync().ConfigureAwait(false);
                snapshot = status.State switch
                {
                    ClusterState.Working => new K8sStatusSnapshot
                    {
                        State = ClusterState.Working,
                        Summary = "Kubernetes: operation in progress",
                    },
                    ClusterState.Running => new K8sStatusSnapshot
                    {
                        State = ClusterState.Running,
                        PodsRunning = status.PodsRunning,
                        PodsTotal = status.PodsTotal,
                        Summary = $"Kubernetes: running · {status.PodsRunning}/{status.PodsTotal} pods up",
                    },
                    ClusterState.Starting => new K8sStatusSnapshot
                    {
                        State = ClusterState.Starting,
                        Summary = "Kubernetes: starting",
                    },
                    ClusterState.Stopped => new K8sStatusSnapshot
                    {
                        State = ClusterState.Stopped,
                        Summary = status.DistroStopped
                            ? $"Kubernetes: not running ({status.Distro} is stopped)"
                            : status.NotKeptRunning
                                ? "Kubernetes: not kept running"
                                : "Kubernetes: stopped",
                    },
                    ClusterState.NotInstalled => new K8sStatusSnapshot
                    {
                        State = ClusterState.NotInstalled,
                        Summary = "Kubernetes: not installed",
                    },
                    ClusterState.NoDistribution => new K8sStatusSnapshot
                    {
                        State = ClusterState.NoDistribution,
                        Summary = "Kubernetes: no usable WSL distribution",
                    },
                    _ => new K8sStatusSnapshot { State = ClusterState.Unknown, Summary = "Kubernetes: unknown" },
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kubernetes footer status poll failed.");
                snapshot = new K8sStatusSnapshot { State = ClusterState.Unknown, Summary = "Kubernetes: unknown" };
            }

            LatestK8s = snapshot;
            _dispatcher.TryEnqueue(() => K8sStatusChanged?.Invoke(this, snapshot));
        }
        finally
        {
            _k8sGate.Release();
        }
    }

    private void OnVisibilityChanged(object? sender, EventArgs e)
    {
        // Show a current Kubernetes status as soon as the window can be seen again.
        if (_visibility.IsViewable && !_disposed)
        {
            _ = Task.Run(PollK8sOnceAsync);
        }
    }

    /// <summary>
    /// Every ~30 minutes, silently re-mints ACR tokens for Azure-backed registries using the
    /// cached az session and re-logs the engine in, so long-running sessions keep working.
    /// No-ops if there are no Azure registries or the az session has expired.
    /// </summary>
    private async Task MaybeRefreshAzureTokensAsync()
    {
        var azureRegistries = _settings.Registries.Where(r => r.IsAzure && r.HasHost).ToList();
        if (azureRegistries.Count == 0)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastAzureRefresh < AppConstants.AzureTokenRefreshInterval)
        {
            return;
        }

        _lastAzureRefresh = DateTimeOffset.UtcNow;

        try
        {
            foreach (var r in azureRegistries)
            {
                await _authRefresher.RefreshAsync(r).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // background best-effort; ignore
            _logger.LogDebug(ex, "Background Azure token refresh failed.");
        }
    }

    private async Task PollOnceAsync()
    {
        await _pollGate.WaitAsync().ConfigureAwait(false);
        try
        {
        EngineStatusSnapshot snapshot;
        var selection = _sessionContext.CaptureSnapshot();
        var scopedRunner = _runner.ForSession(selection.Target);
        var executablePath = scopedRunner.CaptureExecutablePath();
        var observedAt = DateTimeOffset.UtcNow;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? default);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        var requirement = _requirements.Current;
        if (!_requirements.HasCompletedInitialCheck)
        {
            return;
        }

        if (requirement.State != WslRequirementState.Ok)
        {
            snapshot = new EngineStatusSnapshot
            {
                ExecutablePath = executablePath,
                ObservedAt = observedAt,
                Health = EngineHealth.Down,
                Summary = requirement.State switch
                {
                    WslRequirementState.DisabledByPolicy => "Engine: disabled by policy",
                    WslRequirementState.NotInstalled => "Engine: wslc not found",
                    WslRequirementState.TooOld => $"Engine: WSL {WslcRequirements.MinimumVersionDisplay}+ required",
                    _ => "Engine: requirement check failed",
                },
            };

            if (_sessionContext.TryApply(selection, () =>
                {
                    var previousGated = Latest;
                    Latest = snapshot;
                    Volatile.Write(ref _healthObservations, new HealthObservationSnapshot(
                        executablePath, observedAt, false, []));
                    DetectAndNotifyTransitions(previousGated, snapshot, executablePath, selection.Target);
                }))
            {
                _dispatcher.TryEnqueue(() =>
                    _sessionContext.TryApply(selection, () => StatusChanged?.Invoke(this, snapshot)));
            }
            return;
        }

        if (_sessionContext.IsDefaultSessionStopped(executablePath, selection.Target))
        {
            snapshot = new EngineStatusSnapshot
            {
                ExecutablePath = executablePath,
                ObservedAt = observedAt,
                Health = EngineHealth.Unknown,
                DefaultSessionStoppedByUser = true,
                Summary = UiText.Get("ManagedSession_DefaultStatus",
                    "Default CLI session paused in this app. Start or resume it on the WSL engine page."),
            };
            if (_sessionContext.TryApply(selection, () =>
                {
                    Latest = snapshot;
                    Volatile.Write(ref _healthObservations, new HealthObservationSnapshot(
                        executablePath, observedAt, false, []));
                }))
                _dispatcher.TryEnqueue(() =>
                    _sessionContext.TryApply(selection, () => StatusChanged?.Invoke(this, snapshot)));
            return;
        }

        try
        {
            var engineUp = await _wslc.IsEngineAvailableAsync(budget.Token).ConfigureAwait(false);
            if (!engineUp)
            {
                snapshot = new EngineStatusSnapshot
                {
                    ExecutablePath = executablePath,
                    ObservedAt = observedAt,
                    Health = EngineHealth.Down,
                    Summary = "Engine: unreachable",
                };
            }
            else
            {
                var name = selection.Target.Name ?? WslcSessionActionGuard.ResolveDefaultSessionName();
                var before = (await _sessionReader.DiscoverAsync(budget.Token).ConfigureAwait(false))
                    .SingleOrDefault(session => string.Equals(session.Name, name, StringComparison.Ordinal));
                if (selection.Target.Name is not null && before is null)
                    throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                        "The session changed or is no longer available; refresh before taking action."));
                var containers = await _wslc.ListContainersForSessionAsync(
                    selection.Target, executablePath, all: true, ct: budget.Token).ConfigureAwait(false);
                var generation = (await _sessionReader.DiscoverAsync(budget.Token).ConfigureAwait(false))
                    .SingleOrDefault(session => string.Equals(session.Name, name, StringComparison.Ordinal));
                if (generation is null || generation.ID <= 0 || generation.CreatorPid <= 0 ||
                    before is not null && before != generation)
                    throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                        "The session changed or is no longer available; refresh before taking action."));
                if (!string.Equals(_nativeHealthExecutable, executablePath, StringComparison.OrdinalIgnoreCase) ||
                    _nativeHealthGeneration != generation)
                {
                    _nativeHealthExecutable = executablePath;
                    _nativeHealthGeneration = generation;
                    _nativeHealth.Invalidate();
                }
                await _nativeHealth.RefreshAsync(containers,
                    (id, token) => WslcService.InspectContainerWithRunnerAsync(scopedRunner, id, token),
                    detail => _logger.LogDebug("Native health: {Detail}", detail), budget.Token).ConfigureAwait(false);
                await WslcSessionActionGuard.VerifyAsync(_wslc, () => _settings.WslcPath,
                    generation, executablePath, budget.Token).ConfigureAwait(false);
                var running = containers.Count(c => c.State == ContainerState.Running);
                var total = containers.Count;

                snapshot = new EngineStatusSnapshot
                {
                    Health = EngineHealth.Healthy,
                    ObservedAt = observedAt,
                    Session = generation,
                    ExecutablePath = executablePath,
                    RunningCount = running,
                    TotalCount = total,
                    Containers = containers,
                    Summary = $"Engine: running · {running}/{total} containers up",
                };
            }
        }
        catch (Exception ex)
        {
            snapshot = new EngineStatusSnapshot
            {
                ExecutablePath = executablePath,
                ObservedAt = observedAt,
                Health = EngineHealth.Down,
                Summary = $"Engine: error ({ex.Message})",
            };
        }

        if (_sessionContext.TryApply(selection, () =>
            {
                var previous = Latest;
                Latest = snapshot;
                Volatile.Write(ref _healthObservations, new HealthObservationSnapshot(executablePath, observedAt,
                    snapshot.Health is EngineHealth.Healthy or EngineHealth.Degraded &&
                        string.Equals(executablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase),
                    snapshot.Containers.Select(c => new HealthObservationRow(c.Id, c.Name, c.State,
                        c.NativeHealth.State, c.NativeHealth.ObservedAt, c.StateChangedAt)).ToArray()));
                DetectAndNotifyTransitions(previous, snapshot, executablePath, selection.Target);
            }))
        {
            _dispatcher.TryEnqueue(() =>
                _sessionContext.TryApply(selection, () => StatusChanged?.Invoke(this, snapshot)));
        }
        else
        {
            _nativeHealth.Invalidate();
        }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private void OnRequirementChanged(object? sender, WslRequirementStatus e)
    {
        RequestRefresh();
        RequestAutomationRefresh();
    }

    private void OnEngineEventReceived(object? sender, EngineEvent e)
    {
        if (e.IsType("container") || e.IsType("network"))
        {
            RequestEventRefresh();
        }
    }

    private void RequestEventRefresh()
    {
        CancellationTokenSource cts;
        lock (_eventRefreshGate)
        {
            _eventRefreshCts?.Cancel();
            cts = new CancellationTokenSource();
            _eventRefreshCts = cts;
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EventRefreshDebounce, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    RequestRefresh();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_eventRefreshGate)
                {
                    if (ReferenceEquals(_eventRefreshCts, cts))
                    {
                        _eventRefreshCts = null;
                    }
                }

                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Events carry full 64-character IDs while inventory reports 12-character short IDs, so
    /// both are keyed by the short-ID prefix.
    /// </summary>
    private static string ExitNotificationKey(string id) => id.Length > 12 ? id[..12] : id;

    /// <summary>
    /// Compares the prior snapshot to the new one and emits toasts for engine up/down
    /// transitions and for containers that stopped running. Skipped on the first poll
    /// (<paramref name="previous"/> is null) so launch doesn't spam notifications.
    /// </summary>
    private void DetectAndNotifyTransitions(EngineStatusSnapshot? previous, EngineStatusSnapshot current,
        string executablePath, WslcSessionTarget target)
    {
        if (previous is null)
        {
            return;
        }

        var wasUp = previous.Health is EngineHealth.Healthy or EngineHealth.Degraded;
        var isUp = current.Health is EngineHealth.Healthy or EngineHealth.Degraded;

        if (wasUp && current.Health == EngineHealth.Down)
        {
            _notifications.NotifyEngineDown();
        }
        else if (previous.Health == EngineHealth.Down && isUp)
        {
            _notifications.NotifyEngineRecovered();
        }

        // Only compare container states while the engine stays up; a down/up bounce would
        // otherwise report every container as "stopped".
        if (!wasUp || !isUp)
        {
            return;
        }

        var stillPresent = current.Containers.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var was in previous.Containers)
        {
            if (was.State != ContainerState.Running)
            {
                continue;
            }

            // Notify only if the container still exists but is no longer running; a removed
            // container (missing from the new list) was almost certainly user-initiated.
            if (stillPresent.TryGetValue(was.Id, out var now) && now.State != ContainerState.Running)
            {
                // Skip stops the app itself initiated (Stop/Restart/Kill from UI or tray).
                if (_selfInitiatedStops.TryRemove(was.Id, out var when)
                    && DateTimeOffset.UtcNow - when <= SelfInitiatedStopTtl)
                {
                    continue;
                }

                // The live event stream may already have reported this exit.
                if (_lastPollExitNotifications.TryGetValue(ExitNotificationKey(was.Id), out var notified)
                    && DateTimeOffset.UtcNow - notified < ExitNotificationThrottle)
                {
                    continue;
                }

                _lastPollExitNotifications[ExitNotificationKey(was.Id)] = DateTimeOffset.UtcNow;
                _notifications.NotifyContainerExited(string.IsNullOrWhiteSpace(now.Name) ? now.ShortId : now.Name, now.Id,
                    AutomationSessionOwner.Capture(executablePath, target));
            }
        }
    }

    /// <summary>Stops polling and releases timer/event resources.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
        _requirements.Changed -= OnRequirementChanged;
        _events.EventReceived -= OnEngineEventReceived;
        _visibility.Changed -= OnVisibilityChanged;
        _sessionContext.EngineSettingChanged -= OnEngineSettingChanged;
        _sessionContext.DefaultSessionStopChanged -= OnDefaultSessionStopChanged;
        lock (_eventRefreshGate)
        {
            _eventRefreshCts?.Cancel();
            _eventRefreshCts = null;
        }
        _disposed = true;
        _pollGate.Dispose();
    }
}
