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

using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Why a session's statistics are incomplete.</summary>
public enum DashboardStatsFailure { SessionChanged, IdentityUnknown, Missing, InvalidSample, TimedOut, ReadFailed }

/// <summary>Statistics for the observed running containers in one session.</summary>
public sealed record DashboardSessionStats(
    WslcSessionInfo Session, IReadOnlyList<DashboardContainerStats> Containers,
    DashboardStatsFailure? Failure = null, string? Detail = null);

/// <summary>A container and its statistics, or an unavailable statistics sample.</summary>
public sealed record DashboardContainerStats(ContainerInfo Container, ContainerStats? Stats);

/// <summary>Reads live statistics from explicitly observed session generations.</summary>
public sealed class DashboardStatsReader
{
    internal static TimeSpan PollingDelay(int reportingSessions) =>
        TimeSpan.FromSeconds(reportingSessions == 0 ? 15 : Math.Clamp(reportingSessions, 2, 5));

    private readonly WslcSessionInventoryReader _reader;
    private readonly Func<WslcSessionInfo, string, IWslcService> _createClient;
    private readonly ILogger<DashboardStatsReader> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _batchTimeout;

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates a reader using session-bound clients.</summary>
    public DashboardStatsReader(WslcSessionInventoryReader reader, WslcSessionClientFactory clients,
        ILogger<DashboardStatsReader> logger)
        : this(reader, (session, executable) => clients.Create(session, executable, managed: false), logger)
    {
    }
#endif

    internal DashboardStatsReader(WslcSessionInventoryReader reader,
        Func<WslcSessionInfo, string, IWslcService> createClient, ILogger<DashboardStatsReader> logger,
        TimeSpan? timeout = null, TimeSpan? batchTimeout = null)
    {
        _reader = reader;
        _createClient = createClient;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _batchTimeout = batchTimeout ?? TimeSpan.FromSeconds(25);
    }

    /// <summary>Reads each session separately and rejects changed or ambiguous identities.</summary>
    public async Task<IReadOnlyList<DashboardSessionStats>> ReadAsync(
        IReadOnlyList<WslcCombinedInventory.Resource<ContainerInfo>> containers, string executable,
        CancellationToken ct = default)
    {
        var groups = containers.Where(row => row.Value.State == ContainerState.Running)
            .GroupBy(row => row.Session)
            .Select(group => (Session: group.Key, Containers: group.Select(row => row.Value).ToArray()))
            .ToArray();
        if (groups.Length == 0)
            return [];

        EnsureExecutable(executable);
        var before = await DiscoverAsync(ct).ConfigureAwait(false);
        EnsureExecutable(executable);
        using var concurrency = new SemaphoreSlim(2);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batch.CancelAfter(_batchTimeout);
        var results = await Task.WhenAll(groups.Select(async group =>
        {
            var acquired = false;
            try
            {
                await concurrency.WaitAsync(batch.Token).ConfigureAwait(false);
                acquired = true;
                batch.Token.ThrowIfCancellationRequested();
                if (group.Session.ID <= 0 || group.Session.CreatorPid <= 0 ||
                    !before.Contains(group.Session))
                    return Unavailable(group.Session, group.Containers, DashboardStatsFailure.SessionChanged);
                return await ReadSessionAsync(group.Session, group.Containers, executable, batch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && batch.IsCancellationRequested)
            {
                return Unavailable(group.Session, group.Containers, DashboardStatsFailure.TimedOut);
            }
            finally { if (acquired) concurrency.Release(); }
        })).ConfigureAwait(false);

        var after = await DiscoverAsync(ct).ConfigureAwait(false);
        EnsureExecutable(executable);
        return results.Select(result => after.Contains(result.Session)
            ? result
            : Unavailable(result.Session, result.Containers.Select(row => row.Container).ToArray(),
                DashboardStatsFailure.SessionChanged))
            .ToArray();
    }

    private async Task<IReadOnlyList<WslcSessionInfo>> DiscoverAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            return await _reader.DiscoverAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.DiscoveryTimedOut);
        }
    }

    private async Task<DashboardSessionStats> ReadSessionAsync(WslcSessionInfo session,
        IReadOnlyList<ContainerInfo> containers, string executable, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var ids = containers.Select(container => container.Id).ToArray();
            if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                return Unavailable(session, containers, DashboardStatsFailure.IdentityUnknown);
            timeout.Token.ThrowIfCancellationRequested();
            var stats = await _createClient(session, executable).GetStatsAsync(timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            var matched = new Dictionary<string, ContainerStats>(StringComparer.Ordinal);
            foreach (var item in stats)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Id))
                    return Unavailable(session, containers, DashboardStatsFailure.IdentityUnknown);
                var id = ContainerIdentity.ResolveId(ids, item.Id);
                if (id is not null)
                {
                    if (!IsValidPercent(item.CpuPercent) || !IsValidPercent(item.MemPercent))
                        return Unavailable(session, containers, DashboardStatsFailure.InvalidSample);
                    if (!matched.TryAdd(id, item))
                        return Unavailable(session, containers, DashboardStatsFailure.IdentityUnknown);
                }
            }
            var rows = containers.Select(container => new DashboardContainerStats(container,
                matched.GetValueOrDefault(container.Id))).ToArray();
            return new(session, rows, rows.Any(row => row.Stats is null)
                ? DashboardStatsFailure.Missing
                : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Unavailable(session, containers, DashboardStatsFailure.TimedOut);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Dashboard statistics were unexpectedly cancelled for session {Session}.", session.Name);
            return Unavailable(session, containers, DashboardStatsFailure.ReadFailed, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or
                                   ArgumentException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not read dashboard statistics for session {Session}.", session.Name);
            return Unavailable(session, containers, DashboardStatsFailure.ReadFailed, ex.Message);
        }
    }

    private void EnsureExecutable(string executable)
    {
        if (!string.Equals(executable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
    }

    private static bool IsValidPercent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var text = value.Trim();
        if (text.EndsWith('%'))
            text = text[..^1].Trim();
        return !text.Contains('%') &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed >= 0;
    }

    private static DashboardSessionStats Unavailable(WslcSessionInfo session,
        IReadOnlyList<ContainerInfo> containers, DashboardStatsFailure failure, string? detail = null) =>
        new(session, containers.Select(container => new DashboardContainerStats(container, null)).ToArray(), failure, detail);
}
