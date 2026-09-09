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
public sealed class HealthWatchdog : IDisposable
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly ILogger<HealthWatchdog> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly RestartPolicyWatchdog _restartWatchdog;

    private readonly ConcurrentDictionary<string, Runtime> _runtime = new(StringComparer.Ordinal);

    private volatile IReadOnlyList<ContainerInfo> _containers = Array.Empty<ContainerInfo>();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised (on the UI thread) whenever a watched container's health changes.</summary>
    public event EventHandler<HealthSnapshot>? HealthChanged;

    /// <summary>Raised (on the UI thread) when an unhealthy transition should surface a toast.</summary>
    public event Action<string, string>? NotificationRequested;

    public HealthSnapshot Latest { get; private set; } = new();

    public HealthWatchdog(IWslcService wslc, StatusMonitor monitor, ISettingsService settings,
        ILogger<HealthWatchdog> logger, RestartPolicyWatchdog restartWatchdog)
    {
        _wslc = wslc;
        _monitor = monitor;
        _settings = settings;
        _logger = logger;
        _dispatcher = monitor.Dispatcher;
        _restartWatchdog = restartWatchdog;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // Reuse the status monitor's container list rather than polling the engine again.
        _monitor.StatusChanged += OnStatusChanged;
        if (_monitor.Latest is not null)
        {
            _containers = _monitor.Latest.Containers;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        _containers = e.Containers;
        Publish();
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

    private void Tick(CancellationToken ct)
    {
        var configs = _settings.HealthChecks
            .Where(c => c.Enabled && c.IsValid && c.DesiredHealth?.IsDisabled != true)
            .ToList();
        foreach (var container in _containers)
        {
            var name = container.Name.TrimStart('/');
            if (!configs.Any(c => c.ContainerName == name) &&
                (container.NativeHealth.State is NativeHealthState.Starting or NativeHealthState.Healthy or NativeHealthState.Unhealthy ||
                 container.NativeHealth.State == NativeHealthState.Unknown &&
                 (container.NativeHealth.Configuration?.HasCommand == true || _runtime.ContainsKey(name))))
                configs.Add(new HealthCheckConfig
                {
                    ContainerName = name, MaxRestarts = 0,
                    DesiredHealth = container.NativeHealth.Configuration, Command = "engine-owned",
                });
        }

        var active = new HashSet<string>(configs.Select(c => c.ContainerName), StringComparer.Ordinal);
        var changed = false;

        // Drop runtime state for containers that are no longer watched.
        foreach (var name in _runtime.Keys.ToList())
        {
            if (!active.Contains(name) && _runtime.TryRemove(name, out _))
            {
                changed = true;
            }
        }

        var containers = _containers;
        var now = DateTimeOffset.UtcNow;

        foreach (var cfg in configs)
        {
            var rt = _runtime.GetOrAdd(cfg.ContainerName, _ => new Runtime());

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
                    if (rt.State != ContainerHealthState.Down && IsStillActive(cfg))
                    {
                        rt.State = ContainerHealthState.Down;
                        rt.MaxRestarts = cfg.MaxRestarts;
                        rt.Detail = $"Down — not running after {cfg.MaxRestarts} restart attempt(s)";
                        changed = true;
                        Notify($"{cfg.ContainerName} is down",
                            $"Container is not running after {cfg.MaxRestarts} restart attempt(s).");
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
            _ = EvaluateAsync(cfg, container, rt, ct);
        }

        if (changed)
        {
            Publish();
        }
    }

    private async Task EvaluateAsync(HealthCheckConfig cfg, ContainerInfo container, Runtime rt, CancellationToken ct)
    {
        try
        {
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
                    Publish();
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
                    Publish();
                    return;
                }
                healthy = observation.State == NativeHealthState.Healthy;
                if (!NativeHealthPolicy.MatchesDesiredCheck(cfg, observation.Configuration))
                {
                    rt.State = healthy ? ContainerHealthState.Healthy : ContainerHealthState.Down;
                    rt.MaxRestarts = 0;
                    rt.LastCheck = observation.ObservedAt;
                    rt.Detail = $"Engine health: {(healthy ? "healthy" : "unhealthy")}; existing engine check differs from the requested check. App probes/autoheal are suspended to avoid duplicate checks. Recreate explicitly to apply desired settings.";
                    Publish();
                    return;
                }
            }
            else if (cfg.Kind == HealthProbeKind.Command && cfg.DesiredHealth?.IsDisabled == true)
            {
                rt.State = ContainerHealthState.Unknown;
                rt.Detail = "Health check disabled";
                rt.LastCheck = DateTimeOffset.UtcNow;
                Publish();
                return;
            }
            else
            {
                healthy = cfg.Kind == HealthProbeKind.Command
                    ? await ProbeCommandAsync(container.Id, cfg, ct).ConfigureAwait(false)
                    : await ProbeTcpAsync(cfg.TcpPort, ct).ConfigureAwait(false);
            }

            rt.LastCheck = native ? container.NativeHealth.ObservedAt : DateTimeOffset.UtcNow;
            var current = _containers.FirstOrDefault(c => c.Id == container.Id);
            if (current is null || current.State != ContainerState.Running ||
                current.StateChangedAt != container.StateChangedAt)
            {
                rt.State = ContainerHealthState.Unknown;
                rt.Detail = "Container changed while its health check was running; awaiting fresh health.";
                Publish();
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
                Publish();

                return;
            }

            if (!unhealthy)
            {
                rt.Detail = rt.Progress.ConsecutiveFailures == 0
                    ? "Health check: starting (startup grace)"
                    : $"Health check failed ({rt.Progress.ConsecutiveFailures} consecutive failure(s)); awaiting retry threshold.";
                Publish();
                return;
            }

            // If the policy was disabled/removed while this probe was in flight, don't enforce it.
            if (cfg.MaxRestarts > 0 && !IsStillActive(cfg))
            {
                return;
            }

            if (cfg.MaxRestarts > 0 && rt.RestartCount < cfg.MaxRestarts &&
                !_restartWatchdog.IsRestartSuppressed(cfg.ContainerName) &&
                _containers.Any(c => c.Id == container.Id && c.State == ContainerState.Running))
            {
                rt.RestartCount++;
                var announce = rt.State != ContainerHealthState.Degraded;
                rt.State = ContainerHealthState.Degraded;
                rt.Detail = $"Unhealthy — auto-restarting ({rt.RestartCount}/{cfg.MaxRestarts})";
                Publish();

                if (announce)
                {
                    Notify($"{cfg.ContainerName} is unhealthy",
                        $"Health check failed. Auto-restarting (attempt {rt.RestartCount} of {cfg.MaxRestarts}).");
                }

                _monitor.SuppressExitNotification(container.Id);
                var stop = await _wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
                // A manual stop/teardown arriving during autoheal must not be undone by its start.
                var restart = !stop.Success ? stop :
                    _restartWatchdog.IsRestartSuppressed(cfg.ContainerName) || !IsStillActive(cfg)
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
                        await EscalateDownAsync(cfg, container, rt, ct).ConfigureAwait(false);
                    else
                        Publish();
                }
            }
            else
            {
                await EscalateDownAsync(cfg, container, rt, ct).ConfigureAwait(false);
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
            Publish();
        }
        finally
        {
            rt.CheckInProgress = false;
        }
    }

    /// <summary>
    /// Marks the container down after the restart budget is exhausted (or for alert-only policies),
    /// notifying once per transition but always enforcing the stop while the container is running.
    /// </summary>
    private async Task EscalateDownAsync(HealthCheckConfig cfg, ContainerInfo container, Runtime rt, CancellationToken ct)
    {
        var announce = rt.State != ContainerHealthState.Down;
        rt.State = ContainerHealthState.Down;
        rt.Detail = cfg.MaxRestarts > 0
            ? $"Down — stopped after {cfg.MaxRestarts} failed restart(s)"
            : "Down — health check failing";
        Publish();

        if (cfg.MaxRestarts > 0 && !_restartWatchdog.IsRestartSuppressed(cfg.ContainerName) && IsStillActive(cfg))
        {
            if (announce)
            {
                Notify($"{cfg.ContainerName} is down",
                    $"Still unhealthy after {cfg.MaxRestarts} restart attempt(s). Stopping the container.");
            }

            // Enforce the stop even if we were already Down (e.g. the user manually restarted it).
            _monitor.SuppressExitNotification(container.Id);
            var stop = await _wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
            if (!stop.Success)
            {
                rt.Detail = "Unhealthy; stop failed: " + stop.ErrorText;
                _logger.LogWarning("Health stop failed for {Name}: {Detail}", cfg.ContainerName, stop.ErrorText);
                Publish();
            }
        }
        else if (announce)
        {
            Notify($"{cfg.ContainerName} is unhealthy", "Health check is failing.");
        }
    }

    /// <summary>True when an enabled, valid policy for this container still exists in settings.</summary>
    private bool IsStillActive(HealthCheckConfig cfg) =>
        _settings.HealthChecks.Any(c =>
            c.Enabled && c.IsValid && c.DesiredHealth?.IsDisabled != true &&
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

    private void Notify(string title, string message) =>
        _dispatcher.TryEnqueue(() => NotificationRequested?.Invoke(title, message));

    private void Publish()
    {
        var items = _runtime
            .Select(kv => CreateSnapshot(kv.Key, kv.Value))
            .ToList();

        var worst = items.Count == 0
            ? ContainerHealthState.Unknown
            : items.Max(i => i.State);

        var snapshot = new HealthSnapshot { Containers = items, Worst = worst };
        Latest = snapshot;
        _dispatcher.TryEnqueue(() => HealthChanged?.Invoke(this, snapshot));
    }

    private ContainerHealthSnapshot CreateSnapshot(string name, Runtime rt)
    {
        var state = rt.State;
        var detail = rt.Detail;
        var container = _containers.FirstOrDefault(c => c.Id == rt.ContainerId);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.StatusChanged -= OnStatusChanged;

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
        public ContainerHealthState State = ContainerHealthState.Unknown;
        public HealthProbeProgress Progress { get; } = new();
        public int RestartCount;
        public int MaxRestarts;
        public string Detail = string.Empty;
        public DateTimeOffset LastCheck = DateTimeOffset.MinValue;
        public DateTimeOffset LastNativeObservation = DateTimeOffset.MinValue;
        public TimeSpan ObservationMaxAge = TimeSpan.FromSeconds(15);
        public string ContainerId = string.Empty;
        public string Configuration = string.Empty;
        public HealthProbeKind Kind;
        public ulong StartedAt;
        public volatile bool CheckInProgress;
    }
}
