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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class GitHubCopilotProviderContractTests
{
    private static readonly AiToolDefinition[] Tools =
    [
        new() { Name = "inspect_container", Description = "Inspect synthetic data", JsonSchemaParameters = """{"type":"object"}""" },
    ];

    private static AiChatRequest Request() => new(
        new(AiProviderKind.GitHubCopilot, "github-copilot", "captured-model"),
        [
            new() { Role = "system", Content = "Synthetic trusted instructions" },
            new() { Role = "user", Content = "Inspect\npassword: synthetic-history-secret" },
        ]);

    [Fact]
    public async Task SessionSeamReceivesCapturedConfigurationAndSanitizedHistoryAndReturnsPairedTranscript()
    {
        var h = new AiContractHarness();
        var original = new AiToolCall
        {
            Id = "sdk-call", Name = "inspect_container", ArgumentsJson = """{"password":"synthetic-call-secret","id":"approved-id"}""",
        };
        var request = Request();
        var provider = new CopilotChatTurnRunner(
            async (configuration, history, tools, invoke, ct) =>
            {
                Assert.Same(request.Configuration, configuration);
                Assert.Equal("captured-model", configuration.Model);
                Assert.DoesNotContain("synthetic-history-secret", JsonSerializer.Serialize(history));
                Assert.Equal(Tools[0].Name, Assert.Single(tools).Name);
                h.SettingsValues[nameof(ISettingsService.AiGitHubCopilotModel)] = "changed-model";
                var outcome = await invoke(original, ct);
                Assert.DoesNotContain("synthetic-result-secret", outcome);
                Assert.Contains("partial", outcome);
                return "Final response\npassword: synthetic-final-secret";
            });
        var result = await provider.RunTurnAsync(request, Tools, (call, _) =>
        {
            Assert.Same(original, call);
            Assert.Contains("synthetic-call-secret", call.ArgumentsJson);
            return Task.FromResult("""{"status":"partial","password":"synthetic-result-secret"}""");
        }, CancellationToken.None);

        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("assistant", result.Messages[0].Role);
        Assert.Equal(original.Id, Assert.Single(result.Messages[0].ToolCalls).Id);
        Assert.Equal(original.Id, result.Messages[1].ToolCallId);
        Assert.Equal(original.Name, result.Messages[1].ToolName);
        Assert.Equal(result.FinalText, result.Messages[2].Content);
        foreach (var secret in new[] { "history", "call", "result", "final" })
            Assert.DoesNotContain($"synthetic-{secret}-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task OversizedActiveContextStopsSdkContinuationWithoutRetry()
    {
        var calls = 0;
        var sessionRuns = 0;
        var returnedOutcomes = 0;
        var provider = new CopilotChatTurnRunner(
            async (_, _, _, invoke, ct) =>
            {
                sessionRuns++;
                for (var i = 0; i < 4; i++)
                {
                    await invoke(new() { Id = $"sdk-{i}", Name = "inspect_container" }, ct);
                    returnedOutcomes++;
                }
                return "Must not finish";
            });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.RunTurnAsync(Request(), Tools, (_, _) =>
            {
                calls++;
                return Task.FromResult(new string('x', 11_000));
            }, CancellationToken.None));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, sessionRuns);
        Assert.Equal(3, calls);
        Assert.Equal(2, returnedOutcomes);
    }

    [Fact]
    public async Task CancellationBeforeLateSdkCallbackPreventsExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var executions = 0;
        var provider = new CopilotChatTurnRunner(
            async (_, _, _, invoke, _) =>
            {
                cancellation.Cancel();
                await invoke(new() { Id = "late", Name = "inspect_container" }, CancellationToken.None);
                return "Must not finish";
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.RunTurnAsync(Request(), Tools, (_, _) =>
            {
                executions++;
                return Task.FromResult("Unexpected");
            }, cancellation.Token));
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task CallbackAfterSessionCompletionIsRejectedBeforeExecution()
    {
        Func<AiToolCall, CancellationToken, Task<string>>? lateCallback = null;
        var executions = 0;
        var provider = new CopilotChatTurnRunner((_, _, _, invoke, _) =>
        {
            lateCallback = invoke;
            return Task.FromResult("Complete");
        });
        var result = await provider.RunTurnAsync(Request(), Tools, (_, _) =>
        {
            executions++;
            return Task.FromResult("Unexpected");
        }, CancellationToken.None);
        Assert.Equal("Complete", result.FinalText);
        Assert.NotNull(lateCallback);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lateCallback(new() { Id = "late", Name = "inspect_container" }, CancellationToken.None));
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task SessionFailureRejectsConcurrentNoncooperativeCallbackCompletion()
    {
        var entered = AiContractHarness.Signal<bool>();
        var release = AiContractHarness.Signal<string>();
        var expected = new IOException("Synthetic session transport failure");
        Task<string>? lateCompletion = null;
        CancellationToken executionToken = default;
        var executions = 0;
        var provider = new CopilotChatTurnRunner(async (_, _, _, invoke, ct) =>
        {
            lateCompletion = invoke(new() { Id = "in-flight", Name = "inspect_container" }, ct);
            await entered.Task;
            throw expected;
        });
        var turn = provider.RunTurnAsync(Request(), Tools, async (_, token) =>
        {
            executionToken = token;
            executions++;
            entered.TrySetResult(true);
            return await release.Task;
        }, CancellationToken.None);

        try
        {
            var actual = await Assert.ThrowsAsync<IOException>(() => turn.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(expected, actual);
            Assert.True(executionToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult("Late completed mutation evidence");
        }
        Assert.NotNull(lateCompletion);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lateCompletion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedPendingArgumentsFailBeforeExecutionIncludingUnprunableSdkHistory(bool olderTurnCanBePruned)
    {
        var original = new AiToolCall
        {
            Id = "oversized-call", Name = "inspect_container",
            ArgumentsJson = JsonSerializer.Serialize(new { padding = new string('x', 11_000), password = "synthetic-call-secret" }),
        };
        var request = Request();
        var history = new List<AiChatMessage>
        {
            request.History[0],
            new() { Role = "user", Content = new string('u', 11_000) },
            new() { Role = "assistant", Content = new string('a', 11_000) },
        };
        if (olderTurnCanBePruned)
            history.Add(new() { Role = "user", Content = "Inspect the container" });
        request = request with { History = history };
        var executions = 0;
        var sessions = 0;
        var provider = new CopilotChatTurnRunner(async (_, _, _, invoke, ct) =>
        {
            sessions++;
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(original, ct));
            return "Transport swallowed the budget failure";
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.RunTurnAsync(request, Tools, (_, _) =>
            {
                executions++;
                return Task.FromResult("Must not execute");
            }, CancellationToken.None));

        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, executions);
        Assert.Equal(1, sessions);
        Assert.Contains("synthetic-call-secret", original.ArgumentsJson);
    }

    [Fact]
    public async Task ToolFailureStopsSessionEvenWhenTransportSwallowsCallbackException()
    {
        var expected = new InvalidOperationException("Synthetic tool failure");
        var executions = 0;
        var provider = new CopilotChatTurnRunner(
            async (_, _, _, invoke, ct) =>
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    invoke(new() { Id = "failed", Name = "inspect_container" }, ct));
                return "Misleading success";
            });
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.RunTurnAsync(Request(), Tools, (_, _) =>
            {
                executions++;
                throw expected;
            }, CancellationToken.None));
        Assert.Same(expected, actual);
        Assert.Equal(1, executions);
    }
}
