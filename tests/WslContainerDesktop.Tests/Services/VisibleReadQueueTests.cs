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
using WslContainerDesktop.Helpers;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class VisibleReadQueueTests
{
    [Fact]
    public async Task LargePendingListCreatesOnlyFourActiveReadsAndNeverReadsRemovedRows()
    {
        var started = new ConcurrentQueue<int>();
        var errors = new ConcurrentQueue<Exception>();
        var four = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var queue = new VisibleReadQueue<int>(4, async (key, ct) =>
        {
            started.Enqueue(key);
            if (started.Count == 4) four.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { if (Interlocked.Increment(ref count) == 4) stopped.TrySetResult(); }
        }, errors.Enqueue);
        for (var key = 0; key < 1000; key++) queue.SetVisible(key, true);
        queue.SetEnabled(true);
        await four.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, started.Count);
        for (var key = 4; key < 1000; key++) queue.SetVisible(key, false);
        queue.Clear();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([0, 1, 2, 3], started.Order().ToArray());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DisabledQueueDoesNotStartPendingReadsUntilResumed()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        using var queue = new VisibleReadQueue<int>(1, async (key, ct) =>
        {
            if (key == 1)
            {
                first.TrySetResult();
                await release.Task.WaitAsync(ct);
                completed.TrySetResult();
            }
            else second.TrySetResult();
        }, errors.Enqueue);
        queue.SetVisible(1, true);
        queue.SetVisible(2, true);
        Assert.False(first.Task.IsCompleted);
        queue.SetEnabled(true);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SetEnabled(false);
        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.Task.IsCompleted);
        queue.SetEnabled(true);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ReenteringViewportNeverOverlapsTheSameKeyWithItsCancelledRead()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var reads = 0;
        using var queue = new VisibleReadQueue<int>(4, async (_, ct) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                first.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await finish.Task;
                    throw;
                }
            }
            else second.TrySetResult();
        }, errors.Enqueue);
        queue.SetVisible(1, true);
        queue.SetEnabled(true);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SetVisible(1, false);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SetVisible(1, true);
        Assert.Equal(1, Volatile.Read(ref reads));
        finish.TrySetResult();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task FailedReadIsReportedAndRetriedOnlyAfterRefresh()
    {
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var queue = new VisibleReadQueue<int>(1, (_, _) =>
        {
            if (Interlocked.Increment(ref reads) == 1) throw new IOException("inspect failed");
            retried.TrySetResult();
            return Task.CompletedTask;
        }, ex => error.TrySetResult(ex));
        queue.SetVisible(1, true);
        queue.SetEnabled(true);
        Assert.IsType<IOException>(await error.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref reads));
        queue.Refresh();
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref reads));
    }
}
