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
    private readonly WslcSessionContext _sessionContext;
    private readonly LegacyPolicyOwnershipStore _legacyOwners;
    private readonly WslcSessionInfo? _scopedGeneration;
    private readonly string? _scopedExecutable;
    private readonly IWslcService? _discovery;
    private readonly TimeProvider _clock;
    private volatile bool _scopeAvailable;

    private readonly ConcurrentDictionary<(long Revision, string Name), Runtime> _runtime = new();

    /// <summary>Container names the user intentionally stopped; suppresses restart except for <c>always</c>.</summary>
    private readonly RestartSuppressionState _suppression;

    private sealed record Observation(WslcSessionContext.SelectionSnapshot Selection, IReadOnlyList<ContainerInfo> Containers,
        DateTimeOffset ObservedAt);
    private volatile Observation? _observation;
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
#if WSLCD_TEST_NO_DISPATCHER
    internal event Action? EvaluationCompleted;
#endif

    /// <summary>Creates the watchdog and connects it to the shared status monitor and settings.</summary>
    public RestartPolicyWatchdog(IWslcService wslc, StatusMonitor monitor, ISettingsService settings, ILogger<RestartPolicyWatchdog> logger,
        RestartSuppressionState suppression, WslcSessionContext sessionContext, LegacyPolicyOwnershipStore legacyOwners,
        WslcSessionInfo? scopedGeneration = null, string? scopedExecutable = null, IWslcService? discovery = null,
        TimeProvider? clock = null)
    {
        _wslc = wslc;
        _monitor = monitor;
        _settings = settings;
        _logger = logger;
        _suppression = suppression;
        _dispatcher = monitor.Dispatcher;
        _sessionContext = sessionContext;
        _legacyOwners = legacyOwners;
        _scopedGeneration = scopedGeneration;
        _scopedExecutable = scopedExecutable;
        _discovery = discovery;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Begins listening to status snapshots and running the restart loop.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
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
        _observation = new(_sessionContext.CaptureSnapshot(), containers, observedAt ?? DateTimeOffset.UtcNow);
        _scopeAvailable = true;
    }

    internal void PauseScopedObservation()
    {
        _scopeAvailable = false;
        _observation = null;
    }

    private async Task VerifyScopeAsync(CancellationToken ct)
    {
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

    /// <summary>
    /// Records that the user intentionally stopped a container, so <c>unless-stopped</c> and
    /// <c>on-failure</c> policies do not immediately restart it. <c>always</c> still restarts.
    /// </summary>
    public void SuppressRestart(string containerName)
    {
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            _suppression.Suppress(containerName, CurrentOwner());
        }
    }

    /// <summary>Stores the latest monitor snapshot for the background restart loop.</summary>
    internal void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        var selection = _sessionContext.CaptureSnapshot();
        if (_observation is { } previous && previous.Selection != selection)
            _runtime.Clear();
        _observation = new(selection, e.Containers, DateTimeOffset.UtcNow);
    }

    /// <summary>Periodic loop that evaluates restart policies from the most recent status snapshot.</summary>
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

    /// <summary>Evaluates all active policies once and schedules restart attempts when needed.</summary>
    internal void Tick(CancellationToken ct)
    {
        var observation = _observation;
        if (observation is null || observation.Selection != _sessionContext.CaptureSnapshot() ||
            _scopedGeneration is not null && DateTimeOffset.UtcNow - observation.ObservedAt >
                AutomationSessionScopePlanner.ObservationMaxAge)
            return;
        var policies = _settings.RestartPolicies
            .Where(p => p.Enabled && p.IsValid &&
                _legacyOwners.OwnerFor(p)?.Matches(_settings.WslcPath, observation.Selection.Target) == true)
            .ToList();

        // Containers that also have a health check are owned by the HealthWatchdog; don't fight it.
        var healthWatched = new HashSet<string>(
            _settings.HealthChecks.Where(h => h.Enabled && h.IsValid && h.MaxRestarts > 0 &&
                h.DesiredHealth?.IsDisabled != true &&
                _legacyOwners.OwnerFor(h)?.Matches(_settings.WslcPath, observation.Selection.Target) == true).Select(h => h.ContainerName),
            StringComparer.Ordinal);

        var active = new HashSet<string>(policies.Select(p => p.ContainerName), StringComparer.Ordinal);
        foreach (var key in _runtime.Keys.ToList())
        {
            if (key.Revision != observation.Selection.Revision || !active.Contains(key.Name))
            {
                _runtime.TryRemove(key, out _);
            }
        }

        var containers = observation.Containers;
        var now = _clock.GetUtcNow();

        foreach (var policy in policies)
        {
            if (healthWatched.Contains(policy.ContainerName))
            {
                continue;
            }

            var rt = _runtime.GetOrAdd((observation.Selection.Revision, policy.ContainerName), _ => new Runtime());
            if (rt.InProgress)
            {
                continue;
            }

            var container = containers.FirstOrDefault(c =>
                string.Equals(c.Name.TrimStart('/'), policy.ContainerName, StringComparison.Ordinal));

            // Not created (or removed): nothing to supervise yet.
            if (container is null)
            {
                rt.RunningSince = null;
                continue;
            }

            if (container.State == ContainerState.Running)
            {
                // Running resets the budget, but only a successful explicit start clears stop intent.
                rt.RunningSince ??= now;
                var runningSince = container.StateChangedAtKnown
                    ? container.StateChangedUtc
                    : rt.RunningSince.Value;
                if (now - runningSince >= StableResetWindow)
                {
                    rt.RestartCount = 0;
                    rt.Exhausted = false;
                }

                continue;
            }

            rt.RunningSince = null;
            // Unknown (including new engine states) and paused are not evidence of an exit.
            if (container.State is not (ContainerState.Created or ContainerState.Stopped))
            {
                continue;
            }

            // Container is stopped. Decide whether the policy calls for a restart.
            if (rt.Exhausted)
            {
                continue;
            }

            var suppressed = _suppression.IsSuppressed(policy.ContainerName,
                AutomationSessionOwner.Capture(_settings.WslcPath, observation.Selection.Target));
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
                Notify(UiText.Get("Automation_RestartExhaustedTitle", "{0} not restarted", policy.ContainerName),
                    UiText.Get("Automation_RestartExhausted", "Container is not running after {0} restart attempt(s); giving up.", rt.RestartCount), observation.Selection);
                continue;
            }

            rt.InProgress = true;
            _ = RestartAsync(policy, container, rt, observation.Selection, ct);
        }
    }

    /// <summary>Starts one stopped container if its policy still applies and its restart budget allows it.</summary>
    private async Task RestartAsync(RestartPolicyConfig policy, ContainerInfo container, Runtime rt,
        WslcSessionContext.SelectionSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            using var selection = _sessionContext.TryLockSelection(snapshot);
            if (selection is null)
                return;
            // The policy may have been removed (e.g. project teardown) after this cycle's snapshot
            // was taken. Don't restart or notify for a container we're no longer supervising.
            if (!IsStillActive(policy, snapshot))
            {
                return;
            }

            await VerifyScopeAsync(ct).ConfigureAwait(false);
            // on-failure only restarts after a non-zero exit. Other policies restart unconditionally.
            if (policy.Policy == RestartPolicyKind.OnFailure && await ExitedCleanlyAsync(container.Id, ct).ConfigureAwait(false))
            {
                rt.Exhausted = true; // a clean exit is terminal for on-failure
                return;
            }

            rt.LastAttempt = _clock.GetUtcNow();
            rt.RestartCount++;

            Notify(UiText.Get("Automation_RestartingTitle", "Restarting {0}", policy.ContainerName),
                UiText.Get("Automation_Restarting", "Container exited; restarting (attempt {0} of {1}).",
                    rt.RestartCount, policy.MaxRestarts), snapshot);

            await VerifyScopeAsync(ct).ConfigureAwait(false);
            var latest = _observation;
            if (latest is null || latest.Selection != snapshot ||
                !latest.Containers.Any(item => item.Id == container.Id &&
                    item.StateChangedAt == container.StateChangedAt &&
                    item.State is ContainerState.Created or ContainerState.Stopped) ||
                !IsStillActive(policy, snapshot))
                return;
            var start = await _wslc.StartContainerAsync(container.Id, ct, explicitStart: false).ConfigureAwait(false);
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
#if WSLCD_TEST_NO_DISPATCHER
            EvaluationCompleted?.Invoke();
#endif
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

    /// <summary>True when an enabled, valid restart policy for this container still exists in settings.</summary>
    private bool IsStillActive(RestartPolicyConfig policy, WslcSessionContext.SelectionSnapshot snapshot) =>
        _settings.RestartPolicies.Any(p =>
            p.Enabled && p.IsValid &&
            _legacyOwners.OwnerFor(p)?.Matches(_settings.WslcPath, snapshot.Target) == true &&
            string.Equals(p.ContainerName, policy.ContainerName, StringComparison.Ordinal));

    /// <summary>Returns whether user stop intent currently suppresses restarts for this container.</summary>
    public bool IsRestartSuppressed(string containerName) => _suppression.IsSuppressed(containerName, CurrentOwner());

    private AutomationSessionOwner CurrentOwner() =>
        AutomationSessionOwner.Capture(_settings.WslcPath, _sessionContext.Capture());

    /// <summary>Raises notification requests on the UI dispatcher.</summary>
    private void Notify(string title, string message, WslcSessionContext.SelectionSnapshot snapshot) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
                _sessionContext.TryApply(snapshot, () => NotificationRequested?.Invoke(title, message));
        });

    /// <summary>Stops the loop and detaches from the status monitor.</summary>
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

    /// <summary>Mutable restart bookkeeping for one supervised container name.</summary>
    private sealed class Runtime
    {
        public DateTimeOffset? RunningSince;
        public int RestartCount;
        public bool Exhausted;
        public DateTimeOffset LastAttempt = DateTimeOffset.MinValue;
        public volatile bool InProgress;
    }
}
