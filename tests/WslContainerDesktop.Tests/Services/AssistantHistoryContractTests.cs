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

public sealed class AssistantHistoryContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolEvidenceSurvivesFinalSummaryAndProviderFailure(bool fail)
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        var call = AiContractHarness.Call();
        h.Tools.Execute = (_, _) => Task.FromResult("""{"status":"partial","outcomes":[{"id":"approved-id","status":"succeeded"}]}""");
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            await invoke(call, ct);
            if (fail) throw new IOException("provider disconnected");
            return "A deliberately uninformative final summary.";
        });
        if (fail) await Assert.ThrowsAsync<IOException>(() => h.Assistant.SendAsync("stop"));
        else await h.Assistant.SendAsync("stop");
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("follow-up"));
        await h.Assistant.SendAsync("Which targets succeeded?");
        var history = h.Provider.Requests[1];
        var evidence = Assert.Single(history, m => m.Role == "tool");
        Assert.Equal(call.Id, evidence.ToolCallId);
        Assert.Contains("\"status\":\"partial\"", evidence.Content);
        Assert.Equal(call.Id, Assert.Single(history.SelectMany(m => m.ToolCalls)).Id);
        Assert.Single(h.Tools.Executed);
        AssertPaired(history);
        Assert.Equal(2, h.Provider.Requests[0].Count);
    }

    [Theory]
    [InlineData(nameof(ISettingsService.AiOpenAiEndpoint), "https://cloud.invalid/v1")]
    [InlineData(nameof(ISettingsService.AiOpenAiModel), "different-model")]
    public async Task ConfigurationChangeDiscardsHistoryEvenWhenSwitchingBack(string setting, string changed)
    {
        var h = new AiContractHarness();
        var original = h.SettingsValues[setting];
        for (var i = 0; i < 3; i++) h.Provider.Turns.Enqueue((_, _) => Task.FromResult("answer"));
        await h.Assistant.SendAsync("private local conversation");
        h.SettingsValues[setting] = changed;
        await h.Assistant.SendAsync("new destination");
        h.SettingsValues[setting] = original;
        await h.Assistant.SendAsync("back again");
        Assert.All(h.Provider.Requests, history => Assert.Equal(2, history.Count));
        Assert.DoesNotContain("private local", JsonSerializer.Serialize(h.Provider.Requests.Skip(1)));
    }

    [Fact]
    public async Task LocalToCloudSwitchUsesRealServiceAndHttpAdaptersWithoutHistoryTransfer()
    {
        var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var h = new AiContractHarness(providerFactory: settings =>
            [new OllamaProvider(http, settings), new OpenAiProvider(http, settings, new AiContractHarness.Credentials())]);
        h.SettingsValues[nameof(ISettingsService.AiProvider)] = AiProviderKind.Ollama;
        handler.Enqueue("""{"message":{"role":"assistant","content":"local answer"}}""");
        await h.Assistant.SendAsync("local-only private project evidence");
        h.SettingsValues[nameof(ISettingsService.AiProvider)] = AiProviderKind.OpenAi;
        handler.Enqueue("""{"choices":[{"message":{"role":"assistant","content":"cloud answer"}}]}""");
        await h.Assistant.SendAsync("new cloud question");
        Assert.Contains("ollama.invalid", handler.Requests[0].Uri.Host);
        Assert.DoesNotContain("local-only", handler.Requests[1].Body);
        Assert.DoesNotContain("local answer", handler.Requests[1].Body);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.AzureOpenAi)]
    [InlineData(AiProviderKind.Ollama)]
    public async Task RealHttpFollowUpReceivesStructuredEvidence(AiProviderKind kind)
    {
        var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var h = new AiContractHarness(providerFactory: settings => [kind switch
        {
            AiProviderKind.OpenAi => new OpenAiProvider(http, settings, new AiContractHarness.Credentials()),
            AiProviderKind.AzureOpenAi => (IAiChatProvider)new AzureOpenAiProvider(http, settings, new AiContractHarness.Credentials()),
            _ => new OllamaProvider(http, settings),
        }]);
        h.SettingsValues[nameof(ISettingsService.AiProvider)] = kind;
        h.Tools.Category = AssistantPermissionCategory.ReadOnly;
        h.Tools.Execute = (_, _) => Task.FromResult("""{"id":"approved-id","state":"stopped"}""");
        handler.Enqueue(kind == AiProviderKind.Ollama
            ? """{"message":{"tool_calls":[{"function":{"name":"inspect_container","arguments":{"id":"approved-id"}}}]}}"""
            : """{"choices":[{"message":{"tool_calls":[{"id":"wire-id","function":{"name":"inspect_container","arguments":"{\"id\":\"approved-id\"}"}}]}}]}""");
        var final = kind == AiProviderKind.Ollama
            ? """{"message":{"content":"summary without evidence"}}"""
            : """{"choices":[{"message":{"content":"summary without evidence"}}]}""";
        handler.Enqueue(final);
        handler.Enqueue(final);
        await h.Assistant.SendAsync("inspect");
        await h.Assistant.SendAsync("what was its state?");
        using var body = JsonDocument.Parse(handler.Requests[2].Body);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Single(messages, m => m.TryGetProperty("tool_calls", out _));
        var result = Assert.Single(messages, m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("stopped", result.GetProperty("content").GetString());
        if (kind != AiProviderKind.Ollama)
            Assert.Equal("wire-id", result.GetProperty("tool_call_id").GetString());
        Assert.Single(h.Tools.Executed);
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.AzureOpenAi)]
    [InlineData(AiProviderKind.Ollama)]
    public async Task ResetRejectsLateNonCooperativeHttpToolResponse(AiProviderKind kind)
    {
        var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var h = new AiContractHarness(providerFactory: settings => [kind switch
        {
            AiProviderKind.OpenAi => new OpenAiProvider(http, settings, new AiContractHarness.Credentials()),
            AiProviderKind.AzureOpenAi => (IAiChatProvider)new AzureOpenAiProvider(http, settings, new AiContractHarness.Credentials()),
            _ => new OllamaProvider(http, settings),
        }]);
        h.SettingsValues[nameof(ISettingsService.AiProvider)] = kind;
        h.AutoApproved.Add("stop_container");
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<HttpResponseMessage>();
        handler.Responses.Enqueue(_ => { entered.SetResult(true); return resume.Task; });
        var old = h.Assistant.SendAsync("old request");
        await entered.Task.WaitAsync(Deadline);
        h.Assistant.Reset();
        var final = kind == AiProviderKind.Ollama
            ? """{"message":{"content":"fresh answer"}}"""
            : """{"choices":[{"message":{"content":"fresh answer"}}]}""";
        handler.Enqueue(final);
        await h.Assistant.SendAsync("fresh request");
        var lateTool = kind == AiProviderKind.Ollama
            ? """{"message":{"tool_calls":[{"function":{"name":"stop_container","arguments":{"id":"approved-id"}}}]}}"""
            : """{"choices":[{"message":{"tool_calls":[{"id":"late","function":{"name":"stop_container","arguments":"{\"id\":\"approved-id\"}"}}]}}]}""";
        resume.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(lateTool) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Deadline));
        handler.Enqueue(final);
        await h.Assistant.SendAsync("follow-up");
        Assert.Empty(h.Tools.Resolved);
        Assert.DoesNotContain("old request", handler.Requests[^1].Body);
        Assert.Contains("fresh answer", handler.Requests[^1].Body);
    }

    [Fact]
    public async Task CopilotBridgeRoundTripsServiceEvidenceAndIsolatesModelChanges()
    {
        var seen = new List<IReadOnlyList<AiChatMessage>>();
        var configurations = new List<AiChatConfiguration>();
        var runner = new CopilotChatTurnRunner(async (configuration, history, _, invoke, ct) =>
        {
            configurations.Add(configuration);
            seen.Add(history);
            if (seen.Count == 1)
                await invoke(AiContractHarness.Call("inspect_container"), ct);
            return "summary without target details";
        });
        var h = new AiContractHarness(providerFactory: _ => [new CopilotBridgeProvider(runner)]);
        h.SettingsValues[nameof(ISettingsService.AiProvider)] = AiProviderKind.GitHubCopilot;
        h.Tools.Category = AssistantPermissionCategory.ReadOnly;
        h.Tools.Definitions = _ => Task.FromResult<IReadOnlyList<AiToolDefinition>>(
            [new() { Name = "inspect_container", Description = "Inspect a synthetic container", JsonSchemaParameters = """{"type":"object"}""" }]);
        h.Tools.Execute = (_, _) => Task.FromResult("""{"id":"approved-id","state":"stopped"}""");
        await h.Assistant.SendAsync("inspect");
        await h.Assistant.SendAsync("what was its state?");
        Assert.Contains("stopped", Assert.Single(seen[1], m => m.Role == "tool").Content);
        AssertPaired(seen[1]);
        h.SettingsValues[nameof(ISettingsService.AiGitHubCopilotModel)] = "different-model";
        await h.Assistant.SendAsync("fresh conversation");
        Assert.Equal(2, seen[2].Count);
        Assert.Equal("different-model", configurations[2].Model);
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task ConfigurationIsCapturedBeforeAwaitingDefinitions()
    {
        var h = new AiContractHarness();
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<IReadOnlyList<AiToolDefinition>>();
        h.Tools.Definitions = _ => { entered.SetResult(true); return resume.Task; };
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("answer"));
        var send = h.Assistant.SendAsync("question");
        await entered.Task.WaitAsync(Deadline);
        h.SettingsValues[nameof(ISettingsService.AiOpenAiEndpoint)] = "https://changed.invalid";
        h.SettingsValues[nameof(ISettingsService.AiOpenAiModel)] = "changed-model";
        resume.SetResult([]);
        await send.WaitAsync(Deadline);
        Assert.Equal("https://provider.invalid/v1", h.Provider.Configurations[0].Endpoint);
        Assert.Equal("synthetic-model", h.Provider.Configurations[0].Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetOrCancellationDiscardsLateProviderTextAndToolCallbacks(bool reset)
    {
        var h = new AiContractHarness();
        using var cancel = new CancellationTokenSource();
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<bool>();
        h.Provider.Turns.Enqueue(async (invoke, _) =>
        {
            entered.SetResult(true);
            await resume.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoke(AiContractHarness.Call(), CancellationToken.None));
            return "stale final text";
        });
        var old = h.Assistant.SendAsync("old", cancel.Token);
        await entered.Task.WaitAsync(Deadline);
        if (reset) h.Assistant.Reset();
        else cancel.Cancel();
        resume.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Deadline));
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("fresh answer"));
        await h.Assistant.SendAsync("fresh");
        Assert.DoesNotContain("stale final text", JsonSerializer.Serialize(h.Provider.Requests[1]));
        Assert.Empty(h.Tools.Resolved);
        Assert.Empty(h.Tools.Executed);
        if (reset) Assert.Equal(2, h.Provider.Requests[1].Count);
    }

    [Fact]
    public async Task ResetDuringResolutionCannotPublishApprovalOrExecute()
    {
        var h = new AiContractHarness();
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<AssistantResolvedToolCall>();
        h.Tools.Resolve = (_, _) => { entered.SetResult(true); return resume.Task; };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        h.Assistant.ApprovalChanged += (_, approval) => Assert.Null(approval);
        var old = h.Assistant.SendAsync("old");
        await entered.Task.WaitAsync(Deadline);
        h.Assistant.Reset();
        resume.SetResult(new(AiContractHarness.Call(), AssistantPermissionCategory.Lifecycle, "old", "old",
            _ => throw new InvalidOperationException("stale execution must not run")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Deadline));
        Assert.Empty(h.Activity);
    }

    [Fact]
    public async Task OldFinallyCannotClearNewPendingApproval()
    {
        var h = new AiContractHarness();
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<string>();
        h.Provider.Turns.Enqueue((_, _) => { entered.SetResult(true); return resume.Task; });
        var old = h.Assistant.SendAsync("old");
        await entered.Task.WaitAsync(Deadline);
        h.Assistant.Reset();
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        var cleared = 0;
        h.Assistant.ApprovalChanged += (_, approval) =>
        {
            if (approval is null) cleared++;
            else requested.TrySetResult(approval);
        };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        var fresh = h.Assistant.SendAsync("fresh");
        var approval = await requested.Task.WaitAsync(Deadline);
        resume.SetResult("old late result");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Deadline));
        Assert.Equal(0, cleared);
        await h.Assistant.ApproveAsync(approval);
        await fresh.WaitAsync(Deadline);
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task OverlappingTurnsAreRejectedWithoutPollutingHistory()
    {
        var h = new AiContractHarness();
        var resume = AiContractHarness.Signal<string>();
        h.Provider.Turns.Enqueue((_, _) => resume.Task);
        var first = h.Assistant.SendAsync("first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("overlap"));
        resume.SetResult("answer");
        await first;
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("next");
        Assert.DoesNotContain("overlap", JsonSerializer.Serialize(h.Provider.Requests));
    }

    [Fact]
    public async Task DuplicateCallbackCannotExecuteMutationTwice()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        var call = AiContractHarness.Call();
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            await invoke(call, ct);
            await invoke(call, ct);
            return "unreachable";
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("stop"));
        Assert.Single(h.Tools.Executed);
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("next");
        AssertPaired(h.Provider.Requests[1]);
        Assert.Single(h.Provider.Requests[1], m => m.Role == "tool");
    }

    [Fact]
    public async Task CallbackAfterCompletedTurnCannotResolveOrPublishApproval()
    {
        var h = new AiContractHarness();
        Func<AiToolCall, CancellationToken, Task<string>>? lateCallback = null;
        h.Provider.Turns.Enqueue((invoke, _) =>
        {
            lateCallback = invoke;
            return Task.FromResult("done");
        });
        await h.Assistant.SendAsync("first");
        h.Assistant.ApprovalChanged += (_, approval) => Assert.Null(approval);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lateCallback!(AiContractHarness.Call(), CancellationToken.None));
        Assert.Empty(h.Tools.Resolved);
    }

    [Fact]
    public async Task ResetDuringNonCooperativeExecutionCannotRetainLateOutcome()
    {
        var h = new AiContractHarness();
        var entered = AiContractHarness.Signal<bool>();
        var resume = AiContractHarness.Signal<string>();
        h.AutoApproved.Add("stop_container");
        h.Tools.Execute = (_, _) => { entered.SetResult(true); return resume.Task; };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        var old = h.Assistant.SendAsync("old");
        await entered.Task.WaitAsync(Deadline);
        h.Assistant.Reset();
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("new answer"));
        await h.Assistant.SendAsync("new");
        resume.SetResult("late old outcome");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.WaitAsync(Deadline));
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("next");
        Assert.DoesNotContain("late old outcome", JsonSerializer.Serialize(h.Provider.Requests[^1]));
        Assert.DoesNotContain(h.Provider.Requests[^1], m => m.Role == "tool");
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task RetainedArgumentsOutcomesAndFailuresUseSanitizedCopies()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("run_container");
        var call = AiContractHarness.Call("run_container", """{"image":"nginx","password":"synthetic-history-value"}""");
        h.Tools.Execute = (original, _) =>
        {
            Assert.Same(call, original);
            Assert.Contains("synthetic-history-value", original.ArgumentsJson);
            throw new InvalidOperationException("""{"password":"synthetic-history-value","reason":"ordinary-failure"}""");
        };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(call, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("run"));
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("next");
        var retained = JsonSerializer.Serialize(h.Provider.Requests[1]);
        Assert.DoesNotContain("synthetic-history-value", retained);
        Assert.Contains("ordinary-failure", retained);
        Assert.Contains("nginx", retained);
        AssertPaired(h.Provider.Requests[1]);
    }

    [Fact]
    public async Task IndependentExecutionCancellationRetainsUnknownNotSuccess()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        h.Tools.Execute = (_, _) => throw new OperationCanceledException("engine interrupted");
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Assistant.SendAsync("stop"));
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("next");
        var evidence = Assert.Single(h.Provider.Requests[1], m => m.Role == "tool");
        Assert.Contains("unknown", evidence.Content);
        Assert.DoesNotContain("Succeeded", evidence.Content);
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task CancellationRetainsHonestPartialOutcomeWithoutReturningSuccess()
    {
        var h = new AiContractHarness();
        using var cancellation = new CancellationTokenSource();
        h.AutoApproved.Add("stop_container");
        h.Tools.Execute = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult("""{"status":"partial","outcomes":[{"id":"approved-id","status":"succeeded"},{"id":"second","status":"not-run"}]}""");
        };
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(), ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Assistant.SendAsync("stop", cancellation.Token));
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("inspect before continuing"));
        await h.Assistant.SendAsync("what happened?");
        var evidence = Assert.Single(h.Provider.Requests[1], m => m.Role == "tool");
        Assert.Contains("\"status\":\"partial\"", evidence.Content);
        Assert.Contains("not-run", evidence.Content);
        Assert.Single(h.Tools.Executed);
    }

    [Fact]
    public async Task ProviderCallbackCancellationCancelsPendingApprovalWithoutCancellingUserToken()
    {
        var h = new AiContractHarness();
        using var providerCancellation = new CancellationTokenSource();
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, approval) =>
        {
            if (approval is not null) requested.TrySetResult(approval);
        };
        h.Provider.Turns.Enqueue((invoke, _) => invoke(AiContractHarness.Call(), providerCancellation.Token));
        var send = h.Assistant.SendAsync("stop");
        var approval = await requested.Task.WaitAsync(Deadline);
        providerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(Deadline));
        await h.Assistant.ApproveAsync(approval);
        Assert.Empty(h.Tools.Executed);
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("next");
        Assert.Contains("Not run", Assert.Single(h.Provider.Requests[1], m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task SwallowedUnexpectedToolFailureCannotExecuteAnotherCallOrReportSuccess()
    {
        var h = new AiContractHarness();
        h.AutoApproved.Add("stop_container");
        h.Tools.Execute = (_, _) => throw new NotSupportedException("synthetic unexpected tool failure");
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => invoke(AiContractHarness.Call(), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(AiContractHarness.Call(), ct));
            return "misleading success";
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("stop"));
        Assert.Contains("cannot continue", failure.Message);
        Assert.Single(h.Tools.Executed);
        Assert.Single(h.Tools.Resolved);
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("inspect before retrying");
        Assert.DoesNotContain("misleading success", JsonSerializer.Serialize(h.Provider.Requests[1]));
        Assert.Contains("unknown", Assert.Single(h.Provider.Requests[1], m => m.Role == "tool").Content);
    }

    [Fact]
    public async Task LargeConversationIsBoundedAndTruncationIsVisible()
    {
        var h = new AiContractHarness();
        h.Tools.Category = AssistantPermissionCategory.ReadOnly;
        h.Tools.Execute = (_, _) => Task.FromResult("observed state: " + new string('x', 7000));
        AssistantTurnResult? last = null;
        for (var i = 0; i < 20; i++)
        {
            h.Provider.Turns.Enqueue(async (invoke, ct) =>
            {
                await invoke(AiContractHarness.Call("inspect_container"), ct);
                return "answer " + new string('y', 2000);
            });
            last = await h.Assistant.SendAsync("inspect " + i);
        }
        Assert.Contains("Conversation truncated", Assert.Single(last!.Messages).Text);
        Assert.All(h.Provider.Requests, AssertPaired);
        var config = h.Provider.Configurations[0];
        var definitions = await h.Tools.GetDefinitionsAsync(default);
        Assert.All(h.Provider.Requests, request =>
            Assert.True(AiConversationContext.Measure(request, definitions) <= AiConversationContext.InputByteLimit(config)));
        Assert.DoesNotContain(h.Provider.Requests[^1], m => m.Content == "inspect 0");
    }

    [Fact]
    public async Task NearLimitDefinitionsReserveRoomForEvictionNoticeAndServiceRemainsUsable()
    {
        var h = new AiContractHarness();
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("done"));
        await h.Assistant.SendAsync("question");
        var config = h.Provider.Configurations[0];
        static AiToolDefinition Definition(string padding) => new()
        {
            Name = "tool", Description = "Tool",
            JsonSchemaParameters = JsonSerializer.Serialize(new { type = "object", description = padding }),
        };
        var spare = AiConversationContext.InputByteLimit(config)
            - AiConversationContext.Measure(h.Provider.Requests[0], [Definition("")]);
        var definitions = new[] { Definition(new string('x', spare)) };
        Assert.Equal(AiConversationContext.InputByteLimit(config),
            AiConversationContext.Measure(h.Provider.Requests[0], definitions));
        h.Assistant.Reset();
        h.Tools.Definitions = _ => Task.FromResult<IReadOnlyList<AiToolDefinition>>(definitions);
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult(new string('y', 12000)));
        var result = await h.Assistant.SendAsync("question");
        Assert.Contains("Conversation truncated", Assert.Single(result.Messages).Text);
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("done"));
        await h.Assistant.SendAsync("question");
        Assert.True(AiConversationContext.Measure(h.Provider.Requests[^1], definitions)
            <= AiConversationContext.InputByteLimit(config));
    }

    [Fact]
    public void SchemaAccountingCompactsSyntaxOnlyAndRejectsMalformedSchemas()
    {
        var compact = new AiToolDefinition { Name = "tool", Description = "description",
            JsonSchemaParameters = """{"type":"object","properties":{"spaced key":{"type":"string","pattern":"^a b$","description":"keep  two spaces\nand a newline"}}}""" };
        using var schema = JsonDocument.Parse(compact.JsonSchemaParameters);
        AiToolDefinition WithSchema(string json) => new()
            { Name = compact.Name, Description = compact.Description, JsonSchemaParameters = json };
        var formatted = WithSchema(JsonSerializer.Serialize(schema.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        var baseline = AiConversationContext.Measure([], [compact]);
        Assert.Equal(baseline, AiConversationContext.Measure([], [formatted]));
        var significant = WithSchema(compact.JsonSchemaParameters.Replace("a b", "a  b", StringComparison.Ordinal));
        Assert.Equal(baseline + 1, AiConversationContext.Measure([], [significant]));
        foreach (var invalid in new[] { "{", "[]", "null", "\"string\"" })
            Assert.Throws<InvalidOperationException>(() =>
                AiConversationContext.Measure([], [WithSchema(invalid)]));
    }

    [Fact]
    public void BudgetIncludesSchemasUnicodeAndPairedGroupsAndNeverDropsActiveEvidence()
    {
        var config = new AiChatConfiguration(AiProviderKind.Ollama, "http://local", "unknown");
        Assert.Equal(AiConversationContext.InputByteLimit(config),
            AiConversationContext.InputByteLimit(config with { Model = "llama3.1" }));
        var messages = new List<AiChatMessage> { new() { Role = "system", Content = "trusted instructions" } };
        for (var i = 0; i < 12; i++)
        {
            var call = AiContractHarness.Call();
            messages.Add(new() { Role = "user", Content = "request " + i });
            messages.Add(new() { Role = "assistant", ToolCalls = [call] });
            messages.Add(new() { Role = "tool", ToolCallId = call.Id, ToolName = call.Name, Content = new string('\u6f22', 2000) });
            messages.Add(new() { Role = "assistant", Content = "done" });
        }
        messages.Add(new() { Role = "user", Content = "latest" });
        var prepared = AiConversationContext.Prepare(messages, [], config);
        AssertPaired(prepared);
        Assert.True(AiConversationContext.Measure(prepared, []) <= AiConversationContext.InputByteLimit(config));
        Assert.Contains(prepared, m => m.Content == AiConversationContext.TruncationNotice);
        Assert.Equal("latest", prepared[^1].Content);
        Assert.Equal("trusted instructions", prepared[0].Content);
        var hugeTool = new AiToolDefinition { Name = "tool", Description = "", JsonSchemaParameters = new string('x', 40000) };
        Assert.Throws<InvalidOperationException>(() => AiConversationContext.Prepare([messages[0], messages[^1]], [hugeTool], config));
        var active = new AiChatMessage { Role = "assistant", ToolCalls = Enumerable.Range(0, 10)
            .Select(_ => AiContractHarness.Call(arguments: new string('x', 10000))).ToArray() };
        Assert.Throws<InvalidOperationException>(() => AiConversationContext.Prepare([messages[0], messages[^1], active], [], config));
    }

    private static void AssertPaired(IReadOnlyList<AiChatMessage> messages)
    {
        var calls = messages.SelectMany(m => m.ToolCalls).ToArray();
        var outcomes = messages.Where(m => m.Role == "tool").ToArray();
        Assert.Equal(calls.Length, outcomes.Length);
        Assert.All(calls, call => Assert.Single(outcomes, m => m.ToolCallId == call.Id && m.ToolName == call.Name));
    }

    private sealed class CopilotBridgeProvider(CopilotChatTurnRunner runner) : IAiChatProvider
    {
        public AiProviderKind Kind => AiProviderKind.GitHubCopilot;

        public Task<AiChatTurnResult> RunTurnAsync(AiChatRequest request, IReadOnlyList<AiToolDefinition> tools,
            Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync, CancellationToken ct) =>
            runner.RunTurnAsync(request, tools, invokeToolAsync, ct);
    }
}
