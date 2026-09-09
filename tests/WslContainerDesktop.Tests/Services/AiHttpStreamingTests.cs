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

using System.Net;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AiHttpStreamingTests
{
    public static TheoryData<AiProviderKind> Providers => new()
    {
        AiProviderKind.OpenAi, AiProviderKind.AzureOpenAi, AiProviderKind.Ollama,
    };

    private static readonly AiToolDefinition[] Tools =
    [
        new() { Name = "inspect_container", Description = "Inspect", JsonSchemaParameters = """{"type":"object"}""" },
    ];

    private static IAiChatProvider Create(AiProviderKind kind, AiHttpClient http, ISettingsService settings,
        IAiCapabilityService? capabilities = null) => kind switch
    {
        AiProviderKind.OpenAi => new OpenAiProvider(http, settings, new AiContractHarness.Credentials("synthetic-key"), capabilities),
        AiProviderKind.AzureOpenAi => new AzureOpenAiProvider(http, settings, new AiContractHarness.Credentials("synthetic-key"), capabilities),
        AiProviderKind.Ollama => new OllamaProvider(http, settings, capabilities),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static AiChatRequest Request(AiContractHarness harness, AiProviderKind kind, Action<AiChatProgress> progress) =>
        new(AiConversationContext.Capture(harness.Settings, kind),
            [new() { Role = "user", Content = "Inspect\npassword: synthetic-history-secret" }]) { Progress = progress };

    private static string Sse(object delta, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";

    private static string Text(AiProviderKind kind, string text, bool done = false) => kind == AiProviderKind.Ollama
        ? JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done }) + "\n"
        : Sse(new { content = text }, done ? "stop" : null) + (done ? "data: [DONE]\n\n" : "");

    private static string Call(AiProviderKind kind, int index, string id, string arguments = "{}", string name = "inspect_container") =>
        kind == AiProviderKind.Ollama
            ? JsonSerializer.Serialize(new
            {
                message = new { role = "assistant", tool_calls = new[] { new { index, id, function = new { name, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } } } },
                done = false,
            }) + "\n"
            : Sse(new { tool_calls = new[] { new { index, id, type = "function", function = new { name, arguments } } } });

    private static string ToolEnd(AiProviderKind kind) => kind == AiProviderKind.Ollama
        ? """{"done":true,"done_reason":"stop"}""" + "\n"
        : Sse(new { }, "tool_calls") + "data: [DONE]\n\n";

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FragmentedUtf8AndSecretsAreBufferedUntilTerminalAndSanitized(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var progress = new List<AiChatProgress>();
        var body = Text(kind, "Résumé 🔎\npass") + Text(kind, "word: synthetic-split-") + Text(kind, "credential", true);
        var stream = new FragmentStream(body, 1);
        stream.BeforeRead = () => Assert.DoesNotContain(progress, p => p.Kind == AiChatProgressKind.TextDelta);
        using var handler = new StreamingHandler(stream);
        using var http = new AiHttpClient(handler);
        var answer = await Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, progress.Add), Tools,
            (_, _) => throw new InvalidOperationException("No tools expected"), CancellationToken.None);

        Assert.Equal("Résumé 🔎\npassword: <redacted>", answer.FinalText);
        Assert.Equal(new[] { AiChatProgressKind.Generating, AiChatProgressKind.TextDelta }, progress.Select(p => p.Kind));
        Assert.Equal(answer.FinalText, progress[^1].Text);
        Assert.DoesNotContain("synthetic-split", JsonSerializer.Serialize(progress));
        Assert.DoesNotContain("synthetic-history-secret", handler.Bodies[0]);
        Assert.True(JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("stream").GetBoolean());
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SafeProseArrivesWhileTransportPausedBeforeTerminalWithoutDuplicateText(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var progress = new List<AiChatProgress>();
        var first = Text(kind, "Looking now. ");
        var stream = new FragmentStream(first + Text(kind, "Completed.", true), 1)
        {
            PauseAtPosition = Encoding.UTF8.GetByteCount(first),
        };
        using var handler = new StreamingHandler(stream);
        using var http = new AiHttpClient(handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, progress.Add), Tools,
            (_, _) => throw new InvalidOperationException("No tools expected"), deadline.Token);
        try
        {
            await stream.Paused.Task.WaitAsync(deadline.Token);
            Assert.False(run.IsCompleted);
            Assert.False(stream.Disposed);
            var early = Assert.Single(progress, p => p.Kind == AiChatProgressKind.TextDelta);
            Assert.Equal("Looking now. ", early.Text);
            Assert.DoesNotContain(progress, p => p.Text.Contains("Completed.", StringComparison.Ordinal));
        }
        finally
        {
            stream.Resume.TrySetResult();
        }
        var answer = await run;
        Assert.Equal("Looking now. Completed.", answer.FinalText);
        Assert.Equal(answer.FinalText, string.Concat(progress.Where(p => p.Kind == AiChatProgressKind.TextDelta).Select(p => p.Text)));
        Assert.Equal(2, progress.Count(p => p.Kind == AiChatProgressKind.TextDelta));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AllCallsValidateBeforeCallbacksAndIndexOrderDeterminesExecution(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var progress = new List<AiChatProgress>();
        var first = Text(kind, "Inspecting\npassword: synthetic-narration") +
            Call(kind, 1, "call-2", """{"id":"two","password":"synthetic-argument"}""") +
            Call(kind, 0, "call-1", """{"id":"one"}""") + ToolEnd(kind);
        var firstStream = new FragmentStream(first, 3);
        var secondStream = new FragmentStream(Text(kind, "Finished", true), 2);
        using var handler = new StreamingHandler(firstStream, secondStream);
        using var http = new AiHttpClient(handler);
        var calls = new List<AiToolCall>();
        var provider = Create(kind, http, h.Settings);
        var request = Request(h, kind, progress.Add);
        var result = await provider.RunTurnAsync(request, Tools, (call, token) =>
        {
            Assert.True(firstStream.Disposed);
            Assert.Equal(CancellationToken.None, token);
            Assert.Equal(AiChatProgressKind.TextDelta, progress[^1].Kind);
            calls.Add(call);
            h.SettingsValues[nameof(ISettingsService.AiOpenAiModel)] = "changed-model";
            h.SettingsValues[nameof(ISettingsService.AiOllamaModel)] = "changed-model";
            h.SettingsValues[nameof(ISettingsService.AiAzureOpenAiDeployment)] = "changed-model";
            return Task.FromResult("evidence\npassword: synthetic-result");
        }, CancellationToken.None);

        Assert.Equal(new[] { "call-1", "call-2" }, calls.Select(c => c.Id));
        Assert.Contains("synthetic-argument", calls[1].ArgumentsJson);
        Assert.Equal(4, result.Messages.Count);
        Assert.Equal("Finished", result.FinalText);
        Assert.Equal(2, handler.Count);
        Assert.DoesNotContain("synthetic-argument", handler.Bodies[1]);
        Assert.DoesNotContain("synthetic-result", handler.Bodies[1]);
        Assert.DoesNotContain("synthetic-narration", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("changed-model", handler.Bodies[1]);
        Assert.DoesNotContain("changed-model", handler.Uris[1].ToString());
        Assert.Single(request.History);
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.AzureOpenAi)]
    public async Task OpenAiAssemblesInterleavedArgumentAndNameFragments(AiProviderKind kind)
    {
        var body = ": heartbeat\r\n\r\n" +
            Sse(new { tool_calls = new[] { new { index = 0, id = "call-1", type = "function", function = new { name = "inspect_", arguments = "{\"id\":" } } } }) +
            Call(kind, 1, "call-2") +
            Sse(new { tool_calls = new[] { new { index = 0, function = new { name = "container", arguments = "\"approved\"}" } } } }) +
            Sse(new { }, "tool_calls") +
            "data: {\"choices\":[],\"usage\":{\"completion_tokens\":10}}\n\n" +
            "data: [DONE]\n\n";
        var h = new AiContractHarness();
        using var handler = new StreamingHandler(new FragmentStream(body, 1), new FragmentStream(Text(kind, "Done", true)));
        using var http = new AiHttpClient(handler);
        var calls = new List<AiToolCall>();
        await Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools, (call, _) =>
        {
            calls.Add(call);
            return Task.FromResult("ok");
        }, CancellationToken.None);
        Assert.Equal(2, calls.Count);
        Assert.Equal("""{"id":"approved"}""", calls[0].ArgumentsJson);
        Assert.All(calls, c => Assert.Equal("inspect_container", c.Name));
    }

    public static IEnumerable<object[]> InvalidCalls()
    {
        foreach (var kind in new[] { AiProviderKind.OpenAi, AiProviderKind.AzureOpenAi, AiProviderKind.Ollama })
        {
            yield return [kind, Call(kind, 0, "duplicate") + Call(kind, 1, "duplicate")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "bad", "[]")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "bad", "{}", "unknown_tool")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "bad", "{}", "inspect container")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 2, "gap")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 32, "overflow")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, -1, "negative")];
            yield return [kind, Call(kind, 0, "valid") + Call(kind, 0, "repeated-index")];
            if (kind != AiProviderKind.Ollama)
            {
                yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "bad", "{\"password\":\"synthetic-secret\"")];
                yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "bad", """{"id":"one","id":"two"}""")];
                yield return [kind, Call(kind, 0, "valid") + Call(kind, 1, "bad", "null")];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidCalls))]
    public async Task InvalidCallBatchNeverExecutesEvenEarlierValidCallsOrPublishesText(AiProviderKind kind, string calls)
    {
        var h = new AiContractHarness();
        var progress = new List<AiChatProgress>();
        using var handler = new StreamingHandler(new FragmentStream(Text(kind, "Pending narration") + calls + ToolEnd(kind), 7));
        using var http = new AiHttpClient(handler);
        var executed = 0;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, progress.Add), Tools, (_, _) =>
            {
                executed++;
                return Task.FromResult("unsafe");
            }, CancellationToken.None));
        Assert.Equal(0, executed);
        Assert.Equal(1, handler.Count);
        Assert.DoesNotContain(progress, p => p.Kind == AiChatProgressKind.TextDelta);
        Assert.DoesNotContain("synthetic-secret", ex.ToString());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MissingTerminalAndDisconnectDoNotExecuteOrReplay(AiProviderKind kind)
    {
        foreach (var disconnect in new[] { false, true })
        {
            var h = new AiContractHarness();
            var progress = new List<AiChatProgress>();
            var stream = new FragmentStream(Call(kind, 0, "pending"), 1) { DisconnectAtEnd = disconnect };
            using var handler = new StreamingHandler(stream);
            using var http = new AiHttpClient(handler);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, progress.Add), Tools,
                    (_, _) => throw new InvalidOperationException("Must not execute"), CancellationToken.None));
            Assert.DoesNotContain("synthetic-transport-secret", ex.ToString());
            Assert.DoesNotContain(progress, p => p.Kind == AiChatProgressKind.TextDelta);
            Assert.Equal(1, handler.Count);
            Assert.True(stream.Disposed);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancellationInterruptsBodyReadWithoutExecutionAndDisposesResponse(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var stream = new FragmentStream(Call(kind, 0, "pending")) { WaitAtEnd = true };
        using var handler = new StreamingHandler(stream);
        using var http = new AiHttpClient(handler);
        using var cts = new CancellationTokenSource();
        var progress = new List<AiChatProgress>();
        var run = Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, progress.Add), Tools,
            (_, _) => throw new InvalidOperationException("Must not execute"), cts.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.DoesNotContain(progress, p => p.Kind == AiChatProgressKind.TextDelta);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancellationDuringToolCallbackCannotStartAnotherGeneration(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new StreamingHandler(new FragmentStream(Call(kind, 0, "call-1") + ToolEnd(kind)));
        using var http = new AiHttpClient(handler);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools, (_, token) =>
            {
                Assert.Equal(cts.Token, token);
                calls++;
                cts.Cancel();
                return Task.FromResult("cancelled");
            }, cts.Token));
        Assert.Equal(1, calls);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicateIdInLaterGenerationCannotReplayCompletedTool(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var body = Call(kind, 0, "call-1") + ToolEnd(kind);
        using var handler = new StreamingHandler(new FragmentStream(body), new FragmentStream(body));
        using var http = new AiHttpClient(handler);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools, (_, _) =>
            {
                calls++;
                return Task.FromResult("ok");
            }, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TextLineAndTotalByteLimitsFailClosed(AiProviderKind kind)
    {
        var oversizedText = Text(kind, new string('x', AiHttpStreaming.MaxSegment + 1), true);
        var oversizedLine = new string(' ', AiHttpStreaming.MaxLine + 1) + "\n";
        var oversizedStream = kind == AiProviderKind.Ollama
            ? string.Concat(Enumerable.Repeat(new string(' ', 1023) + "\n", AiHttpStreaming.MaxBytes / 1024 + 1))
            : string.Concat(Enumerable.Repeat(":" + new string(' ', 1022) + "\n", AiHttpStreaming.MaxBytes / 1024 + 1));
        foreach (var body in new[] { oversizedText, oversizedLine, oversizedStream })
        {
            var h = new AiContractHarness();
            using var handler = new StreamingHandler(new FragmentStream(body));
            using var http = new AiHttpClient(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools,
                    (_, _) => throw new InvalidOperationException("Must not execute"), CancellationToken.None));
            Assert.Equal(1, handler.Count);
        }
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]")]
    [InlineData("data: {\"error\":{\"message\":\"password: synthetic-error\"}}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("event: error\ndata: password: synthetic-error\n\n")]
    public async Task InvalidSseTerminalAndErrorFramesArePrivate(string body)
    {
        var h = new AiContractHarness();
        using var handler = new StreamingHandler(new FragmentStream(body, 1));
        using var http = new AiHttpClient(handler);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(AiProviderKind.OpenAi, http, h.Settings).RunTurnAsync(Request(h, AiProviderKind.OpenAi, _ => { }), Tools,
                (_, _) => throw new InvalidOperationException("Must not execute"), CancellationToken.None));
        Assert.DoesNotContain("synthetic-error", ex.ToString());
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HttpFailuresNeverReadUnboundedErrorBodyOrRetry(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var stream = new FragmentStream("password: synthetic-error") { BeforeRead = () => throw new InvalidOperationException("Must not read") };
        using var handler = new StreamingHandler(stream) { Status = HttpStatusCode.TooManyRequests };
        using var http = new AiHttpClient(handler);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools,
                (_, _) => throw new InvalidOperationException("Must not execute"), CancellationToken.None));
        Assert.Equal(AiFailureKind.RateLimited, ex.Kind);
        Assert.DoesNotContain("synthetic-error", ex.ToString());
        Assert.Equal(1, handler.Count);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TerminalCompletesWithoutWaitingForHttpBodyToClose(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var stream = new FragmentStream(Text(kind, "Complete", true), 1) { WaitAtEnd = true };
        using var handler = new StreamingHandler(stream);
        using var http = new AiHttpClient(handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools,
            (_, _) => throw new InvalidOperationException("No tools expected"), deadline.Token);
        Assert.Equal("Complete", result.FinalText);
        Assert.False(stream.Waiting.Task.IsCompleted);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AlreadyCancelledRequestDoesNotContactProvider(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new StreamingHandler();
        using var http = new AiHttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools,
                (_, _) => throw new InvalidOperationException("Must not execute"), cts.Token));
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("{\"error\":\"password: synthetic-error\"}\n")]
    [InlineData("{\"done\":true,\"done_reason\":\"length\"}\n")]
    [InlineData("{\"done\":false,\"message\":{\"content\":\"pending\"}}\n{\"done\":")]
    [InlineData("{\"done\":false,\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"inspect_container\",\"arguments\":\"{}\"}}]}}\n{\"done\":true}\n")]
    [InlineData("{\"done\":false,\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"inspect_container\",\"arguments\":{\"id\":\"one\",\"id\":\"two\"}}}]}}\n{\"done\":true}\n")]
    [InlineData("{\"done\":false,\"message\":{\"tool_calls\":[{\"index\":0,\"function\":{\"index\":1,\"name\":\"inspect_container\",\"arguments\":{}}}]}}\n{\"done\":true}\n")]
    public async Task MalformedOllamaFramesFailClosed(string body)
    {
        var h = new AiContractHarness();
        using var handler = new StreamingHandler(new FragmentStream(body, 1));
        using var http = new AiHttpClient(handler);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(AiProviderKind.Ollama, http, h.Settings).RunTurnAsync(Request(h, AiProviderKind.Ollama, _ => { }), Tools,
                (_, _) => throw new InvalidOperationException("Must not execute"), CancellationToken.None));
        Assert.DoesNotContain("synthetic-error", ex.ToString());
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task SseMultilineDataAndCrLfCommentsAreSupported()
    {
        const string body = ": heartbeat\r\n\r\nevent: message\r\n" +
            "data: {\"choices\": [\r\ndata: {\"index\":0,\"delta\":{\"content\":\"safe\"},\"finish_reason\":\"stop\"}]}\r\n\r\n" +
            "data: [DONE]\r\n\r\n";
        var h = new AiContractHarness();
        using var handler = new StreamingHandler(new FragmentStream(body, 1));
        using var http = new AiHttpClient(handler);
        var result = await Create(AiProviderKind.OpenAi, http, h.Settings).RunTurnAsync(Request(h, AiProviderKind.OpenAi, _ => { }), Tools,
            (_, _) => throw new InvalidOperationException("No tools expected"), CancellationToken.None);
        Assert.Equal("safe", result.FinalText);
    }

    [Theory]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.AzureOpenAi)]
    public async Task NullableDeltaMetadataDoesNotDiscardAssembledToolIdentity(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var body = Sse(JsonSerializer.Deserialize<JsonElement>(
            """{"role":"assistant","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"inspect_container","arguments":"{"}}]}""")) +
            Sse(JsonSerializer.Deserialize<JsonElement>(
            """{"role":null,"content":null,"function_call":null,"tool_calls":[{"index":0,"id":null,"type":null,"function":{"name":null,"arguments":"}"}}]}""")) +
            Sse(JsonSerializer.Deserialize<JsonElement>(
            """{"role":null,"content":null,"tool_calls":null,"function_call":null}"""), "tool_calls") +
            "data: [DONE]\n\n";
        using var handler = new StreamingHandler(new FragmentStream(body, 1), new FragmentStream(Text(kind, "Complete", true)));
        using var http = new AiHttpClient(handler);
        var invoked = new List<AiToolCall>();
        await Create(kind, http, h.Settings).RunTurnAsync(Request(h, kind, _ => { }), Tools, (call, _) =>
        {
            invoked.Add(call);
            return Task.FromResult("ok");
        }, CancellationToken.None);
        var actual = Assert.Single(invoked);
        Assert.Equal("call-1", actual.Id);
        Assert.Equal("inspect_container", actual.Name);
        Assert.Equal("{}", actual.ArgumentsJson);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NullableUnusedToolListIsValidInExplicitNonstreamResponse(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var body = JsonResponse(kind).Replace("\"tool_calls\":[]", "\"tool_calls\":null", StringComparison.Ordinal);
        using var handler = new StreamingHandler(new FragmentStream(body, 1));
        using var http = new AiHttpClient(handler);
        var result = await Create(kind, http, h.Settings, new StreamingCapabilities(AiSupport.Unsupported))
            .RunTurnAsync(Request(h, kind, _ => { }), Tools,
                (_, _) => throw new InvalidOperationException("No tool requested"), CancellationToken.None);
        Assert.Equal("Complete", result.FinalText);
    }

    private static string JsonResponse(AiProviderKind kind, bool withTool = false)
    {
        object[] calls = withTool
            ? [kind == AiProviderKind.Ollama
                ? new { id = "call-1", function = new { name = "inspect_container", arguments = new { id = "one" } } }
                : new { id = "call-1", type = "function", function = new { name = "inspect_container", arguments = """{"id":"one"}""" } }]
            : [];
        var message = new { role = "assistant", content = "Complete", tool_calls = calls };
        return kind == AiProviderKind.Ollama
            ? JsonSerializer.Serialize(new { message, done = true, done_reason = "stop" })
            : JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message, finish_reason = withTool ? "tool_calls" : "stop" } } });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OnlyExplicitUnsupportedSelectsNonStreamingBeforeRequest(AiProviderKind kind)
    {
        foreach (var support in new[] { AiSupport.Unknown, AiSupport.Supported, AiSupport.Unsupported })
        {
            var h = new AiContractHarness();
            var capabilities = new StreamingCapabilities(support);
            var body = support == AiSupport.Unsupported ? JsonResponse(kind) : Text(kind, "Complete", true);
            using var handler = new StreamingHandler(new FragmentStream(body, 1));
            using var http = new AiHttpClient(handler);
            var progress = new List<AiChatProgress>();
            var request = Request(h, kind, progress.Add);
            var result = await Create(kind, http, h.Settings, capabilities).RunTurnAsync(request, Tools,
                (_, _) => throw new InvalidOperationException("No tools expected"), CancellationToken.None);
            using var wire = JsonDocument.Parse(handler.Bodies[0]);
            Assert.Equal(support != AiSupport.Unsupported, wire.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("Complete", result.FinalText);
            Assert.Equal("Complete", string.Concat(progress.Where(p => p.Kind == AiChatProgressKind.TextDelta).Select(p => p.Text)));
            Assert.Equal(1, handler.Count);
            Assert.Equal(request.Configuration, Assert.Single(capabilities.Configurations));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NonStreamingSelectionRemainsFixedAcrossToolGenerations(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var capabilities = new StreamingCapabilities(AiSupport.Unsupported);
        using var handler = new StreamingHandler(new FragmentStream(JsonResponse(kind, true)), new FragmentStream(JsonResponse(kind)));
        using var http = new AiHttpClient(handler);
        var calls = 0;
        await Create(kind, http, h.Settings, capabilities).RunTurnAsync(Request(h, kind, _ => { }), Tools, (_, _) =>
        {
            capabilities.Support = AiSupport.Supported;
            calls++;
            return Task.FromResult("ok");
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(2, handler.Count);
        Assert.Single(capabilities.Configurations);
        foreach (var body in handler.Bodies)
        {
            using var wire = JsonDocument.Parse(body);
            Assert.False(wire.RootElement.GetProperty("stream").GetBoolean());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MalformedExplicitNonStreamingResponseNeverFallsBackOrExecutes(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        var capabilities = new StreamingCapabilities(AiSupport.Unsupported);
        var body = JsonResponse(kind, true).Replace("\"inspect_container\"", "\"unrecognized_tool\"", StringComparison.Ordinal);
        using var handler = new StreamingHandler(new FragmentStream(body));
        using var http = new AiHttpClient(handler);
        var progress = new List<AiChatProgress>();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(kind, http, h.Settings, capabilities).RunTurnAsync(Request(h, kind, progress.Add), Tools,
                (_, _) => throw new InvalidOperationException("Must not execute"), CancellationToken.None));
        Assert.Equal(1, handler.Count);
        Assert.DoesNotContain(progress, p => p.Kind == AiChatProgressKind.TextDelta);
    }

    private sealed class StreamingCapabilities(AiSupport support) : IAiCapabilityService
    {
        internal AiSupport Support { get; set; } = support;
        internal List<AiChatConfiguration> Configurations { get; } = [];
        public AiCapabilitySnapshot GetCached(AiChatConfiguration configuration)
        {
            Configurations.Add(configuration);
            return new(configuration) { Streaming = new(Support, AiObservationSource.Metadata) };
        }
        public Task<AiCapabilitySnapshot> GetAsync(AiChatConfiguration configuration, bool probe = false, CancellationToken ct = default) =>
            throw new InvalidOperationException("Generation must not probe or alter capability observations.");
        public void Invalidate() => throw new InvalidOperationException("Generation must not alter capability observations.");
    }

    private sealed class StreamingHandler(params FragmentStream[] streams) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        internal List<string> Bodies { get; } = [];
        internal List<Uri> Uris { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Uris.Add(request.RequestUri!);
            var stream = streams[Count++];
            return new HttpResponseMessage(Status) { Content = new StreamContent(stream) };
        }
    }

    private sealed class FragmentStream(string text, int fragmentSize = 4096) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);
        private int _position;
        private bool _paused;
        internal bool Disposed { get; private set; }
        internal Action? BeforeRead { get; set; }
        internal bool DisconnectAtEnd { get; init; }
        internal bool WaitAtEnd { get; init; }
        internal int? PauseAtPosition { get; init; }
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Use async bounded reads");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRead?.Invoke();
            if (!_paused && PauseAtPosition is int pauseAt && _position >= pauseAt)
            {
                _paused = true;
                Paused.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            if (_position == _bytes.Length)
            {
                if (DisconnectAtEnd) throw new IOException("password: synthetic-transport-secret");
                if (WaitAtEnd)
                {
                    Waiting.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return 0;
            }
            var count = Math.Min(Math.Min(fragmentSize, buffer.Length), _bytes.Length - _position);
            _bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
