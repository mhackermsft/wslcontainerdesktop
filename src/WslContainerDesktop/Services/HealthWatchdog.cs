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

    public HealthWatchdog(IWslcService wslc, StatusMonitor monitor, ISettingsService settings, ILogger<HealthWatchdog> logger)
    {
        _wslc = wslc;
        _monitor = monitor;
        _settings = settings;
        _logger = logger;
        _dispatcher = monitor.Dispatcher;
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

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e) => _containers = e.Containers;

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
            .Where(c => c.Enabled && c.IsValid)
            .ToList();

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
            var container = containers.FirstOrDefault(c =>
                string.Equals(c.Name, cfg.ContainerName, StringComparison.Ordinal));

            // Not present or not running: the workload probe is meaningless. Preserve a "Down"
            // state we produced by exhausting the restart budget (so the badge/tray stay red);
            // otherwise treat it as Unknown.
            if (container is null || container.State != ContainerState.Running)
            {
                var gaveUp = rt.State == ContainerHealthState.Down &&
                    cfg.MaxRestarts > 0 && rt.RestartCount >= cfg.MaxRestarts;

                if (!gaveUp && rt.State != ContainerHealthState.Unknown)
                {
                    rt.State = ContainerHealthState.Unknown;
                    rt.ConsecutiveFailures = 0;
                    changed = true;
                }

                continue;
            }

            if (rt.CheckInProgress)
            {
                continue;
            }

            if ((now - rt.LastCheck).TotalSeconds < cfg.EffectiveIntervalSeconds)
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
            var healthy = cfg.Kind == HealthProbeKind.Command
                ? await ProbeCommandAsync(container.Id, cfg.Command, ct).ConfigureAwait(false)
                : await ProbeTcpAsync(cfg.TcpPort, ct).ConfigureAwait(false);

            rt.LastCheck = DateTimeOffset.UtcNow;

            if (healthy)
            {
                var recovered = rt.State != ContainerHealthState.Healthy;
                rt.State = ContainerHealthState.Healthy;
                rt.ConsecutiveFailures = 0;
                rt.RestartCount = 0;
                rt.Detail = "Healthy";
                if (recovered)
                {
                    Publish();
                }

                return;
            }

            rt.ConsecutiveFailures++;

            if (cfg.MaxRestarts > 0 && rt.RestartCount < cfg.MaxRestarts)
            {
                rt.RestartCount++;
                var wasDegraded = rt.State == ContainerHealthState.Degraded;
                rt.State = ContainerHealthState.Degraded;
                rt.Detail = $"Unhealthy — auto-restarting ({rt.RestartCount}/{cfg.MaxRestarts})";
                Publish();

                if (!wasDegraded)
                {
                    Notify($"{cfg.ContainerName} is unhealthy",
                        $"Health check failed. Auto-restarting (attempt {rt.RestartCount} of {cfg.MaxRestarts}).");
                }

                await _wslc.RestartContainerAsync(container.Id, ct).ConfigureAwait(false);

                // Give the container time to come back before the next probe.
                rt.LastCheck = DateTimeOffset.UtcNow;
            }
            else
            {
                var wasDown = rt.State == ContainerHealthState.Down;
                rt.State = ContainerHealthState.Down;
                rt.Detail = cfg.MaxRestarts > 0
                    ? $"Down — stopped after {cfg.MaxRestarts} failed restart(s)"
                    : "Down — health check failing";
                Publish();

                if (!wasDown)
                {
                    if (cfg.MaxRestarts > 0)
                    {
                        Notify($"{cfg.ContainerName} is down",
                            $"Still unhealthy after {cfg.MaxRestarts} restart attempt(s). Stopping the container.");
                        await _wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        Notify($"{cfg.ContainerName} is unhealthy", "Health check is failing.");
                    }
                }
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
        }
        finally
        {
            rt.CheckInProgress = false;
        }
    }

    private async Task<bool> ProbeCommandAsync(string id, string command, CancellationToken ct)
    {
        try
        {
            var result = await _wslc.ExecAsync(id, command, ct).ConfigureAwait(false);
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
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
            .Select(kv => new ContainerHealthSnapshot
            {
                ContainerName = kv.Key,
                State = kv.Value.State,
                RestartCount = kv.Value.RestartCount,
                MaxRestarts = kv.Value.MaxRestarts,
                Detail = kv.Value.Detail,
            })
            .ToList();

        var worst = items.Count == 0
            ? ContainerHealthState.Unknown
            : items.Max(i => i.State);

        var snapshot = new HealthSnapshot { Containers = items, Worst = worst };
        Latest = snapshot;
        _dispatcher.TryEnqueue(() => HealthChanged?.Invoke(this, snapshot));
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
        public int ConsecutiveFailures;
        public int RestartCount;
        public int MaxRestarts;
        public string Detail = string.Empty;
        public DateTimeOffset LastCheck = DateTimeOffset.MinValue;
        public volatile bool CheckInProgress;
    }
}
