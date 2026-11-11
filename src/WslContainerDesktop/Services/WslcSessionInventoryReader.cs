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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Reads explicitly selected, active WSLC sessions without changing the app's management scope.</summary>
public sealed class WslcSessionInventoryReader
{
    private const int MaxConcurrentSessions = 2;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(45);
    private readonly IWslcService _discovery;
    private readonly Func<WslcSessionInfo, string, IWslcService> _createReader;
    private readonly Func<string> _executablePath;
    private readonly ILogger<WslcSessionInventoryReader> _logger;
    private readonly TimeSpan _sessionTimeout;

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates a reader using a separate immutable runner and cache for each session.</summary>
    public WslcSessionInventoryReader(
        IWslcService discovery,
        ISettingsService settings,
        WslcSessionClientFactory clients,
        ILogger<WslcSessionInventoryReader> logger)
        : this(discovery,
            (session, executable) => clients.Create(session, executable, managed: false),
            () => settings.WslcPath,
            logger)
    {
    }
#endif

    internal WslcSessionInventoryReader(
        IWslcService discovery,
        Func<WslcSessionInfo, string, IWslcService> createReader,
        Func<string> executablePath,
        ILogger<WslcSessionInventoryReader> logger,
        TimeSpan? sessionTimeout = null)
    {
        _discovery = discovery;
        _createReader = createReader;
        _executablePath = executablePath;
        _logger = logger;
        _sessionTimeout = sessionTimeout ?? SessionTimeout;
    }

    internal string CurrentExecutablePath => _executablePath();

