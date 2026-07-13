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
    public EngineHealth Health { get; init; }
    public int RunningCount { get; init; }
    public int TotalCount { get; init; }
    public IReadOnlyList<ContainerInfo> Containers { get; init; } = Array.Empty<ContainerInfo>();
    public string Summary { get; init; } = string.Empty;
}

/// <summary>Snapshot of Kubernetes (k3s) health for the nav footer indicator.</summary>
public sealed class K8sStatusSnapshot
{
    public ClusterState State { get; init; } = ClusterState.Unknown;
    public int PodsRunning { get; init; }
    public int PodsTotal { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>Whether the cluster is installed (footer indicator is hidden otherwise).</summary>
    public bool IsInstalled => State is ClusterState.Stopped or ClusterState.Running;
}

/// <summary>
/// Periodically polls the WSL container engine and raises <see cref="StatusChanged"/>
/// on the UI thread. Acts as the single source of truth for container health so the
/// tray icon and the Containers page do not poll independently.
/// </summary>
public sealed class StatusMonitor : IDisposable
{
    private readonly IWslcService _wslc;
    private readonly IKubernetesService _k8s;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly INotificationService _notifications;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<StatusMonitor> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private DateTimeOffset _lastAzureRefresh = DateTimeOffset.MinValue;

    // Serializes container polls so the loop and any RequestRefresh don't run the
    // read-modify-write of Latest + transition detection concurrently.
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    // Container IDs the app itself just stopped/restarted/killed, so the resulting
    // running->stopped transition is not surfaced as an "exited" toast. Entries are
    // consumed on detection and expire if the stop never lands (e.g. failed command).
    private readonly ConcurrentDictionary<string, DateTimeOffset> _selfInitiatedStops = new(StringComparer.Ordinal);
    private static readonly TimeSpan SelfInitiatedStopTtl = TimeSpan.FromSeconds(60);

    public event EventHandler<EngineStatusSnapshot>? StatusChanged;
    public event EventHandler<K8sStatusSnapshot>? K8sStatusChanged;

    public EngineStatusSnapshot? Latest { get; private set; }

    public K8sStatusSnapshot? LatestK8s { get; private set; }

    public DispatcherQueue Dispatcher => _dispatcher;

    public StatusMonitor(IWslcService wslc, IKubernetesService k8s, RegistryAuthRefresher authRefresher, ISettingsService settings, INotificationService notifications, DispatcherQueue dispatcher, ILogger<StatusMonitor> logger)
    {
        _wslc = wslc;
        _k8s = k8s;
        _authRefresher = authRefresher;
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
    }

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
        _ = Task.Run(PollOnceAsync);
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
            // Poll containers and Kubernetes together. The k8s probe also warms the WSL
            // distro early so the Kubernetes page loads quickly when first opened.
            await Task.WhenAll(PollOnceAsync(), PollK8sOnceAsync()).ConfigureAwait(false);

            // Keep Azure-backed registry tokens fresh in the background so pulls/runs keep
            // working. Runs on a slow cadence since ACR tokens last a few hours.
            await MaybeRefreshAzureTokensAsync().ConfigureAwait(false);

            var interval = Math.Clamp(_settings.RefreshIntervalSeconds, AppConstants.RefreshIntervalMinSeconds, AppConstants.RefreshIntervalMaxSeconds);
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

    private async Task PollK8sOnceAsync()
    {
        K8sStatusSnapshot snapshot;
        try
        {
            var status = await _k8s.GetFooterStatusAsync().ConfigureAwait(false);
            snapshot = status.State switch
            {
                ClusterState.Running => new K8sStatusSnapshot
                {
                    State = ClusterState.Running,
                    PodsRunning = status.PodsRunning,
                    PodsTotal = status.PodsTotal,
                    Summary = $"Kubernetes: running · {status.PodsRunning}/{status.PodsTotal} pods up",
                },
                ClusterState.Stopped => new K8sStatusSnapshot
                {
                    State = ClusterState.Stopped,
                    Summary = "Kubernetes: stopped",
                },
                ClusterState.NotInstalled => new K8sStatusSnapshot
                {
                    State = ClusterState.NotInstalled,
                    Summary = "Kubernetes: not installed",
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
        try
        {
            var engineUp = await _wslc.IsEngineAvailableAsync().ConfigureAwait(false);
            if (!engineUp)
            {
                snapshot = new EngineStatusSnapshot
                {
                    Health = EngineHealth.Down,
                    Summary = "Engine: unreachable",
                };
            }
            else
            {
                var containers = await _wslc.ListContainersAsync(all: true).ConfigureAwait(false);
                var running = containers.Count(c => c.State == ContainerState.Running);
                var total = containers.Count;

                snapshot = new EngineStatusSnapshot
                {
                    Health = EngineHealth.Healthy,
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
                Health = EngineHealth.Down,
                Summary = $"Engine: error ({ex.Message})",
            };
        }

        var previous = Latest;
        Latest = snapshot;

        DetectAndNotifyTransitions(previous, snapshot);

        _dispatcher.TryEnqueue(() => StatusChanged?.Invoke(this, snapshot));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Compares the prior snapshot to the new one and emits toasts for engine up/down
    /// transitions and for containers that stopped running. Skipped on the first poll
    /// (<paramref name="previous"/> is null) so launch doesn't spam notifications.
    /// </summary>
    private void DetectAndNotifyTransitions(EngineStatusSnapshot? previous, EngineStatusSnapshot current)
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

                _notifications.NotifyContainerExited(string.IsNullOrWhiteSpace(now.Name) ? now.ShortId : now.Name, now.Id);
            }
        }
    }

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
        _pollGate.Dispose();
        _disposed = true;
    }
}
