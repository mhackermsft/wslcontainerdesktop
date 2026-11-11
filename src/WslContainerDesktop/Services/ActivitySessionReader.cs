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

namespace WslContainerDesktop.Services;

/// <summary>One session's recent events or an explicit failure to read them.</summary>
internal sealed record ActivitySessionResult(
    WslcSessionSelection Selection, IReadOnlyList<EngineEvent>? Events, Exception? Failure, bool TimedOut = false);

/// <summary>Recent event observations for the active session generations of one executable.</summary>
internal sealed record ActivitySessionBatch(
    string ExecutablePath, DateTimeOffset Until, IReadOnlyList<ActivitySessionResult> Sessions);

/// <summary>Reads recent events across active sessions without starting a stopped session.</summary>
public sealed class ActivitySessionReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(60);
    private readonly Func<CancellationToken, Task<IReadOnlyList<WslcSessionInfo>>> _discover;
    private readonly Func<string> _executable;
    private readonly Func<string> _defaultName;
    private readonly Func<WslcSessionSelection, CancellationToken, Task> _verify;
    private readonly Func<WslcSessionSelection, IWslcService> _client;
    private readonly ILogger<ActivitySessionReader> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _batchTimeout;

    /// <summary>Creates a reader with guarded session clients.</summary>
    public ActivitySessionReader(WslcSessionInventoryReader reader, ISettingsService settings,
        IWslcSessionSelectionService selector, ILogger<ActivitySessionReader> logger)
        : this(reader.DiscoverAsync, () => settings.WslcPath, WslcSessionActionGuard.ResolveDefaultSessionName,
            selector.VerifyAsync, selector.CreateClient, logger)
    {
    }

    internal ActivitySessionReader(Func<CancellationToken, Task<IReadOnlyList<WslcSessionInfo>>> discover,
        Func<string> executable, Func<string> defaultName,
        Func<WslcSessionSelection, CancellationToken, Task> verify,
        Func<WslcSessionSelection, IWslcService> client, ILogger<ActivitySessionReader> logger,
        TimeSpan? timeout = null, TimeSpan? batchTimeout = null)
    {
        _discover = discover;
        _executable = executable;
        _defaultName = defaultName;
        _verify = verify;
        _client = client;
        _logger = logger;
        _timeout = timeout ?? ReadTimeout;
        _batchTimeout = batchTimeout ?? BatchTimeout;
    }

    internal async Task<ActivitySessionBatch> ReadAsync(
        IReadOnlyDictionary<WslcSessionInfo, DateTimeOffset> previousReads, CancellationToken ct)
    {
        var executable = _executable();
        var sessions = await _discover(ct).ConfigureAwait(false);
        EnsureExecutable(executable);
        var choices = WslcSessionChoices.Create(sessions, executable, _defaultName(),
                UiText.Get("WslEngine_Session_Default", "Default CLI session"))
            .Where(choice => choice.Generation is not null).ToArray();
        var until = DateTimeOffset.UtcNow;
        using var gate = new SemaphoreSlim(2);
        using var batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchTimeout.CancelAfter(_batchTimeout);
        var results = await Task.WhenAll(choices.Select(async choice =>
        {
            try { await gate.WaitAsync(batchTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ActivitySessionResult(choice, null, null, TimedOut: true);
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(batchTimeout.Token);
                timeout.CancelAfter(_timeout);
                try
                {
                    var earliest = until.AddHours(-1);
                    var since = previousReads.TryGetValue(choice.Generation!, out var previous)
                        ? previous.AddSeconds(-2) : earliest;
                    if (since < earliest) since = earliest;
                    if (since > until) since = until;
                    await _verify(choice, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    var events = await _client(choice).GetEventsAsync(since, until, timeout.Token)
                        .WaitAsync(timeout.Token).ConfigureAwait(false);
                    await _verify(choice, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    EnsureExecutable(executable);
                    return new ActivitySessionResult(choice, events, null);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    return new ActivitySessionResult(choice, null, null, TimedOut: true);
                }
                catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex) && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not read activity for session {Session}.", choice.Generation!.Name);
                    return new ActivitySessionResult(choice, null, ex);
                }
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        EnsureExecutable(executable);
        return new(executable, until, results);
    }

    internal Task VerifyAsync(WslcSessionSelection selection, CancellationToken ct) => _verify(selection, ct);
    internal string CurrentExecutablePath => _executable();

    private void EnsureExecutable(string executable)
    {
        if (!string.Equals(executable, _executable(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("WslcSessionInventory_ExecutableChanged",
                "The WSLC executable changed while session inventory was being read."));
    }
}
