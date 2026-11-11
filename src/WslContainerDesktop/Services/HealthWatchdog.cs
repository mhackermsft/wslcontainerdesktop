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
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.Services;

/// <summary>Aggregate health of all watched containers, broadcast to the list and tray.</summary>
public sealed class HealthSnapshot
{
    /// <summary>
    /// Gets the per-container health observations in this snapshot.
    /// </summary>
    public IReadOnlyList<ContainerHealthSnapshot> Containers { get; init; } = Array.Empty<ContainerHealthSnapshot>();

    /// <summary>Worst state across all watched containers (used for the tray roll-up).</summary>
    public ContainerHealthState Worst { get; init; } = ContainerHealthState.Unknown;

    /// <summary>The worst state mapped onto the tray's engine-health glyph.</summary>
    public EngineHealth TrayHealth => Worst switch
    {
        ContainerHealthState.Down => EngineHealth.Down,
        ContainerHealthState.Degraded => EngineHealth.Degraded,
        ContainerHealthState.Healthy => EngineHealth.Healthy,
        _ => EngineHealth.Unknown,
    };
}

/// <summary>
/// Periodically evaluates a user-defined health probe (in-container command or host-side TCP
/// connect) per container and enforces a restart policy. Reuses <see cref="StatusMonitor"/> as
/// the single container-polling source, so it never polls the engine list independently.
/// </summary>
#if WSLCD_TEST_NO_DISPATCHER
public sealed class HealthWatchdogSessionHarness : IDisposable, IAppHealthObservationSource
#else
public sealed class HealthWatchdog : IDisposable, IAppHealthObservationSource
#endif
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly RestartPolicyWatchdog _restartWatchdog;
    private readonly WslcSessionContext _sessionContext;
    private readonly LegacyPolicyOwnershipStore _legacyOwners;
    private readonly Func<int, CancellationToken, Task<bool>> _tcpProbe;
    private readonly WslcSessionInfo? _scopedGeneration;
    private readonly string? _scopedExecutable;
    private readonly IWslcService? _discovery;
    private volatile bool _scopeAvailable;

    private readonly ConcurrentDictionary<(long Revision, string Name), Runtime> _runtime = new();

    private sealed record Observation(WslcSessionContext.SelectionSnapshot Selection, IReadOnlyList<ContainerInfo> Containers,
        DateTimeOffset ObservedAt);
    private volatile Observation? _observation;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised (on the UI thread) whenever a watched container's health changes.</summary>
    public event EventHandler<HealthSnapshot>? HealthChanged;

    /// <summary>Raised (on the UI thread) when an unhealthy transition should surface a toast.</summary>
    public event Action<string, string>? NotificationRequested;
#if WSLCD_TEST_NO_DISPATCHER
    internal event Action? EvaluationCompleted;
#endif

    /// <summary>
    /// Gets latest for callers in the service or view-model layer.
    /// </summary>
    public HealthSnapshot Latest { get; private set; } = new();
    /// <summary>
    /// Gets observations information for callers in the service or view-model layer.
    /// </summary>
    public IReadOnlyList<ContainerHealthSnapshot> GetObservations() => Latest.Containers;
    internal IReadOnlyList<ContainerInfo> ObservedContainers => _observation?.Containers ?? [];

    internal void UseAsMirror()
    {
        _sessionContext.SelectionChanged -= OnSessionContextChanged;
        _sessionContext.EngineSettingChanged -= OnSessionContextChanged;
    }

    internal void ApplyMirroredSnapshot(HealthSnapshot snapshot)
    {
        if (_disposed)
            return;
        Latest = snapshot;
        HealthChanged?.Invoke(this, snapshot);
    }

    /// <summary>
    /// Initializes a new <c>HealthWatchdog</c> with the collaborators it needs from dependency injection.
    /// </summary>
