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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AutomationSessionObservationSchedulerTests
{
    [Fact]
    public async Task StartupBudgetPublishesEarlierPairsAndRotatesPastAnUnresponsivePair()
    {
        var published = new List<int>();
        var attempted = new List<int>();
        var result = await AutomationSessionObservationScheduler.RunBatchedAsync([1, 2, 3, 4, 5], 0,
            async (batch, ct) =>
            {
                Assert.InRange(batch.Count, 1, 2);
                attempted.AddRange(batch);
                if (batch.Contains(3))
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                published.AddRange(batch);
            }, CancellationToken.None, TimeSpan.FromMilliseconds(100));

        Assert.True(result.TimedOut);
        Assert.Equal([1, 2], published);
        Assert.Equal([1, 2, 3, 4], attempted);
        Assert.Equal(4, result.NextOffset);

        var resumed = new List<int>();
        var next = await AutomationSessionObservationScheduler.RunBatchedAsync([1, 2, 3, 4, 5], result.NextOffset,
            (batch, _) =>
            {
                resumed.AddRange(batch);
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.Equal([5, 1, 2, 3, 4], resumed);
        Assert.False(next.TimedOut);
    }

    [Fact]
    public async Task StartupBudgetDoesNotSwallowApplicationCancellation()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AutomationSessionObservationScheduler.RunBatchedAsync([1, 2, 3], 0, (batch, ct) =>
            {
                stop.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }, stop.Token));
    }

    [Fact]
    public async Task SlowSessionDoesNotDelayPublishingOtherSessions()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentQueue<int>();
        var active = 0;
        var maximum = 0;
        async Task Observe(int session, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref active);
            int previous;
            do
            {
                previous = Volatile.Read(ref maximum);
                if (previous >= count) break;
            } while (Interlocked.CompareExchange(ref maximum, count, previous) != previous);
            try
            {
                if (session == 1) await release.Task.WaitAsync(ct);
                seen.Enqueue(session);
                if (session == 3) third.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sweep = AutomationSessionObservationScheduler.RunAsync([1, 2, 3], Observe, timeout.Token);
        try
        {
            await third.Task.WaitAsync(timeout.Token);
            Assert.Contains(2, seen);
            Assert.Contains(3, seen);
            Assert.DoesNotContain(1, seen);
            Assert.True(maximum <= 2);
        }
        finally { release.TrySetResult(); }
        await sweep;
    }

    [Fact]
    public async Task CancellingSweepStopsActiveAndQueuedObservations()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var active = 0;
        using var cancellation = new CancellationTokenSource();
        async Task Observe(int session, CancellationToken ct)
        {
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref count) == 2) started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Interlocked.Decrement(ref active); }
        }

        var sweep = AutomationSessionObservationScheduler.RunAsync([1, 2, 3], Observe, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep);
        Assert.Equal(0, active);
    }
}
