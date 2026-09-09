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

using WslContainerDesktop.Helpers;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AssistantTurnDispatchTests
{
    [Fact]
    public void QueuedOldProgressApprovalAndClearCannotChangeNewTurn()
    {
        var generation = 1;
        var queue = new Queue<Action>();
        var state = "new approval";
        foreach (var value in new[] { "old progress", "old approval", "" })
        {
            AssistantTurnDispatch.Queue(1, () => generation, () => true, queue.Enqueue, () => state = value);
        }

        generation = 2;
        while (queue.TryDequeue(out var callback)) callback();

        Assert.Equal("new approval", state);
    }

    [Fact]
    public void CallbackReceivedAfterResetKeepsItsCapturedGeneration()
    {
        var generation = 2;
        var count = 0;
        var queue = new Queue<Action>();
        AssistantTurnDispatch.Queue(1, () => generation, () => true, queue.Enqueue, () => count++);
        queue.Dequeue()();
        Assert.Equal(0, count);
    }

    [Fact]
    public void CompletedTurnRejectsLateCallbacksWithoutNeedingANewTurn()
    {
        var isBusy = true;
        var queue = new Queue<Action>();
        var changed = false;
        AssistantTurnDispatch.Queue(1, () => 1, () => isBusy, queue.Enqueue, () => changed = true);
        isBusy = false;
        queue.Dequeue()();
        Assert.False(changed);
    }

    [Fact]
    public void CurrentTurnUpdatesOnlyAtDispatchAndInOrder()
    {
        var queue = new Queue<Action>();
        var values = new List<string>();
        foreach (var value in new[] { "loading", "narration", "approval", "tool result", "completed" })
            AssistantTurnDispatch.Queue(2, () => 2, () => true, queue.Enqueue, () => values.Add(value));

        Assert.Empty(values);
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal(new[] { "loading", "narration", "approval", "tool result", "completed" }, values);
    }
}
