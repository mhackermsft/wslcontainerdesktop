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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Live health enrichment owned by the status poller, not a second polling loop.</summary>
public sealed class NativeHealthMonitor
{
    private readonly ConcurrentDictionary<string, (ulong Generation, DateTimeOffset Expires, NativeHealthObservation Value)> _absent = new();
    private int _offset;

    public void Invalidate() => _absent.Clear();

    public async Task RefreshAsync(IReadOnlyList<ContainerInfo> containers,
        Func<string, CancellationToken, Task<CommandResult>> inspect, Action<string> report,
        CancellationToken ct = default)
    {
        var present = containers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _absent.Keys)
            if (!present.Contains(id))
                _absent.TryRemove(id, out _);
        var running = containers.Where(c => c.State == ContainerState.Running).ToArray();
        if (running.Length == 0)
            return;
        // Rotate the queue so an engine slowdown cannot permanently starve later rows.
        var offset = _offset % running.Length;
        _offset = (offset + 4) % running.Length;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        using var concurrency = new SemaphoreSlim(4);
        await Task.WhenAll(running.Skip(offset).Concat(running.Take(offset)).Select(async container =>
        {
            if (_absent.TryGetValue(container.Id, out var cached) &&
                cached.Generation == container.StateChangedAt && cached.Expires > DateTimeOffset.UtcNow)
            {
                container.NativeHealth = cached.Value;
                return;
            }
            var entered = false;
            try
            {
                await concurrency.WaitAsync(budget.Token).ConfigureAwait(false);
                entered = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var result = await inspect(container.Id, timeout.Token).ConfigureAwait(false);
                var inspectedId = result.Success ? NativeHealthParser.ContainerId(result.StandardOutput) : null;
                var matches = inspectedId is not null && (inspectedId == container.Id ||
                    container.Id.Length >= 12 && inspectedId.StartsWith(container.Id, StringComparison.Ordinal));
                container.NativeHealth = result.Success && matches
                    ? NativeHealthParser.Parse(result.StandardOutput)
                    : result.Success
                        ? new(NativeHealthState.Unknown, Diagnostic: "Health inspect identity was missing or did not match the container.")
                    : new(NativeHealthState.Unknown, Diagnostic: "Health inspect failed: " + result.ErrorText);
                if (container.NativeHealth.State is NativeHealthState.Absent or NativeHealthState.Disabled)
                    _absent[container.Id] = (container.StateChangedAt, DateTimeOffset.UtcNow.AddMinutes(5), container.NativeHealth);
                if (container.NativeHealth.Diagnostic is { } diagnostic)
                    report($"{container.Name}: {diagnostic}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                container.NativeHealth = new(NativeHealthState.Unknown, Diagnostic: "Health observation timed out; status is unknown.");
                report($"{container.Name}: health observation timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                container.NativeHealth = new(NativeHealthState.Unknown, Diagnostic: $"Health observation unavailable: {ex.Message}");
                report($"{container.Name}: {ex.Message}");
            }
            finally
            {
                if (entered) concurrency.Release();
            }
        })).ConfigureAwait(false);
    }
}
