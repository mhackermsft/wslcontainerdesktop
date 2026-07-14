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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Enforces compose <c>restart:</c> policies for containers that have <em>no</em> health check
/// (health-checked containers are supervised by <see cref="HealthWatchdog"/>). It watches the
/// containers named in <see cref="ISettingsService.RestartPolicies"/> and, when one exits, starts
/// it again within a restart budget — a best-effort, in-process emulation of the daemon behaviour.
///
/// <para><b>Requires the app to be running.</b> Like the health watchdog it reuses
/// <see cref="StatusMonitor"/> as the single container-polling source and resumes after the app is
/// relaunched via the compose supervisor's reconcile.</para>
/// </summary>
public sealed class RestartPolicyWatchdog : IDisposable
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly ILogger<RestartPolicyWatchdog> _logger;
    private readonly DispatcherQueue _dispatcher;

    private readonly ConcurrentDictionary<string, Runtime> _runtime = new(StringComparer.Ordinal);

    /// <summary>Container names the user intentionally stopped; suppresses restart except for <c>always</c>.</summary>
    private readonly ConcurrentDictionary<string, byte> _suppressed = new(StringComparer.Ordinal);

    private volatile IReadOnlyList<ContainerInfo> _containers = Array.Empty<ContainerInfo>();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    /// <summary>Wait this long after a restart attempt before trying again, giving the container time to boot.</summary>
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(6);

    /// <summary>How long a container must stay running before its restart budget resets.</summary>
    private static readonly TimeSpan StableResetWindow = TimeSpan.FromSeconds(20);

    /// <summary>Raised (on the UI thread) when a restart or give-up event should surface a toast.</summary>
    public event Action<string, string>? NotificationRequested;

    public RestartPolicyWatchdog(IWslcService wslc, StatusMonitor monitor, ISettingsService settings, ILogger<RestartPolicyWatchdog> logger)
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
        _monitor.StatusChanged += OnStatusChanged;
        if (_monitor.Latest is not null)
        {
            _containers = _monitor.Latest.Containers;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// Records that the user intentionally stopped a container, so <c>unless-stopped</c> and
    /// <c>on-failure</c> policies do not immediately restart it. <c>always</c> still restarts.
    /// </summary>
    public void SuppressRestart(string containerName)
    {
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            _suppressed[containerName.TrimStart('/')] = 1;
        }
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
                _logger.LogDebug(ex, "Restart watchdog tick failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick(CancellationToken ct)
    {
        var policies = _settings.RestartPolicies
            .Where(p => p.Enabled && p.IsValid)
            .ToList();

        // Containers that also have a health check are owned by the HealthWatchdog; don't fight it.
        var healthWatched = new HashSet<string>(
            _settings.HealthChecks.Where(h => h.Enabled && h.IsValid).Select(h => h.ContainerName),
            StringComparer.Ordinal);

        var active = new HashSet<string>(policies.Select(p => p.ContainerName), StringComparer.Ordinal);
        foreach (var name in _runtime.Keys.ToList())
        {
            if (!active.Contains(name))
            {
                _runtime.TryRemove(name, out _);
            }
        }

        var containers = _containers;
        var now = DateTimeOffset.UtcNow;

        foreach (var policy in policies)
        {
            if (healthWatched.Contains(policy.ContainerName))
            {
                continue;
            }

            var rt = _runtime.GetOrAdd(policy.ContainerName, _ => new Runtime());
            if (rt.InProgress)
            {
                continue;
            }

            var container = containers.FirstOrDefault(c =>
                string.Equals(c.Name.TrimStart('/'), policy.ContainerName, StringComparison.Ordinal));

            // Not created (or removed): nothing to supervise yet.
            if (container is null)
            {
                continue;
            }

            if (container.State == ContainerState.Running)
            {
                // Sustained running resets the budget and clears any manual-stop suppression.
                if (now - container.StateChangedUtc >= StableResetWindow)
                {
                    rt.RestartCount = 0;
                    rt.Exhausted = false;
                    _suppressed.TryRemove(policy.ContainerName, out _);
                }

                continue;
            }

            // Container is not running. Decide whether the policy calls for a restart.
            if (rt.Exhausted)
            {
                continue;
            }

            var suppressed = _suppressed.ContainsKey(policy.ContainerName);
            if (suppressed && policy.Policy != RestartPolicyKind.Always)
            {
                continue;
            }

            if (now - rt.LastAttempt < RestartBackoff)
            {
                continue;
            }

            if (policy.MaxRestarts <= 0 || rt.RestartCount >= policy.MaxRestarts)
            {
                rt.Exhausted = true;
                Notify($"{policy.ContainerName} not restarted",
                    $"Container is not running after {rt.RestartCount} restart attempt(s); giving up.");
                continue;
            }

            rt.InProgress = true;
            _ = RestartAsync(policy, container, rt, ct);
        }
    }

    private async Task RestartAsync(RestartPolicyConfig policy, ContainerInfo container, Runtime rt, CancellationToken ct)
    {
        try
        {
            // on-failure only restarts after a non-zero exit. Other policies restart unconditionally.
            if (policy.Policy == RestartPolicyKind.OnFailure && await ExitedCleanlyAsync(container.Id, ct).ConfigureAwait(false))
            {
                rt.Exhausted = true; // a clean exit is terminal for on-failure
                return;
            }

            rt.LastAttempt = DateTimeOffset.UtcNow;
            rt.RestartCount++;

            Notify($"Restarting {policy.ContainerName}",
                $"Container exited; restarting (attempt {rt.RestartCount} of {policy.MaxRestarts}).");

            var start = await _wslc.StartContainerAsync(container.Id, ct).ConfigureAwait(false);
            if (!start.Success)
            {
                _logger.LogDebug("Restart of {Name} failed: {Detail}", policy.ContainerName,
                    string.IsNullOrWhiteSpace(start.StandardError) ? start.StandardOutput : start.StandardError);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Restart of {Name} threw.", policy.ContainerName);
        }
        finally
        {
            rt.InProgress = false;
        }
    }

    /// <summary>
    /// Best-effort exit-code check via <c>wslc inspect</c>: returns true only when the container's
    /// last exit code can be read and is zero. Unknown/unreadable exits are treated as failures.
    /// </summary>
    private async Task<bool> ExitedCleanlyAsync(string id, CancellationToken ct)
    {
        try
        {
            var inspect = await _wslc.InspectContainerAsync(id, ct).ConfigureAwait(false);
            if (!inspect.Success || string.IsNullOrWhiteSpace(inspect.StandardOutput))
            {
                return false;
            }

            var match = Regex.Match(inspect.StandardOutput, "\"ExitCode\"\\s*:\\s*(-?\\d+)",
                RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var code) && code == 0;
        }
        catch
        {
            return false;
        }
    }

    private void Notify(string title, string message) =>
        _dispatcher.TryEnqueue(() => NotificationRequested?.Invoke(title, message));

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

    private sealed class Runtime
    {
        public int RestartCount;
        public bool Exhausted;
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        public volatile bool InProgress;
    }
}