#if WSLCD_TEST_NO_DISPATCHER
    public HealthWatchdogSessionHarness(IWslcService wslc, StatusMonitor monitor, ISettingsService settings,
        ILogger<HealthWatchdogSessionHarness> logger, RestartPolicyWatchdog restartWatchdog, WslcSessionContext sessionContext,
        LegacyPolicyOwnershipStore legacyOwners,
        Func<int, CancellationToken, Task<bool>>? tcpProbe = null,
        WslcSessionInfo? scopedGeneration = null, string? scopedExecutable = null, IWslcService? discovery = null)
#else
    public HealthWatchdog(IWslcService wslc, StatusMonitor monitor, ISettingsService settings,
        ILogger<HealthWatchdog> logger, RestartPolicyWatchdog restartWatchdog, WslcSessionContext sessionContext,
        LegacyPolicyOwnershipStore legacyOwners,
        Func<int, CancellationToken, Task<bool>>? tcpProbe = null,
        WslcSessionInfo? scopedGeneration = null, string? scopedExecutable = null, IWslcService? discovery = null)
#endif
    {
        _wslc = wslc;
        _monitor = monitor;
        _settings = settings;
        _logger = logger;
        _dispatcher = monitor.Dispatcher;
        _restartWatchdog = restartWatchdog;
        _sessionContext = sessionContext;
        _legacyOwners = legacyOwners;
        _tcpProbe = tcpProbe ?? ProbeTcpAsync;
        _scopedGeneration = scopedGeneration;
        _scopedExecutable = scopedExecutable;
        _discovery = discovery;
        _sessionContext.SelectionChanged += OnSessionContextChanged;
        _sessionContext.EngineSettingChanged += OnSessionContextChanged;
    }

    /// <summary>
    /// Starts the background work requested by the UI or a supervisor.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // Reuse the status monitor's container list rather than polling the engine again.
        if (_scopedGeneration is null)
        {
            _monitor.StatusChanged += OnStatusChanged;
            if (_monitor.Latest is not null)
                _observation = new(_sessionContext.CaptureSnapshot(), _monitor.Latest.Containers, DateTimeOffset.UtcNow);
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    internal void UpdateScopedObservation(IReadOnlyList<ContainerInfo> containers, DateTimeOffset? observedAt = null)
    {
        if (_scopedGeneration is null)
            throw new InvalidOperationException("A scoped observation requires a session generation.");
        var selection = _sessionContext.CaptureSnapshot();
        _observation = new(selection, containers, observedAt ?? DateTimeOffset.UtcNow);
        _scopeAvailable = true;
        Publish(selection);
    }

    internal void PauseScopedObservation()
    {
        _scopeAvailable = false;
        _observation = null;
    }

    private async Task VerifyScopeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_scopedGeneration is null)
            return;
        if (!_scopeAvailable || _observation is not { } observation || DateTimeOffset.UtcNow - observation.ObservedAt >
            AutomationSessionScopePlanner.ObservationMaxAge)
            throw new InvalidOperationException(UiText.Get("Automation_ObservationStale",
                "The session's automatic-action observation is stale. No action was started."));
        if (_discovery is null || string.IsNullOrWhiteSpace(_scopedExecutable))
            throw new InvalidOperationException("The session scope is incomplete.");
        await WslcSessionActionGuard.VerifyAsync(_discovery, () => _settings.WslcPath,
            _scopedGeneration, _scopedExecutable, ct).ConfigureAwait(false);
        if (!_scopeAvailable)
            throw new InvalidOperationException(UiText.Get("Automation_ObservationStale",
                "The session's automatic-action observation is stale. No action was started."));
    }

    internal void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        var selection = _sessionContext.CaptureSnapshot();
        if (_observation is { } previous && previous.Selection != selection)
            _runtime.Clear();
        _observation = new(selection, e.Containers, DateTimeOffset.UtcNow);
        Publish(selection);
    }

    private void OnSessionContextChanged(object? sender, EventArgs e)
    {
        var selection = _sessionContext.CaptureSnapshot();
        _runtime.Clear();
        _observation = new(selection, Array.Empty<ContainerInfo>(), DateTimeOffset.UtcNow);
        Publish(selection);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health watchdog tick failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal void Tick(CancellationToken ct)
    {
        var observation = _observation;
        if (observation is null || observation.Selection != _sessionContext.CaptureSnapshot() ||
            _scopedGeneration is not null && DateTimeOffset.UtcNow - observation.ObservedAt >
                AutomationSessionScopePlanner.ObservationMaxAge)
            return;
        var configs = _settings.HealthChecks
            .Where(c => c.Enabled && c.IsValid && c.DesiredHealth?.IsDisabled != true &&
                _legacyOwners.OwnerFor(c)?.Matches(_settings.WslcPath, observation.Selection.Target) == true)
            .ToList();
        foreach (var container in observation.Containers)
        {
            var name = container.Name.TrimStart('/');
            if (!configs.Any(c => c.ContainerName == name) &&
                (container.NativeHealth.State is NativeHealthState.Starting or NativeHealthState.Healthy or NativeHealthState.Unhealthy ||
                 container.NativeHealth.State == NativeHealthState.Unknown &&
                 (container.NativeHealth.Configuration?.HasCommand == true ||
                  _runtime.ContainsKey((observation.Selection.Revision, name)))))
                configs.Add(new HealthCheckConfig
                {
                    ContainerName = name, MaxRestarts = 0,
                    SessionOwner = AutomationSessionOwner.Capture(_settings.WslcPath, observation.Selection.Target),
                    DesiredHealth = container.NativeHealth.Configuration, Command = "engine-owned",
                });
        }

        var active = new HashSet<string>(configs.Select(c => c.ContainerName), StringComparer.Ordinal);
        var changed = false;

        // Drop runtime state for containers that are no longer watched.
        foreach (var key in _runtime.Keys.ToList())
        {
            if ((key.Revision != observation.Selection.Revision || !active.Contains(key.Name)) &&
                _runtime.TryRemove(key, out _))
            {
                changed = true;
            }
        }

        var containers = observation.Containers;
        var now = DateTimeOffset.UtcNow;

        foreach (var cfg in configs)
        {
            var rt = _runtime.GetOrAdd((observation.Selection.Revision, cfg.ContainerName), _ => new Runtime());

            // A probe for this container is still in flight. Skip entirely — including the
            // not-running branch below — so we never mutate its Runtime state concurrently
            // with the running EvaluateAsync (e.g. during the transient not-running window
            // of a restart it triggered).
            if (rt.CheckInProgress)
            {
                continue;
            }
            var fingerprint = JsonSerializer.Serialize(cfg);
            rt.Kind = cfg.Kind;
            if (!string.Equals(rt.Configuration, fingerprint, StringComparison.Ordinal))
            {
                rt.Configuration = fingerprint;
                rt.Progress.Reset(now);
                rt.RestartCount = 0;
                rt.LastCheck = DateTimeOffset.MinValue;
                rt.LastNativeObservation = DateTimeOffset.MinValue;
                rt.State = ContainerHealthState.Unknown;
                changed = true;
            }

            var container = containers.FirstOrDefault(c =>
                string.Equals(c.Name.TrimStart('/'), cfg.ContainerName, StringComparison.Ordinal));

            // Not present or not running: the workload probe is meaningless. If we've spent the
            // restart budget, settle on Down (badge/tray stay red and we notify once); otherwise
            // treat it as Unknown until it runs again.
            if (container is null || container.State != ContainerState.Running)
            {
                var exhausted = cfg.MaxRestarts > 0 && rt.RestartCount >= cfg.MaxRestarts;
                if (exhausted)
                {
                    // The policy may have been removed (e.g. project teardown) after this cycle's
                    // config snapshot was taken. Don't announce a down state for a container we're
                    // no longer supervising.
                    if (rt.State != ContainerHealthState.Down && IsStillActive(cfg, observation.Selection))
                    {
                        rt.State = ContainerHealthState.Down;
                        rt.MaxRestarts = cfg.MaxRestarts;
                        rt.Detail = $"Down — not running after {cfg.MaxRestarts} restart attempt(s)";
                        changed = true;
                        Notify(UiText.Get("Automation_HealthDownTitle", "{0} is down", cfg.ContainerName),
                            UiText.Get("Automation_HealthDown", "Container is not running after {0} restart attempt(s).",
                                cfg.MaxRestarts), observation.Selection);
                    }
                }
                else if (rt.State != ContainerHealthState.Unknown)
                {
                    rt.State = ContainerHealthState.Unknown;
                    rt.Progress.Reset(now);
                    changed = true;
                }

                continue;
            }

            if (rt.ContainerId != container.Id || rt.StartedAt != container.StateChangedAt)
            {
                rt.State = ContainerHealthState.Unknown;
                rt.LastCheck = DateTimeOffset.MinValue;
                rt.ContainerId = container.Id;
                rt.StartedAt = container.StateChangedAt;
                rt.Progress.Reset(now);
                rt.LastNativeObservation = DateTimeOffset.MinValue;
            }

            TimeSpan interval;
            try
            {
                interval = ProbeInterval(cfg, rt, now);
            }
            catch (InvalidOperationException ex)
            {
                rt.State = ContainerHealthState.Unknown;
                rt.Detail = ex.Message;
                changed = true;
                continue;
            }
            if (cfg.Kind == HealthProbeKind.Command && container.NativeHealth.OwnsCommandProbe)
                interval = TimeSpan.FromSeconds(1);
            if (now - rt.LastCheck < interval)
            {
                continue;
            }

            rt.CheckInProgress = true;
            _ = EvaluateAsync(cfg, container, rt, observation.Selection, ct);
        }

        if (changed)
        {
            Publish(observation.Selection);
        }
    }

    private async Task EvaluateAsync(HealthCheckConfig cfg, ContainerInfo container, Runtime rt,
        WslcSessionContext.SelectionSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            if (snapshot != _sessionContext.CaptureSnapshot())
                return;
            rt.MaxRestarts = cfg.MaxRestarts;
            bool healthy;
            var native = cfg.Kind == HealthProbeKind.Command && container.NativeHealth.OwnsCommandProbe;
            rt.ObservationMaxAge = TimeSpan.FromSeconds(15);
            if (native)
            {
                var observation = container.NativeHealth;
                if (DateTimeOffset.UtcNow - observation.ObservedAt > TimeSpan.FromSeconds(15))
                {
                    rt.State = ContainerHealthState.Unknown;
                    rt.Detail = "Engine health observation is stale; awaiting a fresh status poll.";
                    rt.LastCheck = DateTimeOffset.UtcNow;
                    Publish(snapshot);
                    return;
                }
                if (observation.ObservedAt <= rt.LastNativeObservation)
                    return;
                rt.LastNativeObservation = observation.ObservedAt;
                if (observation.State is NativeHealthState.Unknown or NativeHealthState.Starting)
                {
                    rt.State = ContainerHealthState.Unknown;
                    rt.Detail = observation.Diagnostic ?? (observation.State == NativeHealthState.Starting
                        ? "Engine health: starting" : "Engine health: unknown");
                    rt.LastCheck = observation.ObservedAt;
                    Publish(snapshot);
                    return;
                }
                healthy = observation.State == NativeHealthState.Healthy;
                if (!NativeHealthPolicy.MatchesDesiredCheck(cfg, observation.Configuration))
                {
                    rt.State = healthy ? ContainerHealthState.Healthy : ContainerHealthState.Down;
                    rt.MaxRestarts = 0;
                    rt.LastCheck = observation.ObservedAt;
                    rt.Detail = $"Engine health: {(healthy ? "healthy" : "unhealthy")}; existing engine check differs from the requested check. App probes/autoheal are suspended to avoid duplicate checks. Recreate explicitly to apply desired settings.";
                    Publish(snapshot);
                    return;
                }
            }
            else if (cfg.Kind == HealthProbeKind.Command && cfg.DesiredHealth?.IsDisabled == true)
            {
                rt.State = ContainerHealthState.Unknown;
                rt.Detail = "Health check disabled";
                rt.LastCheck = DateTimeOffset.UtcNow;
                Publish(snapshot);
                return;
            }
            else
            {
                if (cfg.Kind == HealthProbeKind.Command)
                {
                    using var selection = _sessionContext.TryLockSelection(snapshot);
                    if (selection is null)
                        return;
                    await VerifyScopeAsync(ct).ConfigureAwait(false);
                    healthy = await ProbeCommandAsync(container.Id, cfg, ct).ConfigureAwait(false);
                }
                else
                {
                    healthy = await _tcpProbe(cfg.TcpPort, ct).ConfigureAwait(false);
                }
            }

            if (snapshot != _sessionContext.CaptureSnapshot())
                return;
            rt.LastCheck = native ? container.NativeHealth.ObservedAt : DateTimeOffset.UtcNow;
            var observed = _observation;
            if (observed is null || observed.Selection != snapshot)
                return;
            var current = observed.Containers.FirstOrDefault(c => c.Id == container.Id);
            if (current is null || current.State != ContainerState.Running ||
                current.StateChangedAt != container.StateChangedAt)
            {
                rt.State = ContainerHealthState.Unknown;
                rt.Detail = "Container changed while its health check was running; awaiting fresh health.";
                Publish(snapshot);
                return;
            }
            var unhealthy = rt.Progress.Record(healthy, native,
                cfg.DesiredHealth?.Retries ?? (cfg.DesiredHealth is null ? 1 : 3),
                StartPeriod(cfg), rt.LastCheck);

            if (healthy)
            {
                rt.State = ContainerHealthState.Healthy;
                rt.RestartCount = 0;
                if (!native)
                {
                    var interval = ProbeInterval(cfg, rt, rt.LastCheck);
                    if (interval > rt.ObservationMaxAge)
                        rt.ObservationMaxAge = interval;
                }
                rt.Detail = native ? "Engine health: healthy" : "App health: healthy (app must remain open)";
                if (!native && cfg.DesiredHealth is { } desired &&
                    new[] { desired.Interval, desired.StartInterval }.Any(value =>
                        value is not null && NativeHealthPolicy.Duration(value) < TimeSpan.FromSeconds(1)))
                    rt.Detail += "; sub-second intervals cannot be honored (1s scheduler resolution)";
                // A repeated success refreshes dependency evidence even without a badge transition.
                Publish(snapshot);

                return;
            }

            if (!unhealthy)
            {
                rt.Detail = rt.Progress.ConsecutiveFailures == 0
                    ? "Health check: starting (startup grace)"
                    : $"Health check failed ({rt.Progress.ConsecutiveFailures} consecutive failure(s)); awaiting retry threshold.";
                Publish(snapshot);
                return;
            }

            // If the policy was disabled/removed while this probe was in flight, don't enforce it.
            if (cfg.MaxRestarts > 0 && !IsStillActive(cfg, snapshot))
            {
                return;
            }

            using var enforcement = _sessionContext.TryLockSelection(snapshot);
            var currentObservation = _observation;
            if (enforcement is null || currentObservation is null || currentObservation.Selection != snapshot)
                return;
            if (cfg.MaxRestarts > 0 && rt.RestartCount < cfg.MaxRestarts &&
                !_restartWatchdog.IsRestartSuppressed(cfg.ContainerName) &&
                currentObservation.Containers.Any(c => c.Id == container.Id && c.State == ContainerState.Running))
            {
                rt.RestartCount++;
                var announce = rt.State != ContainerHealthState.Degraded;
                rt.State = ContainerHealthState.Degraded;
                rt.Detail = $"Unhealthy — auto-restarting ({rt.RestartCount}/{cfg.MaxRestarts})";
                Publish(snapshot);

                if (announce)
                {
                    Notify(UiText.Get("Automation_HealthUnhealthyTitle", "{0} is unhealthy", cfg.ContainerName),
                        UiText.Get("Automation_HealthRestarting", "Health check failed. Auto-restarting (attempt {0} of {1}).",
                            rt.RestartCount, cfg.MaxRestarts), snapshot);
                }

                await VerifyScopeAsync(ct).ConfigureAwait(false);
                if (_scopedGeneration is null)
                    _monitor.SuppressExitNotification(container.Id);
                var stop = await _wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
                // A manual stop/teardown arriving during autoheal must not be undone by its start.
                await VerifyScopeAsync(ct).ConfigureAwait(false);
                var restart = !stop.Success ? stop :
                    _restartWatchdog.IsRestartSuppressed(cfg.ContainerName) || !IsStillActive(cfg, snapshot)
                        ? new CommandResult { ExitCode = -1, StandardError = "Autoheal cancelled by a stop or policy change." }
                        : await _wslc.StartContainerAsync(container.Id, ct, explicitStart: false).ConfigureAwait(false);

                // Give the container time to come back before the next probe.
                rt.LastCheck = DateTimeOffset.UtcNow;
                rt.LastNativeObservation = rt.LastCheck;
                rt.Progress.Reset(rt.LastCheck);
                _monitor.RequestRefresh();

                // If the restart itself failed and the budget is now spent, escalate immediately
                // rather than waiting for a probe against a container that never came back.
                if (!restart.Success)
                {
                    _logger.LogWarning("Autoheal failed for {Name}: {Detail}", cfg.ContainerName, restart.ErrorText);
                    rt.Detail = "Autoheal failed: " + restart.ErrorText;
                    if (rt.RestartCount >= cfg.MaxRestarts)
                        await EscalateDownAsync(cfg, container, rt, snapshot, ct).ConfigureAwait(false);
                    else
                        Publish(snapshot);
                }
            }
            else
            {
                await EscalateDownAsync(cfg, container, rt, snapshot, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Health probe for {Name} failed.", cfg.ContainerName);
            rt.LastCheck = DateTimeOffset.UtcNow;
            rt.State = ContainerHealthState.Unknown;
            rt.Detail = $"Health check unavailable: {ex.Message}";
            Publish(snapshot);
        }
        finally
        {
            rt.CheckInProgress = false;
#if WSLCD_TEST_NO_DISPATCHER
            EvaluationCompleted?.Invoke();
#endif
        }
    }

    /// <summary>
    /// Marks the container down after the restart budget is exhausted (or for alert-only policies),
    /// notifying once per transition but always enforcing the stop while the container is running.
    /// </summary>
    private async Task EscalateDownAsync(HealthCheckConfig cfg, ContainerInfo container, Runtime rt,
        WslcSessionContext.SelectionSnapshot snapshot, CancellationToken ct)
    {
        var announce = rt.State != ContainerHealthState.Down;
        rt.State = ContainerHealthState.Down;
        rt.Detail = cfg.MaxRestarts > 0
            ? $"Down — stopped after {cfg.MaxRestarts} failed restart(s)"
            : "Down — health check failing";
        Publish(snapshot);

        if (cfg.MaxRestarts > 0 && !_restartWatchdog.IsRestartSuppressed(cfg.ContainerName) && IsStillActive(cfg, snapshot))
        {
            if (announce)
            {
                Notify(UiText.Get("Automation_HealthDownTitle", "{0} is down", cfg.ContainerName),
                    UiText.Get("Automation_HealthStopping", "Still unhealthy after {0} restart attempt(s). Stopping the container.",
                        cfg.MaxRestarts), snapshot);
            }

            // Enforce the stop even if we were already Down (e.g. the user manually restarted it).
            await VerifyScopeAsync(ct).ConfigureAwait(false);
            if (_scopedGeneration is null)
                _monitor.SuppressExitNotification(container.Id);
            var stop = await _wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
            if (!stop.Success)
            {
                rt.Detail = "Unhealthy; stop failed: " + stop.ErrorText;
                _logger.LogWarning("Health stop failed for {Name}: {Detail}", cfg.ContainerName, stop.ErrorText);
                Publish(snapshot);
            }
        }
        else if (announce)
        {
            Notify(UiText.Get("Automation_HealthUnhealthyTitle", "{0} is unhealthy", cfg.ContainerName),
                UiText.Get("Automation_HealthFailing", "Health check is failing."), snapshot);
        }
    }

    /// <summary>True when an enabled, valid policy for this container still exists in settings.</summary>
    private bool IsStillActive(HealthCheckConfig cfg, WslcSessionContext.SelectionSnapshot snapshot) =>
        _settings.HealthChecks.Any(c =>
            c.Enabled && c.IsValid && c.DesiredHealth?.IsDisabled != true &&
            _legacyOwners.OwnerFor(c)?.Matches(_settings.WslcPath, snapshot.Target) == true &&
            string.Equals(c.ContainerName, cfg.ContainerName, StringComparison.Ordinal) &&
            string.Equals(JsonSerializer.Serialize(c), JsonSerializer.Serialize(cfg), StringComparison.Ordinal));

    private static TimeSpan StartPeriod(HealthCheckConfig cfg) => cfg.DesiredHealth?.StartPeriod is { } period
        ? NativeHealthPolicy.Duration(period, allowZero: true) : TimeSpan.Zero;

    private static TimeSpan ProbeInterval(HealthCheckConfig cfg, Runtime rt, DateTimeOffset now)
    {
        if (cfg.DesiredHealth is not { } health)
            return TimeSpan.FromSeconds(cfg.EffectiveIntervalSeconds);
        if (!rt.Progress.HasSucceeded && now - rt.Progress.StartedAt < StartPeriod(cfg))
            return health.StartInterval is { } startup
                ? NativeHealthPolicy.Duration(startup) : TimeSpan.FromSeconds(5);
        return health.Interval is { } interval ? NativeHealthPolicy.Duration(interval) : TimeSpan.FromSeconds(30);
    }

    private async Task<bool> ProbeCommandAsync(string id, HealthCheckConfig cfg, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(cfg.DesiredHealth?.Timeout is { } timeout
            ? NativeHealthPolicy.Duration(timeout)
            : TimeSpan.FromSeconds(cfg.DesiredHealth is null ? Math.Clamp(cfg.EffectiveIntervalSeconds, 10, 60) : 30));
        try
        {
            var result = cfg.DesiredHealth is { } desired
                ? await _wslc.ExecHealthAsync(id, desired, linked.Token).ConfigureAwait(false)
                : await _wslc.ExecAsync(id, cfg.Command, linked.Token).ConfigureAwait(false);
            return result.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // A timed-out or failed probe counts as unhealthy.
            return false;
        }
    }

    private static async Task<bool> ProbeTcpAsync(int hostPort, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync("127.0.0.1", hostPort, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void Notify(string title, string message, WslcSessionContext.SelectionSnapshot snapshot) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
                _sessionContext.TryApply(snapshot, () => NotificationRequested?.Invoke(title, message));
        });

    private void Publish(WslcSessionContext.SelectionSnapshot selection)
    {
        var observation = _observation;
        if (observation is null || observation.Selection != selection)
            return;
        var items = _runtime
            .Where(kv => kv.Key.Revision == selection.Revision)
            .Select(kv => CreateSnapshot(kv.Key.Name, kv.Value, observation.Containers))
            .ToList();

        var worst = items.Count == 0
            ? ContainerHealthState.Unknown
            : items.Max(i => i.State);

        var snapshot = new HealthSnapshot { Containers = items, Worst = worst };
        if (!_sessionContext.TryApply(selection, () => Latest = snapshot))
            return;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
                _sessionContext.TryApply(selection, () => HealthChanged?.Invoke(this, snapshot));
        });
    }

    private static ContainerHealthSnapshot CreateSnapshot(string name, Runtime rt, IReadOnlyList<ContainerInfo> containers)
    {
        var state = rt.State;
        var detail = rt.Detail;
        var container = containers.FirstOrDefault(c => c.Id == rt.ContainerId);
        // A host TCP policy remains independent; do not hide engine failures or silently turn
        // them into TCP autoheal triggers.
        if (rt.Kind == HealthProbeKind.Tcp && container?.State == ContainerState.Running &&
            container.NativeHealth.State == NativeHealthState.Unhealthy)
        {
            state = ContainerHealthState.Down;
            detail += "; engine health: unhealthy (reported independently of the TCP restart policy)";
        }
        else if (rt.Kind == HealthProbeKind.Tcp && container?.State == ContainerState.Running &&
            container.NativeHealth.State is NativeHealthState.Starting or NativeHealthState.Unknown)
        {
            if (state == ContainerHealthState.Healthy)
                state = ContainerHealthState.Unknown;
            detail += "; engine health: " + container.NativeHealth.State.ToString().ToLowerInvariant();
        }
        return new ContainerHealthSnapshot
        {
            ContainerName = name, ContainerId = rt.ContainerId, ContainerGeneration = rt.StartedAt,
            ObservedAt = rt.LastCheck, State = state, RestartCount = rt.RestartCount,
            ObservationMaxAge = rt.ObservationMaxAge,
            MaxRestarts = rt.MaxRestarts, Detail = detail,
        };
    }

    /// <summary>
    /// Releases long-lived resources owned by this service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.StatusChanged -= OnStatusChanged;
        _sessionContext.SelectionChanged -= OnSessionContextChanged;
        _sessionContext.EngineSettingChanged -= OnSessionContextChanged;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
    }

    /// <summary>Mutable per-container tracking used by the evaluation loop.</summary>
    private sealed class Runtime
    {
        /// <summary>
        /// Gets or sets the latest health state for this watched container.
        /// </summary>
        public ContainerHealthState State = ContainerHealthState.Unknown;
        /// <summary>
        /// Gets progress for callers in the service or view-model layer.
        /// </summary>
        public HealthProbeProgress Progress { get; } = new();
        /// <summary>
        /// Gets restart count for other services or view models.
        /// </summary>
        public int RestartCount;
        /// <summary>
        /// Gets max restarts for other services or view models.
        /// </summary>
        public int MaxRestarts;
        /// <summary>
        /// Gets or sets the user-facing detail for the latest health observation.
        /// </summary>
        public string Detail = string.Empty;
        /// <summary>
        /// Gets last check for other services or view models.
        /// </summary>
        public DateTimeOffset LastCheck = DateTimeOffset.MinValue;
        /// <summary>
        /// Gets last native observation for other services or view models.
        /// </summary>
        public DateTimeOffset LastNativeObservation = DateTimeOffset.MinValue;
        /// <summary>
        /// Gets observation max age for callers in the service or view-model layer.
        /// </summary>
        public TimeSpan ObservationMaxAge = TimeSpan.FromSeconds(15);
        /// <summary>
        /// Extracts a container id from native health JSON.
        /// </summary>
        public string ContainerId = string.Empty;
        /// <summary>
        /// Gets or sets the health-check configuration text being evaluated.
        /// </summary>
        public string Configuration = string.Empty;
        /// <summary>
        /// Gets or sets which kind of health probe produced this runtime state.
        /// </summary>
        public HealthProbeKind Kind;
        /// <summary>
        /// Starts d at work requested by the UI or a background supervisor.
        /// </summary>
        public ulong StartedAt;
        /// <summary>
        /// Gets check in progress for other services or view models.
        /// </summary>
        public volatile bool CheckInProgress;
    }
}
