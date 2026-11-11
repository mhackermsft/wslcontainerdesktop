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

namespace WslContainerDesktop.Services;

internal static class AutomationSessionObservationScheduler
{
    internal static async Task<(int NextOffset, bool TimedOut)> RunBatchedAsync<T>(
        IReadOnlyList<T> sessions, int offset, Func<IReadOnlyList<T>, CancellationToken, Task> observe,
        CancellationToken ct, TimeSpan? maximumDuration = null)
    {
        if (sessions.Count == 0) return (0, false);
        offset %= sessions.Count;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(maximumDuration ?? TimeSpan.FromSeconds(5));
        var attempted = 0;
        try
        {
            while (attempted < sessions.Count)
            {
                budget.Token.ThrowIfCancellationRequested();
                var count = Math.Min(2, sessions.Count - attempted);
                var batch = Enumerable.Range(0, count)
                    .Select(index => sessions[(offset + attempted + index) % sessions.Count]).ToArray();
                attempted += count;
                await observe(batch, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            return ((offset + attempted) % sessions.Count, true);
        }
        ct.ThrowIfCancellationRequested();
        return ((offset + attempted) % sessions.Count, false);
    }

    internal static async Task RunAsync<T>(IReadOnlyList<T> sessions,
        Func<T, CancellationToken, Task> observe, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(sessions.Select(async session =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                await observe(session, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }
}
