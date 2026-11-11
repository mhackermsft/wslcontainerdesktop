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

namespace WslContainerDesktop.Services;

/// <summary>Scopes local AI setup and removal to an explicitly chosen session.</summary>
public sealed class LocalAiSessionCoordinator(
    IWslcSessionSelectionService selector,
    WslcSessionInventoryReader reader,
    WslcSessionContext management,
    DialogService dialogs,
    IAiCapabilityService capabilities,
    ILogger<LocalAiSetupService> setupLogger,
    ILogger<LocalAiSessionCoordinator> logger)
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly LocalAiPresenceTracker _presence = new();

    internal async Task<LocalAiSessionScope?> OpenAsync(CancellationToken ct = default, bool startDefault = true)
    {
        await _operationGate.WaitAsync(ct);
        IDisposable? lease = null;
        var releaseGate = true;
        try
        {
            lease = management.LockSelection();
            var target = await SessionOperationTarget.ChooseAsync(selector, dialogs, logger, ct, startDefault);
            if (target is null) return null;
            var scope = new LocalAiSessionScope(target,
                new LocalAiSetupService(target.Client, capabilities, setupLogger), lease, _operationGate);
            lease = null;
            releaseGate = false;
            return scope;
        }
        finally
        {
            try { lease?.Dispose(); }
            finally { if (releaseGate) _operationGate.Release(); }
        }
    }

    internal async Task<(bool Present, bool Complete)> ReadPresenceAsync(CancellationToken ct = default)
    {
        var sessions = await reader.DiscoverAsync(ct);
        var inventories = await reader.ReadContainersAsync(sessions, ct);
        return (inventories.Any(item => item.Containers.Items?.Any(container =>
                string.Equals(container.Name.TrimStart('/'), LocalAiSetupService.ManagedContainerName, StringComparison.OrdinalIgnoreCase)) == true),
            inventories.All(item => item.IsComplete));
    }

    internal long BeginPresenceRead() => _presence.Begin();

    internal bool IsCurrentPresence(long revision) => _presence.IsCurrent(revision);

    internal void InvalidatePresence() => _presence.Invalidate();
}

internal sealed class LocalAiPresenceTracker
{
    private long _revision;

    internal long Begin() => Interlocked.Increment(ref _revision);

    internal bool IsCurrent(long revision) => revision == Volatile.Read(ref _revision);

    internal void Invalidate() => Interlocked.Increment(ref _revision);
}

internal sealed class LocalAiSessionScope(
    SessionOperationTarget target, ILocalAiSetupService service, IDisposable lease, SemaphoreSlim operationGate) : IDisposable
{
    private int _disposed;
    internal SessionOperationTarget Target { get; } = target;
    internal ILocalAiSetupService Service { get; } = service;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { lease.Dispose(); }
        finally { operationGate.Release(); }
    }
}
