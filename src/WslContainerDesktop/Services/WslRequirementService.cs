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

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Background-friendly checker for whether the configured <c>wslc.exe</c> is installed, allowed by policy, and new enough.
/// StatusMonitor and other services observe this instead of each probing the engine separately.
/// </summary>
/// <remarks>
/// Results are published through an optional UI-thread enqueue callback. Transient failures schedule automatic rechecks with backoff so the app recovers after WSL updates or install changes.
/// </remarks>
public sealed class WslRequirementService : IWslRequirementService, IDisposable
{
    private static readonly TimeSpan[] TransientRetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly ISettingsService _settings;
    private readonly IWslPolicyService _policy;
    private readonly Func<Action, bool>? _enqueue;
    private readonly ILogger<WslRequirementService> _logger;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>> _run;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _retryGate = new();
    private CancellationTokenSource? _autoRecheckCts;
    private int _transientRetryAttempt;
    private bool _disposed;

    /// <summary>Creates the requirement checker using the real process runner.</summary>
    public WslRequirementService(
        ISettingsService settings,
        IWslPolicyService policy,
        Func<Action, bool> enqueue,
        ILogger<WslRequirementService> logger)
        : this(settings, policy, enqueue, logger, ProcessRunner.RunAtPathAsync)
    {
    }

    /// <summary>Creates the requirement checker with injectable process and delay delegates for tests.</summary>
    internal WslRequirementService(
        ISettingsService settings,
        IWslPolicyService policy,
        Func<Action, bool>? enqueue,
        ILogger<WslRequirementService> logger,
        Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>> run,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _policy = policy;
        _enqueue = enqueue;
        _logger = logger;
        _run = run;
        _delay = delay ?? Task.Delay;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Most recent requirement status reported to observers.</summary>
    public WslRequirementStatus Current { get; private set; } =
        new(WslRequirementState.Unknown, Diagnostic: "Checking the configured WSL container engine.");

    /// <summary>Whether at least one explicit or automatic check has completed.</summary>
    public bool HasCompletedInitialCheck { get; private set; }

    /// <summary>Raised whenever a newly evaluated requirement status is published.</summary>
    public event EventHandler<WslRequirementStatus>? Changed;

    /// <summary>Runs a serialized requirement check and publishes the result.</summary>
    public async Task<WslRequirementStatus> RecheckAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = await EvaluateAsync(ct).ConfigureAwait(false);
            Publish(status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Performs the actual policy, path, process, and version checks.</summary>
    private async Task<WslRequirementStatus> EvaluateAsync(CancellationToken ct)
    {
        try
        {
            var policy = _policy.GetPolicy();
            if (policy.WslContainersDisabled)
            {
                return new(WslRequirementState.DisabledByPolicy,
                    Diagnostic: "Your organization disabled WSL containers by policy.");
            }

            var path = _settings.WslcPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new(WslRequirementState.NotInstalled,
                    Diagnostic: $"Could not find wslc.exe at the configured path: {path}");
            }

            var result = await _run(path, ["--version"], ct).ConfigureAwait(false);
            var combined = result.StandardOutput + "\n" + result.StandardError;
            if (!result.Success)
            {
                if (CommandErrorText.IsWslContainersDisabled(combined))
                {
                    return new(WslRequirementState.DisabledByPolicy,
                        Diagnostic: "Your organization disabled WSL containers by policy.");
                }

                return new(WslRequirementState.Unknown,
                    Diagnostic: CommandErrorText.Friendly(result.ErrorText));
            }

            if (!WslcVersionParser.TryParseOutput(combined, out var found, out var parsed))
            {
                return new(WslRequirementState.Unknown,
                    Diagnostic: "Could not determine the wslc version from the configured executable.");
            }

            if (parsed < WslcRequirements.MinimumVersion)
            {
                return new(WslRequirementState.TooOld, found,
                    "Select Update WSL, or run 'wsl --update' in a terminal, to install the latest WSL release.");
            }

            return WslRequirementStatus.Ok(found);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "WSL requirement check failed.");
            return new(WslRequirementState.Unknown, Diagnostic: ex.Message);
        }
    }

    /// <summary>Stores a result, schedules follow-up work, and raises change notifications.</summary>
    private void Publish(WslRequirementStatus status)
    {
        Current = status;
        HasCompletedInitialCheck = true;
        ScheduleAutoRecheck(status.State);
        if (_enqueue is null || !_enqueue(() => Changed?.Invoke(this, status)))
        {
            Changed?.Invoke(this, status);
        }
    }

    /// <summary>Schedules retry checks for transient or user-fixable requirement states.</summary>
    private void ScheduleAutoRecheck(WslRequirementState state)
    {
        CancellationTokenSource cts;
        TimeSpan delay;
        lock (_retryGate)
        {
            _autoRecheckCts?.Cancel();
            _autoRecheckCts = null;

            if (_disposed || state == WslRequirementState.Ok)
            {
                _transientRetryAttempt = 0;
                return;
            }

            delay = state switch
            {
                WslRequirementState.Unknown or WslRequirementState.NotInstalled => TransientRetryDelays[
                    Math.Min(_transientRetryAttempt++, TransientRetryDelays.Length - 1)],
                WslRequirementState.TooOld or WslRequirementState.DisabledByPolicy => TimeSpan.FromSeconds(60),
                _ => TimeSpan.FromSeconds(60),
            };

            if (state is WslRequirementState.TooOld or WslRequirementState.DisabledByPolicy)
            {
                _transientRetryAttempt = 0;
            }

            cts = new CancellationTokenSource();
            _autoRecheckCts = cts;
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _delay(delay, token).ConfigureAwait(false);
                await RecheckAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Automatic WSL requirement refresh failed.");
            }
            finally
            {
                lock (_retryGate)
                {
                    if (ReferenceEquals(_autoRecheckCts, cts))
                    {
                        _autoRecheckCts = null;
                    }
                }

                cts.Dispose();
            }
        });
    }

    /// <summary>Cancels any pending automatic recheck and optionally resets retry backoff.</summary>
    private void CancelAutoRecheck(bool resetBackoff)
    {
        lock (_retryGate)
        {
            _autoRecheckCts?.Cancel();
            _autoRecheckCts = null;
            if (resetBackoff)
            {
                _transientRetryAttempt = 0;
            }
        }
    }

    /// <summary>Restarts requirement evaluation after settings are saved.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        CancelAutoRecheck(resetBackoff: true);
        _ = Task.Run(async () =>
        {
            try
            {
                await RecheckAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WSL requirement refresh after settings change failed.");
            }
        });
    }

    /// <summary>Stops pending retries, unsubscribes from settings, and disposes synchronization state.</summary>
    public void Dispose()
    {
        lock (_retryGate)
        {
            _disposed = true;
        }

        CancelAutoRecheck(resetBackoff: false);
        _settings.Changed -= OnSettingsChanged;
        _gate.Dispose();
    }
}