    /// <summary>Discovers currently active sessions; it never creates the default CLI session.</summary>
    public async Task<IReadOnlyList<WslcSessionInfo>> DiscoverAsync(CancellationToken ct = default)
    {
        var executable = _executablePath();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_sessionTimeout);
        try
        {
            var information = await _discovery.GetSystemInfoAsync(timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            timeout.Token.ThrowIfCancellationRequested();
            EnsureSameExecutable(executable);
            if (information.Server?.Sessions is null)
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.InvalidSessions);
            ValidateSessions(information.Server.Sessions);
            return information.Server.Sessions.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.DiscoveryTimedOut);
        }
    }

    /// <summary>
    /// Reads only the supplied session generations and discards results for sessions that changed
    /// while the read was in progress. This method does not start a poller or manage containers.
    /// </summary>
    public async Task<IReadOnlyList<WslcSessionInventory>> ReadAsync(
        IReadOnlyList<WslcSessionInfo> sessions, CancellationToken ct = default, bool containersOnly = false)
    {
        ValidateSessions(sessions);
        var executable = _executablePath();
        var before = await DiscoverAsync(ct).ConfigureAwait(false);
        EnsureSameExecutable(executable);
        using var gate = new SemaphoreSlim(MaxConcurrentSessions);
        var results = await Task.WhenAll(sessions.Select(async session =>
        {
            if (!ContainsGeneration(before, session))
                return Changed(session);

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_sessionTimeout);
                var reader = _createReader(session, executable);
                var containers = ReadPartAsync(
                    token => reader.ListContainersAsync(all: true, ct: token), "containers", session, timeout.Token, ct);
                if (containersOnly)
                    return new WslcSessionInventory(session, await containers.ConfigureAwait(false),
                        new(null), new(null), new(null), ObservedAt: DateTimeOffset.UtcNow);
                var images = ReadPartAsync(token => reader.ListImagesAsync(token), "images", session, timeout.Token, ct);
                var volumes = ReadPartAsync(token => reader.ListVolumesAsync(token), "volumes", session, timeout.Token, ct);
                var networks = ReadPartAsync(token => ReadNetworksAsync(reader, token), "networks", session, timeout.Token, ct);
                await Task.WhenAll(containers, images, volumes, networks).ConfigureAwait(false);
                return new WslcSessionInventory(session, await containers.ConfigureAwait(false),
                    await images.ConfigureAwait(false), await volumes.ConfigureAwait(false), await networks.ConfigureAwait(false),
                    ObservedAt: DateTimeOffset.UtcNow);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var after = await DiscoverAsync(ct).ConfigureAwait(false);
        EnsureSameExecutable(executable);
        return results.Select(result => ContainsGeneration(after, result.Session) ? result : Changed(result.Session)).ToArray();
    }

    /// <summary>Reads container lists only, without probing images, volumes or networks.</summary>
    public async Task<IReadOnlyList<WslcSessionContainerInventory>> ReadContainersAsync(
        IReadOnlyList<WslcSessionInfo> sessions, CancellationToken ct = default, TimeSpan? sessionTimeout = null)
    {
        ValidateSessions(sessions);
        var executable = _executablePath();
        var before = await DiscoverAsync(ct).ConfigureAwait(false);
        EnsureSameExecutable(executable);
        return await ReadContainersAsync(sessions, before, executable, ct, sessionTimeout).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<WslcSessionContainerInventory>> ReadContainersAsync(
        IReadOnlyList<WslcSessionInfo> sessions, IReadOnlyList<WslcSessionInfo> discoveredBefore,
        string expectedExecutable, CancellationToken ct = default, TimeSpan? sessionTimeout = null)
    {
        ValidateSessions(sessions);
        ValidateSessions(discoveredBefore);
        if (!string.Equals(expectedExecutable, _executablePath(), StringComparison.OrdinalIgnoreCase))
            throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
        var executable = expectedExecutable;
        using var gate = new SemaphoreSlim(MaxConcurrentSessions);
        var results = await Task.WhenAll(sessions.Select(async session =>
        {
            if (!ContainsGeneration(discoveredBefore, session))
                return ChangedContainers(session, DateTimeOffset.UtcNow);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var observedAt = DateTimeOffset.UtcNow;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(sessionTimeout ?? _sessionTimeout);
                var reader = _createReader(session, executable);
                var containers = await ReadPartAsync(
                    token => reader.ListContainersAsync(all: true, ct: token, resolvePorts: false),
                    "containers", session, timeout.Token, ct)
                    .ConfigureAwait(false);
                return new WslcSessionContainerInventory(session, containers, ObservedAt: observedAt);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var after = await DiscoverAsync(ct).ConfigureAwait(false);
        EnsureSameExecutable(expectedExecutable);
        return results.Select(result => ContainsGeneration(after, result.Session)
            ? result : ChangedContainers(result.Session, result.ObservedAt ?? DateTimeOffset.UtcNow)).ToArray();
    }

    private async Task<WslcInventoryPart<T>> ReadPartAsync<T>(
        Func<CancellationToken, Task<IReadOnlyList<T>>> read, string kind, WslcSessionInfo session,
        CancellationToken timeout, CancellationToken caller)
    {
        try
        {
            var result = await read(timeout).WaitAsync(timeout).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            timeout.ThrowIfCancellationRequested();
            return new WslcInventoryPart<T>(result);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            _logger.LogWarning("Session {Session} {Kind} inventory timed out.", session.Name, kind);
            return new WslcInventoryPart<T>(null, TimedOut: true);
        }
        catch (OperationCanceledException ex) when (!caller.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Session {Session} {Kind} inventory was unexpectedly cancelled.", session.Name, kind);
            return new WslcInventoryPart<T>(null, UnexpectedCancellation: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Session {Session} {Kind} inventory failed.", session.Name, kind);
            return new WslcInventoryPart<T>(null, ex.Message);
        }
    }

    private static async Task<IReadOnlyList<NetworkInfo>> ReadNetworksAsync(IWslcService reader, CancellationToken ct) =>
        NetworkDisplayList.Create(await reader.ListNetworksAsync(ct).ConfigureAwait(false));

    private void EnsureSameExecutable(string expected)
    {
        if (!string.Equals(expected, _executablePath(), StringComparison.OrdinalIgnoreCase))
            throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
    }

    private static bool ContainsGeneration(IReadOnlyList<WslcSessionInfo> sessions, WslcSessionInfo expected) =>
        sessions.Any(session => session.ID == expected.ID && session.CreatorPid == expected.CreatorPid &&
            string.Equals(session.Name, expected.Name, StringComparison.Ordinal));

    private static WslcSessionInventory Changed(WslcSessionInfo session) => new(
        session, new(null), new(null), new(null), new(null), SessionChanged: true, ObservedAt: DateTimeOffset.UtcNow);

    private static WslcSessionContainerInventory ChangedContainers(WslcSessionInfo session, DateTimeOffset observedAt) =>
        new(session, new(null), SessionChanged: true, ObservedAt: observedAt);

    private static void ValidateSessions(IReadOnlyList<WslcSessionInfo> sessions)
    {
        if (sessions.Any(session => session is null || session.ID <= 0 || string.IsNullOrWhiteSpace(session.Name)) ||
            sessions.Select(session => session.Name).Distinct(StringComparer.Ordinal).Count() != sessions.Count ||
            sessions.Select(session => session.ID).Distinct().Count() != sessions.Count)
            throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.AmbiguousSessions);
    }
}
