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

using System.Text.Json;
using WslContainerDesktop.Models;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AssistantProgressContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ApprovalAndActualExecutionHaveOrderedDistinctProgress()
    {
        var h = new AiContractHarness();
        var updates = new List<AiChatProgress>();
        var approval = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, value) => { if (value is not null) approval.TrySetResult(value); };
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            h.Provider.CapturedRequests[^1].Progress!(new(AiChatProgressKind.Generating, "Generating."));
            await invoke(AiContractHarness.Call(), ct);
            return "model narration";
        });
        var send = h.Assistant.SendAsync("stop", updates.Add);
        var request = await approval.Task.WaitAsync(Deadline);
        Assert.Empty(h.Tools.Executed);
        Assert.Equal(AiChatProgressKind.AwaitingApproval, updates[^1].Kind);
        await h.Assistant.ApproveAsync(request);
        await send.WaitAsync(Deadline);
        Assert.Equal(new[]
        {
            AiChatProgressKind.Loading, AiChatProgressKind.Generating, AiChatProgressKind.ToolRequested,
            AiChatProgressKind.AwaitingApproval, AiChatProgressKind.ExecutingTool,
            AiChatProgressKind.ToolResult, AiChatProgressKind.Completed,
        }, updates.Select(p => p.Kind));
        Assert.Contains("Stopped approved-id", Assert.Single(updates, p => p.Kind == AiChatProgressKind.ToolResult).Text);
        Assert.DoesNotContain(updates, p => p.Kind == AiChatProgressKind.ToolResult && p.Text.Contains("model narration"));
    }

    [Fact]
    public async Task CancellationAtApprovalReportsNotRunWithoutExecuting()
    {
        var h = new AiContractHarness();
        using var cancellation = new CancellationTokenSource();
        var updates = new List<AiChatProgress>();
        var approval = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, value) => { if (value is not null) approval.TrySetResult(value); };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        var send = h.Assistant.SendAsync("stop", updates.Add, cancellation.Token);
        var request = await approval.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Deadline));
        await h.Assistant.ApproveAsync(request);
        Assert.Empty(h.Tools.Executed);
        Assert.Contains(updates, p => p.Kind == AiChatProgressKind.ToolResult && p.Text.StartsWith("Not run:"));
        Assert.Equal(AiChatProgressKind.Cancelled, updates[^1].Kind);
    }

    [Fact]
    public async Task ProviderGenerationCannotHidePendingApproval()
    {
        var h = new AiContractHarness();
        var updates = new List<AiChatProgress>();
        var approval = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, value) => { if (value is not null) approval.TrySetResult(value); };
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            var report = h.Provider.CapturedRequests[^1].Progress!;
            report(new(AiChatProgressKind.Generating, "Generating."));
            report(new(AiChatProgressKind.Generating, "Generating."));
            var tool = invoke(AiContractHarness.Call(), ct);
            var pending = await approval.Task.WaitAsync(Deadline);
            report(new(AiChatProgressKind.Generating, "Still generating."));
            Assert.Equal(AiChatProgressKind.AwaitingApproval, updates[^1].Kind);
            await h.Assistant.RejectAsync(pending);
            await tool;
            return "Rejected.";
        });
        await h.Assistant.SendAsync("stop", updates.Add);
        Assert.Single(updates, p => p.Kind == AiChatProgressKind.Generating);
        Assert.Empty(h.Tools.Executed);
    }

    [Fact]
    public async Task FailureAfterMutationPublishesHonestSanitizedEvidenceWithoutReplay()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        var updates = new List<AiChatProgress>();
        h.Tools.Execute = (_, _) => Task.FromResult("""{"status":"partial","password":"synthetic-stream-secret","outcomes":[{"status":"succeeded","id":"approved-id"}]}""");
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            await invoke(AiContractHarness.Call(), ct);
            throw new IOException("password=synthetic-provider-secret");
        });
        await Assert.ThrowsAsync<IOException>(() => h.Assistant.SendAsync("stop", updates.Add));
        Assert.Contains(updates, p => p.Kind == AiChatProgressKind.ToolResult && p.Text.Contains("\"status\":\"partial\""));
        Assert.Equal(AiChatProgressKind.Failed, updates[^1].Kind);
        Assert.DoesNotContain("synthetic-stream-secret", JsonSerializer.Serialize(updates));
        Assert.DoesNotContain("synthetic-provider-secret", JsonSerializer.Serialize(updates));
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task CancellationKeepsLatePartialToolEvidenceWithoutContinuingInference()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        using var cancellation = new CancellationTokenSource();
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<string>();
        var updates = new List<AiChatProgress>();
        h.Tools.Execute = (_, _) => { entered.SetResult(true); return resume.Task; };
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            await invoke(AiContractHarness.Call(), ct);
            return "must not finish normally";
        });
        var send = h.Assistant.SendAsync("stop", updates.Add, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        resume.SetResult("""{"status":"partial","outcomes":[{"id":"approved-id","status":"succeeded"},{"id":"other","status":"not_run"}]}""");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Deadline));
        Assert.Contains(updates, p => p.Kind == AiChatProgressKind.ToolResult && p.Text.Contains("\"status\":\"partial\""));
        Assert.DoesNotContain(updates, p => p.Kind == AiChatProgressKind.Completed);
        Assert.Single(h.Tools.Executed);
        Assert.Equal(AiChatProgressKind.Cancelled, updates[^1].Kind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"id":"first","id":"second"}""")]
    public async Task MalformedOrPartialArgumentsCannotReachResolver(string arguments)
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(arguments: arguments), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("stop", _ => { }));
        Assert.Empty(h.Tools.Resolved);
        Assert.Empty(h.Tools.Executed);
    }

    [Fact]
    public async Task ResetSuppressesOldProgressAndTerminalWhileFreshTurnCompletes()
    {
        var h = new AiContractHarness();
        var updates = new List<AiChatProgress>();
        var resume = AiContractHarness.Signal<bool>();
        h.Provider.Turns.Enqueue(async (_, _) =>
        {
            var report = h.Provider.CapturedRequests[^1].Progress!;
            await resume.Task;
            Assert.ThrowsAny<OperationCanceledException>(() => report(new(AiChatProgressKind.Generating, "stale")));
            return "stale answer";
        });
        var old = h.Assistant.SendAsync("old", updates.Add);
        h.Assistant.Reset();
        var count = updates.Count;
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("fresh"));
        var fresh = new List<AiChatProgress>();
        await h.Assistant.SendAsync("new", fresh.Add);
        resume.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Deadline));
        Assert.Equal(count, updates.Count);
        Assert.Equal(AiChatProgressKind.Completed, fresh[^1].Kind);
    }

    [Fact]
    public async Task ProviderCannotForgeExecutionProgress()
    {
        var h = new AiContractHarness();
        var updates = new List<AiChatProgress>();
        h.Provider.Turns.Enqueue((_, _) =>
        {
            h.Provider.CapturedRequests[^1].Progress!(new(AiChatProgressKind.ToolResult, "invented success"));
            return Task.FromResult("done");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("stop", updates.Add));
        Assert.DoesNotContain(updates, p => p.Text.Contains("invented success"));
        Assert.Empty(h.Tools.Executed);
    }

    [Fact]
    public async Task FailedCallbackCancelsProviderEvenWhenProviderSwallowsError()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        h.Tools.Execute = (_, _) => throw new IOException("synthetic failure");
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            await Assert.ThrowsAsync<IOException>(() => invoke(AiContractHarness.Call(), ct));
            Assert.True(ct.IsCancellationRequested);
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(AiContractHarness.Call(), CancellationToken.None));
            return "forged success";
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("stop", _ => { }));
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task SensitiveProtocolIdsCannotLeakOrCollapseDistinctToolEvidence()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        var updates = new List<AiChatProgress>();
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            foreach (var id in new[] { "password=firstsecret", "password=secondsecret" })
                await invoke(new AiToolCall { Id = id, Name = "stop_container" }, ct);
            return "done";
        });
        await h.Assistant.SendAsync("stop", updates.Add);
        var outcomes = updates.Where(p => p.Kind == AiChatProgressKind.ToolResult).ToArray();
        Assert.Equal(2, outcomes.Select(p => p.ToolCallId).Distinct().Count());
        Assert.DoesNotContain("firstsecret", JsonSerializer.Serialize(updates));
        Assert.DoesNotContain("secondsecret", JsonSerializer.Serialize(updates));
    }

    [Theory]
    [InlineData(AiChatProgressKind.ToolRequested, false)]
    [InlineData(AiChatProgressKind.ToolRequested, true)]
    [InlineData(AiChatProgressKind.AwaitingApproval, false)]
    [InlineData(AiChatProgressKind.AwaitingApproval, true)]
    [InlineData(AiChatProgressKind.ExecutingTool, false)]
    [InlineData(AiChatProgressKind.ExecutingTool, true)]
    public async Task ReentrantResetOrCancelCannotPublishApprovalOrStartExecution(AiChatProgressKind boundary, bool reset)
    {
        var h = new AiContractHarness();
        using var cancellation = new CancellationTokenSource();
        if (boundary == AiChatProgressKind.ExecutingTool)
            h.AutoApproved.Add("stop_container");
        h.Assistant.ApprovalChanged += (_, approval) => Assert.Null(approval);
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Assistant.SendAsync("stop", progress =>
        {
            if (progress.Kind != boundary) return;
            if (reset) h.Assistant.Reset();
            else cancellation.Cancel();
        }, cancellation.Token));
        Assert.Empty(h.Tools.Executed);
        if (boundary == AiChatProgressKind.ToolRequested)
            Assert.Empty(h.Tools.Resolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolDeadlineStartsAfterApprovalAndReportsTimeoutNotUserCancellation(bool latePartial)
    {
        var clock = new ManualDeadlineClock();
        var h = new AiContractHarness(timeProvider: clock);
        using var cancellation = new CancellationTokenSource();
        var approval = AiContractHarness.Signal<AssistantApprovalRequest>();
        var executing = AiContractHarness.Signal<bool>();
        var completion = AiContractHarness.Signal<string>();
        var updates = new List<AiChatProgress>();
        h.Assistant.ApprovalChanged += (_, value) => { if (value is not null) approval.TrySetResult(value); };
        h.Tools.Execute = async (_, token) =>
        {
            executing.SetResult(true);
            if (latePartial) return await completion.Task;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        var send = h.Assistant.SendAsync("stop", updates.Add, cancellation.Token);
        var pending = await approval.Task.WaitAsync(Deadline);
        Assert.Null(clock.Timer);
        await h.Assistant.ApproveAsync(pending);
        await executing.Task.WaitAsync(Deadline);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.DueTime);
        clock.Timer!.Fire();
        completion.SetResult("""{"status":"partial","outcomes":[{"id":"approved-id","status":"succeeded"}]}""");
        await Assert.ThrowsAsync<TimeoutException>(() => send.WaitAsync(Deadline));
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Contains(updates, p => p.Kind == AiChatProgressKind.ToolResult &&
            p.Text.Contains(latePartial ? "\"status\":\"partial\"" : "unknown"));
        Assert.Equal(AiChatProgressKind.Failed, updates[^1].Kind);
        Assert.Single(h.Tools.Executed);
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        public ManualTimer? Timer { get; private set; }
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return Timer = new ManualTimer(callback, state);
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public void Fire() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
