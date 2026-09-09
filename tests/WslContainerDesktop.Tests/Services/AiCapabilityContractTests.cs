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

using System.Net;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AiCapabilityContractTests
{
    public static TheoryData<AiProviderKind> Providers => AiProviderContractTests.Providers;
    private static AiChatConfiguration Configuration(AiProviderKind kind = AiProviderKind.OpenAi) =>
        new(kind, "https://synthetic.invalid/v1", "synthetic-model");

    private static string Reply(AiProviderKind kind, string content, bool tool = false)
    {
        object message = tool
            ? new
            {
                content = "", tool_calls = new[]
                {
                    new { id = "ack", type = "function", function = new
                    {
                        name = "capability_ack",
                        arguments = kind == AiProviderKind.Ollama
                            ? (object) new { ok = true } : """{"ok":true}""",
                    } },
                },
            }
            : new { content };
        return JsonSerializer.Serialize(kind == AiProviderKind.Ollama
            ? (object)new { message } : new { choices = new[] { new { message } } });
    }

    [Fact]
    public async Task OpenAiInventoryIsReachabilityNotNameBasedFeatureSupport()
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue("""{"data":[{"id":"gpt-4o","context_length":999999,"supports_tools":true}]}""");
        var observer = new HttpAiCapabilityObserver(AiProviderKind.OpenAi, http, new AiContractHarness.Credentials());
        var state = await observer.ReadMetadataAsync(Configuration() with { Model = "gpt-4o" }, default);
        Assert.Equal(AiEndpointState.Reachable, state.Endpoint);
        Assert.Equal(AiModelState.Available, state.Model);
        Assert.Equal(AiSupport.Unknown, state.Chat.Support);
        Assert.Equal(AiSupport.Unknown, state.Tools.Support);
        Assert.Equal(AiSupport.Unknown, state.StructuredJson.Support);
        Assert.Equal(AiSupport.Unknown, state.Streaming.Support);
        Assert.Equal(AiSupport.Unknown, state.Context.Support);
        Assert.False(state.CanUseTools);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OllamaRecognizedMetadataSeparatesInstalledUnloadedAndFeatureStates(bool tools)
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue("""{"models":[{"name":"synthetic-model:latest","digest":"synthetic-digest"}]}""");
        handler.Enqueue("""{"version":"synthetic-runtime-version"}""");
        handler.Enqueue(JsonSerializer.Serialize(new
        {
            capabilities = tools ? new[] { "completion", "tools" } : new[] { "completion" },
            model_info = new Dictionary<string, object> { ["architecture.context_length"] = 4096 },
        }));
        handler.Enqueue("""{"models":[]}""");
        var config = Configuration(AiProviderKind.Ollama);
        var state = await new HttpAiCapabilityObserver(config.Kind, http, new AiContractHarness.Credentials())
            .ReadMetadataAsync(config, default);
        Assert.Equal(AiSupport.Supported, state.Chat.Support);
        Assert.Equal(tools ? AiSupport.Supported : AiSupport.Unsupported, state.Tools.Support);
        Assert.Equal(AiObservationSource.Metadata, state.Tools.Source);
        Assert.Equal(AiDownloadState.Downloaded, state.Download);
        Assert.Equal(AiLoadState.Unloaded, state.Load);
        Assert.Equal(4096, state.Context.ContextTokens);
        Assert.Null(state.Context.InputByteCeiling);
        Assert.Equal(32768, AiConversationContext.InputByteLimit(config));
        Assert.Equal(4, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("/chat"));
        Assert.DoesNotContain("synthetic-digest", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("synthetic-runtime-version", JsonSerializer.Serialize(state));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HarmlessProbesProveOnlyValidatedFeaturesAndNeverInvokeAnAppTool(AiProviderKind kind)
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Reply(kind, "OK"));
        handler.Enqueue(Reply(kind, "", tool: true));
        handler.Enqueue(Reply(kind, """{"ok":true}"""));
        var state = await new HttpAiCapabilityObserver(kind, http, new AiContractHarness.Credentials())
            .ProbeAsync(new(Configuration(kind)), default);
        Assert.True(state.CanUseTools);
        Assert.Equal(AiSupport.Supported, state.StructuredJson.Support);
        Assert.Equal(AiSupport.Unknown, state.Streaming.Support);
        Assert.Equal(AiSupport.Unknown, state.Context.Support);
        Assert.Equal(3, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            using var doc = JsonDocument.Parse(request.Body);
            Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(64, kind == AiProviderKind.Ollama
                ? doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32()
                : doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.DoesNotContain("stop_container", request.Body);
            Assert.DoesNotContain("synthetic-key-not-a-credential", request.Body);
            Assert.DoesNotContain("synthetic-key-not-a-credential", request.Uri.ToString());
        }
        using var toolRequest = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("capability_ack", toolRequest.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task JsonRejectionLeavesChatUsableAndIgnoredToolsUnknown(AiProviderKind kind)
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Reply(kind, "OK"));
        handler.Enqueue(Reply(kind, "I cannot call that tool."));
        handler.Enqueue(JsonSerializer.Serialize(new { error = new
        {
            code = "unsupported_parameter", param = kind == AiProviderKind.Ollama ? "format" : "response_format",
            message = "password=synthetic-private-value",
        } }), HttpStatusCode.BadRequest);
        var state = await new HttpAiCapabilityObserver(kind, http, new AiContractHarness.Credentials())
            .ProbeAsync(new(Configuration(kind)), default);
        Assert.True(state.CanChat);
        Assert.False(state.CanUseTools);
        Assert.Equal(AiSupport.Unknown, state.Tools.Support);
        Assert.Equal(AiSupport.Unsupported, state.StructuredJson.Support);
        Assert.Equal(AiEndpointState.Reachable, state.Endpoint);
        Assert.DoesNotContain("synthetic-private-value", JsonSerializer.Serialize(state));
        Assert.DoesNotContain("synthetic-private-value", state.StatusText);
    }

    [Theory]
    [InlineData(401, "authentication_error", AiAuthenticationState.RequiredOrRejected, AiModelState.Unknown, AiLoadState.Unknown)]
    [InlineData(404, "model_not_found", AiAuthenticationState.Unknown, AiModelState.Missing, AiLoadState.Unknown)]
    [InlineData(503, "model_loading", AiAuthenticationState.Unknown, AiModelState.Unknown, AiLoadState.Loading)]
    [InlineData(503, "server_error", AiAuthenticationState.Unknown, AiModelState.Unknown, AiLoadState.Unknown)]
    [InlineData(404, "route_not_found", AiAuthenticationState.Unknown, AiModelState.Unknown, AiLoadState.Unknown)]
    public async Task OperationalFailuresDoNotInventCapabilityFailures(int status, string code,
        AiAuthenticationState auth, AiModelState model, AiLoadState load)
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(JsonSerializer.Serialize(new { error = new { code } }), (HttpStatusCode)status);
        var state = await new HttpAiCapabilityObserver(AiProviderKind.OpenAi, http, new AiContractHarness.Credentials())
            .ProbeAsync(new(Configuration()), default);
        Assert.Equal(auth, state.Authentication);
        Assert.Equal(model, state.Model);
        Assert.Equal(load, state.Load);
        Assert.Equal(AiSupport.Unknown, state.Chat.Support);
        Assert.Equal(AiSupport.Unknown, state.Tools.Support);
        Assert.Equal(AiEndpointState.Reachable, state.Endpoint);
        Assert.False(state.CanUseTools);
        Assert.Single(handler.Requests);
        if (load == AiLoadState.Loading) Assert.Contains("warming up", state.NextStep);
    }

    [Fact]
    public async Task MissingOllamaModelNeverTriggersDownloadOrGeneration()
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue("""{"models":[]}""");
        var observer = new HttpAiCapabilityObserver(AiProviderKind.Ollama, http, new AiContractHarness.Credentials());
        var state = await observer.ReadMetadataAsync(Configuration(AiProviderKind.Ollama), default);
        state = await observer.ProbeAsync(state, default);
        Assert.Equal(AiModelState.Missing, state.Model);
        Assert.Equal(AiDownloadState.NotDownloaded, state.Download);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(AiSupport.Unknown)]
    [InlineData(AiSupport.Unsupported)]
    public async Task ServiceRejectsUnknownOrUnsupportedToolsBeforeDefinitionLookup(AiSupport support)
    {
        var h = new AiContractHarness();
        h.Capabilities.Tools = support;
        h.Tools.Definitions = _ => throw new InvalidOperationException("Must not load definitions.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("stop a container"));
        Assert.Contains("require observed", error.Message);
        Assert.Empty(h.Provider.Requests);
        Assert.Empty(h.Tools.Resolved);
        Assert.Empty(h.Tools.Executed);
    }

    [Fact]
    public async Task RevokedObservationWhileAwaitingApprovalPreventsExecution()
    {
        var h = new AiContractHarness();
        var pending = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, a) => { if (a is not null) pending.TrySetResult(a); };
        h.Provider.Turns.Enqueue(async (invoke, ct) => await invoke(AiContractHarness.Call(), ct));
        var task = h.Assistant.SendAsync("stop the approved container");
        var approval = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Capabilities.Invalidate();
        await h.Assistant.ApproveAsync(approval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Empty(h.Tools.Executed);
    }

    [Fact]
    public async Task CacheCoalescesExplicitProbesAndNeverGeneratesOnMetadataRefresh()
    {
        var observer = new FakeObserver();
        var clock = new FakeClock();
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials(), clock);
        var config = Configuration();
        Assert.False((await service.GetAsync(config)).CanChat);
        Assert.Equal(0, observer.ProbeCalls);
        var results = await Task.WhenAll(service.GetAsync(config, true), service.GetAsync(config, true));
        Assert.All(results, s => Assert.True(s.CanUseTools));
        Assert.Equal(1, observer.ProbeCalls);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await service.GetAsync(config)).CanUseTools);
        Assert.Equal(1, observer.ProbeCalls);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(service.GetCached(config).CanUseTools);
        Assert.False((await service.GetAsync(config)).CanUseTools);
        Assert.Equal(1, observer.ProbeCalls);
    }

    [Fact]
    public async Task ConfigurationCredentialRuntimeAndModelRevisionChangesInvalidateEvidence()
    {
        var observer = new FakeObserver();
        var clock = new FakeClock();
        var credentials = new MutableCredentials();
        var service = new AiCapabilityService([observer], credentials, clock);
        var config = Configuration();
        await service.GetAsync(config, true);
        credentials.Secret = "second-synthetic-key";
        Assert.False(service.GetCached(config).CanUseTools);
        Assert.False((await service.GetAsync(config)).CanUseTools);
        await service.GetAsync(config, true);
        clock.Advance(TimeSpan.FromMinutes(2));
        observer.RuntimeIdentity = "runtime-2";
        Assert.False((await service.GetAsync(config)).CanUseTools);
        await service.GetAsync(config, true);
        clock.Advance(TimeSpan.FromMinutes(2));
        observer.ModelIdentity = "revision-2";
        Assert.False((await service.GetAsync(config)).CanUseTools);
        await service.GetAsync(config, true);
        Assert.False((await service.GetAsync(config with { Model = "different-model" })).CanUseTools);
        Assert.False((await service.GetAsync(config)).CanUseTools); // no switch-back resurrection
        Assert.False((await service.GetAsync(config with { Endpoint = "https://other.invalid" })).CanUseTools);
        service.Invalidate();
        Assert.False(service.GetCached(config).CanUseTools);
    }

    [Fact]
    public async Task CancellationAndInvalidationRejectLateMetadataWithoutPublishing()
    {
        foreach (var invalidate in new[] { false, true })
        {
            var observer = new FakeObserver();
            var entered = AiContractHarness.Signal<bool>();
            var release = AiContractHarness.Signal<bool>();
            observer.Read = async (config, _) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return Ready(config);
            };
            var service = new AiCapabilityService([observer], new AiContractHarness.Credentials());
            using var cancellation = new CancellationTokenSource();
            var task = service.GetAsync(Configuration(), ct: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (invalidate) service.Invalidate(); else cancellation.Cancel();
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(service.GetCached(Configuration()).CanUseTools);
        }
    }

    [Fact]
    public async Task HttpCancellationStopsBeforeAnyFurtherProbe()
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        handler.Responses.Enqueue(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Reply(AiProviderKind.OpenAi, "OK")),
            });
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpAiCapabilityObserver(AiProviderKind.OpenAi, http, new AiContractHarness.Credentials())
                .ProbeAsync(new(Configuration()), cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ContextUnitsRemainDistinctAndObservedByteCeilingIsConfigurationBound()
    {
        var config = Configuration() with { Model = "context-test-" + Guid.NewGuid() };
        var observer = new FakeObserver { Context = new(AiSupport.Supported, 1000000, 8192, AiObservationSource.Metadata) };
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials());
        try
        {
            await service.GetAsync(config);
            Assert.Equal(8192, AiConversationContext.InputByteLimit(config));
            Assert.Equal(32768, AiConversationContext.InputByteLimit(config with { Endpoint = "https://other.invalid" }));
            Assert.Throws<InvalidOperationException>(() => AiConversationContext.Prepare(
                [new() { Role = "user", Content = new string('\u6f22', 4000) }], [], config));
            service.Invalidate();
            Assert.Equal(32768, AiConversationContext.InputByteLimit(config));
            observer.Context = new(AiSupport.Supported, 4096, Source: AiObservationSource.Metadata);
            await service.GetAsync(config);
            Assert.Equal(32768, AiConversationContext.InputByteLimit(config)); // not 4096 bytes or 4x tokens
        }
        finally { service.Invalidate(); }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DiagnosisOmitsUnprovenJsonModeButStillParsesJsonAndChatOmitsToolOptions(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        h.Capabilities.Json = AiSupport.Unsupported;
        IAiProvider provider = kind switch
        {
            AiProviderKind.OpenAi => new OpenAiProvider(http, h.Settings, new AiContractHarness.Credentials(), h.Capabilities),
            AiProviderKind.AzureOpenAi => new AzureOpenAiProvider(http, h.Settings, new AiContractHarness.Credentials(), h.Capabilities),
            _ => new OllamaProvider(http, h.Settings, h.Capabilities),
        };
        handler.Enqueue(Reply(kind, """{"summary":"diagnosed","likelyCause":"test","confidence":0.7}"""));
        Assert.Equal("diagnosed", (await provider.CompleteAsync(new("Return JSON", "synthetic evidence"), default)).Summary);
        handler.Enqueue(Reply(kind, "plain chat"));
        await ((IAiChatProvider)provider).RunTurnAsync(new(AiConversationContext.Capture(h.Settings, kind),
            [new() { Role = "user", Content = "hello" }]), [], (_, _) => throw new InvalidOperationException(), default);
        using var diagnosis = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(diagnosis.RootElement.TryGetProperty("response_format", out _));
        Assert.False(diagnosis.RootElement.TryGetProperty("format", out _));
        using var chat = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.False(chat.RootElement.TryGetProperty("tools", out _));
        Assert.False(chat.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task CopilotSyntheticBridgeProvesCallbackNotProseAndLeavesJsonStreamingUnknown()
    {
        var sessions = 0;
        var result = await CopilotCapabilityProbe.RunAsync(new(Configuration(AiProviderKind.GitHubCopilot)),
            async (_, tools, invoke, ct) =>
            {
                sessions++;
                if (tools.Count > 0)
                {
                    Assert.Equal("capability_ack", Assert.Single(tools).Name);
                    await invoke(new() { Id = "ack", Name = "capability_ack", ArgumentsJson = """{"ok":true}""" }, ct);
                }
                return new("OK", []);
            }, default);
        Assert.Equal(2, sessions);
        Assert.True(result.CanUseTools);
        Assert.Equal(AiSupport.Unknown, result.StructuredJson.Support);
        Assert.Equal(AiSupport.Unknown, result.Streaming.Support);
        var proseOnly = await CopilotCapabilityProbe.RunAsync(new(Configuration(AiProviderKind.GitHubCopilot)),
            (_, _, _, _) => Task.FromResult(new AiChatTurnResult("I support tools", [])), default);
        Assert.True(proseOnly.CanChat);
        Assert.False(proseOnly.CanUseTools);
    }

    [Fact]
    public async Task TimeoutIsActionableNotUnsupportedAndExplicitRetryHasCooldown()
    {
        var observer = new FakeObserver
        {
            Probe = (_, _) => throw new OperationCanceledException("synthetic timeout"),
        };
        var clock = new FakeClock();
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials(), clock);
        var state = await service.GetAsync(Configuration(), true);
        Assert.True(state.ProbeTimedOut);
        Assert.Equal(AiLoadState.Unknown, state.Load); // timeout alone cannot prove loading
        Assert.Equal(AiSupport.Unknown, state.Chat.Support);
        Assert.Contains("one-minute cooldown", state.NextStep);
        await service.GetAsync(Configuration(), true);
        Assert.Equal(1, observer.ProbeCalls);
        clock.Advance(TimeSpan.FromMinutes(2));
        observer.Probe = null;
        Assert.True((await service.GetAsync(Configuration(), true)).CanUseTools);
        Assert.Equal(2, observer.ProbeCalls);
    }

    [Fact]
    public async Task CancelledGenerationNeverPublishesCapabilitiesAndDoesNotContinue()
    {
        var entered = AiContractHarness.Signal<bool>();
        var release = AiContractHarness.Signal<bool>();
        var observer = new FakeObserver
        {
            Probe = async (state, _) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return Ready(state.Configuration);
            },
        };
        using var cancellation = new CancellationTokenSource();
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials());
        var task = service.GetAsync(Configuration(), true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(service.GetCached(Configuration()).CanUseTools);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExplicitToolRejectionDoesNotDisableChatOrJson(AiProviderKind kind)
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Reply(kind, "OK"));
        handler.Enqueue("""{"error":{"code":"unsupported_feature","param":"tools"}}""", HttpStatusCode.BadRequest);
        handler.Enqueue(Reply(kind, """{"ok":true}"""));
        var result = await new HttpAiCapabilityObserver(kind, http, new AiContractHarness.Credentials())
            .ProbeAsync(new(Configuration(kind)), default);
        Assert.True(result.CanChat);
        Assert.False(result.CanUseTools);
        Assert.Equal(AiSupport.Unsupported, result.Tools.Support);
        Assert.Equal(AiSupport.Supported, result.StructuredJson.Support);
    }

    [Fact]
    public async Task GenericBadRequestAndMalformedToolResponsesRemainUnknown()
    {
        foreach (var body in new[]
        {
            """{"error":{"message":"bad input"}}""",
            """{"choices":[{"message":{"tool_calls":[{"function":{"name":"wrong_tool","arguments":"{}"}}]}}]}""",
            """{"choices":[]}""",
        })
        {
            using var handler = new AiContractHarness.ScriptedHttpHandler();
            using var http = new AiHttpClient(handler);
            handler.Enqueue(Reply(AiProviderKind.OpenAi, "OK"));
            handler.Enqueue(body, body.Contains("bad input") ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            handler.Enqueue(Reply(AiProviderKind.OpenAi, """{"ok":true}"""));
            var state = await new HttpAiCapabilityObserver(AiProviderKind.OpenAi, http, new AiContractHarness.Credentials())
                .ProbeAsync(new(Configuration()), default);
            Assert.True(state.CanChat);
            Assert.Equal(AiSupport.Unknown, state.Tools.Support);
        }
    }

    [Fact]
    public async Task CapturedProbeCredentialCannotChangeBetweenRequests()
    {
        var credentials = new MutableCredentials();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Responses.Enqueue(_ =>
        {
            credentials.Secret = "changed-synthetic-key";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Reply(AiProviderKind.AzureOpenAi, "OK")),
            });
        });
        handler.Enqueue(Reply(AiProviderKind.AzureOpenAi, "", true));
        handler.Enqueue(Reply(AiProviderKind.AzureOpenAi, """{"ok":true}"""));
        await new HttpAiCapabilityObserver(AiProviderKind.AzureOpenAi, http, credentials)
            .ProbeAsync(new(Configuration(AiProviderKind.AzureOpenAi)), default);
        Assert.All(handler.Requests, r => Assert.Equal("first-synthetic-key", r.Headers["api-key"]));
    }

    [Fact]
    public async Task AzureMissingCredentialsRequiresAuthenticationWithoutTransport()
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var observer = new HttpAiCapabilityObserver(AiProviderKind.AzureOpenAi, http, new AiContractHarness.Credentials(null));
        var state = await observer.ReadMetadataAsync(Configuration(AiProviderKind.AzureOpenAi), default);
        state = await observer.ProbeAsync(state, default);
        Assert.Equal(AiAuthenticationState.RequiredOrRejected, state.Authentication);
        Assert.Equal(AiEndpointState.Unknown, state.Endpoint);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DiagnosisDoesNotSendEvidenceToConfigurationChangedDuringObservation()
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var observer = new FakeObserver
        {
            Read = (config, _) =>
            {
                h.SettingsValues[nameof(ISettingsService.AiOpenAiEndpoint)] = "https://unapproved.invalid";
                return Task.FromResult(Ready(config));
            },
        };
        var observations = new AiCapabilityService([observer], new AiContractHarness.Credentials());
        var service = new AiDiagnosticsService(
            NetworkTestProxy.Create<IWslcService>((_, _) => throw new InvalidOperationException("No engine access")),
            NetworkTestProxy.Create<IActivityLog>((_, _) => throw new InvalidOperationException("No audit access")),
            h.Settings,
            NetworkTestProxy.Create<IWslcCapabilitiesService>((_, _) => throw new InvalidOperationException("No CLI probes")),
            [new OpenAiProvider(http, h.Settings, new AiContractHarness.Credentials(), observations)],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AiDiagnosticsService>.Instance,
            observations);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiagnoseAsync(new("system", "private synthetic evidence")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ObservationInvalidatedDuringDefinitionLookupNeverSendsToolEnabledRequest()
    {
        var h = new AiContractHarness();
        h.Tools.Definitions = _ =>
        {
            h.Capabilities.Invalidate();
            return Task.FromResult<IReadOnlyList<AiToolDefinition>>([]);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("inspect"));
        Assert.Empty(h.Provider.Requests);
        Assert.Empty(h.Tools.Resolved);
    }

    [Fact]
    public async Task CopilotToolFailureDoesNotErasePlainChatObservation()
    {
        var state = await CopilotCapabilityProbe.RunAsync(new(Configuration(AiProviderKind.GitHubCopilot)),
            (_, tools, _, _) => tools.Count > 0
                ? throw new InvalidOperationException("synthetic tool rejection")
                : Task.FromResult(new AiChatTurnResult("OK", [])), default);
        Assert.True(state.CanChat);
        Assert.Equal(AiSupport.Unknown, state.Tools.Support);
    }

    [Fact]
    public async Task ObserverCannotPublishCapabilitiesForAnotherDestination()
    {
        var observer = new FakeObserver
        {
            Read = (config, _) => Task.FromResult(Ready(config with { Endpoint = "https://another.invalid" })),
        };
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(Configuration(), true));
        Assert.False(service.GetCached(Configuration()).CanUseTools);
        Assert.Equal(0, observer.ProbeCalls);
    }

    [Fact]
    public async Task MetadataRefreshCannotExtendOriginalProbeProofExpiry()
    {
        var clock = new FakeClock();
        var observer = new FakeObserver
        {
            Probe = (state, _) => Task.FromResult(state with
            {
                Chat = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
                Tools = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
                StructuredJson = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
                Streaming = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
                Context = new(AiSupport.Supported, 4096, 8192, AiObservationSource.HarmlessProbe),
            }),
        };
        var config = Configuration() with { Model = "proof-expiry-" + Guid.NewGuid() };
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials(), clock);
        try
        {
            var original = await service.GetAsync(config, true); // t0
            Assert.True(original.CanUseTools);
            Assert.Equal(8192, AiConversationContext.InputByteLimit(config));
            clock.Advance(TimeSpan.FromMinutes(9));
            var refreshed = await service.GetAsync(config); // t9, metadata-only
            Assert.True(refreshed.CanUseTools);
            Assert.True(refreshed.ObservedAt > original.ObservedAt);
            Assert.Equal(1, observer.ProbeCalls);
            clock.Advance(TimeSpan.FromMinutes(2));
            var expired = service.GetCached(config); // t11, without another metadata read
            Assert.Equal(AiEndpointState.Reachable, expired.Endpoint); // refreshed metadata survives
            Assert.Equal(AiSupport.Unknown, expired.Chat.Support);
            Assert.Equal(AiSupport.Unknown, expired.Tools.Support);
            Assert.Equal(AiSupport.Unknown, expired.StructuredJson.Support);
            Assert.Equal(AiSupport.Unknown, expired.Streaming.Support);
            Assert.Equal(AiSupport.Unknown, expired.Context.Support);
            Assert.False(expired.CanUseTools);
            Assert.Equal(32768, AiConversationContext.InputByteLimit(config));
            Assert.Equal(1, observer.ProbeCalls);
        }
        finally { service.Invalidate(); }
    }

    [Fact]
    public async Task MetadataCacheFastPathAlsoWithdrawsExpiredProbeToolsWithoutDiscardingMetadataChat()
    {
        var clock = new FakeClock();
        var metadataReads = 0;
        var observer = new FakeObserver
        {
            Read = (config, _) =>
            {
                metadataReads++;
                return Task.FromResult(new AiCapabilitySnapshot(config)
                {
                    Chat = new(AiSupport.Supported, AiObservationSource.Metadata),
                    Endpoint = AiEndpointState.Reachable,
                    RuntimeIdentity = "same-runtime",
                    ModelIdentity = "same-model",
                });
            },
            Probe = (state, _) => Task.FromResult(state with
            {
                Tools = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
            }),
        };
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials(), clock);
        Assert.True((await service.GetAsync(Configuration(), true)).CanUseTools);
        clock.Advance(TimeSpan.FromMinutes(9.5));
        Assert.True((await service.GetAsync(Configuration())).CanUseTools);
        clock.Advance(TimeSpan.FromMinutes(0.5)); // proof expires exactly at ten minutes
        var expired = await service.GetAsync(Configuration()); // metadata is still in its one-minute cache
        Assert.Equal(2, metadataReads);
        Assert.True(expired.CanChat);
        Assert.Equal(AiObservationSource.Metadata, expired.Chat.Source);
        Assert.Equal(AiSupport.Unknown, expired.Tools.Support);
        Assert.False(expired.CanUseTools);
        Assert.Equal(1, observer.ProbeCalls);
    }

    public static TheoryData<AiProviderKind, string> InvalidEndpoints
    {
        get
        {
            var data = new TheoryData<AiProviderKind, string>();
            foreach (var kind in new[] { AiProviderKind.OpenAi, AiProviderKind.AzureOpenAi, AiProviderKind.Ollama })
                foreach (var endpoint in new[]
                {
                    "", "not a URI", "/relative/endpoint", "https://", "https://synthetic.invalid:bad",
                    "https://synthetic.invalid/%zz", "https://synthetic.invalid/path with space",
                    "file:///synthetic/model", "ftp://synthetic.invalid",
                    "https://synthetic-user:synthetic-password@synthetic.invalid/v1",
                })
                    data.Add(kind, endpoint);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEndpoints))]
    public async Task InvalidEndpointsProduceSafeActionableConfigurationStatusWithoutTransport(AiProviderKind kind, string endpoint)
    {
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var observer = new HttpAiCapabilityObserver(kind, http, new AiContractHarness.Credentials());
        var config = Configuration(kind) with { Endpoint = endpoint };
        var metadata = await observer.ReadMetadataAsync(config, default);
        var observed = await observer.ProbeAsync(metadata, default);
        var directProbe = await observer.ProbeAsync(new(config), default);
        var service = new AiCapabilityService([observer], new AiContractHarness.Credentials());
        var cached = await service.GetAsync(config, true);
        foreach (var state in new[] { metadata, observed, directProbe, cached, service.GetCached(config) })
        {
            Assert.Equal(AiEndpointState.InvalidConfiguration, state.Endpoint);
            Assert.False(state.CanChat);
            Assert.False(state.CanUseTools);
            Assert.Equal(AiSupport.Unknown, state.Chat.Support);
            Assert.Equal(AiSupport.Unknown, state.Tools.Support);
            Assert.Contains("Fix the endpoint in Settings", state.NextStep);
            Assert.Contains("absolute HTTP(S) URL", state.NextStep);
            Assert.Contains("without embedded credentials", state.NextStep);
            Assert.DoesNotContain("synthetic-user", state.StatusText);
            Assert.DoesNotContain("synthetic-password", state.StatusText);
            Assert.DoesNotContain("synthetic.invalid", state.StatusText);
        }
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"ok":false}""")]
    [InlineData("""{"ok":true,"extra":1}""")]
    [InlineData("""[]""")]
    public async Task CopilotSwallowedInvalidArgumentsCannotBeRepairedByAnotherCallback(string arguments)
    {
        var rejected = 0;
        var result = await CopilotCapabilityProbe.RunAsync(new(Configuration(AiProviderKind.GitHubCopilot)),
            async (_, tools, invoke, ct) =>
            {
                if (tools.Count > 0)
                {
                    foreach (var json in new[] { arguments, """{"ok":true}""" })
                    {
                        try
                        {
                            await invoke(new() { Id = "ack", Name = "capability_ack", ArgumentsJson = json }, ct);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                        {
                            // Simulate an SDK that swallows errors and still reports completion.
                            rejected++;
                        }
                    }
                }
                return new("OK", []);
            }, default);
        Assert.Equal(2, rejected);
        Assert.True(result.CanChat);
        Assert.Equal(AiSupport.Unknown, result.Tools.Support);
        Assert.False(result.CanUseTools);
    }

    private static AiCapabilitySnapshot Ready(AiChatConfiguration config) => new(config)
    {
        Chat = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
        Tools = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
        Endpoint = AiEndpointState.Reachable,
    };

    private sealed class FakeObserver : IAiCapabilityObserver
    {
        public AiProviderKind Kind => AiProviderKind.OpenAi;
        public int ProbeCalls { get; private set; }
        public string RuntimeIdentity { get; set; } = "runtime-1";
        public string ModelIdentity { get; set; } = "revision-1";
        public AiContextObservation Context { get; set; } = new();
        public Func<AiChatConfiguration, CancellationToken, Task<AiCapabilitySnapshot>>? Read { get; set; }
        public Func<AiCapabilitySnapshot, CancellationToken, Task<AiCapabilitySnapshot>>? Probe { get; set; }
        public Task<AiCapabilitySnapshot> ReadMetadataAsync(AiChatConfiguration configuration, CancellationToken ct) =>
            Read?.Invoke(configuration, ct) ?? Task.FromResult(new AiCapabilitySnapshot(configuration)
            {
                Endpoint = AiEndpointState.Reachable, RuntimeIdentity = RuntimeIdentity, ModelIdentity = ModelIdentity,
                Context = Context,
            });
        public Task<AiCapabilitySnapshot> ProbeAsync(AiCapabilitySnapshot metadata, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ProbeCalls++;
            return Probe?.Invoke(metadata, ct)
                ?? Task.FromResult(metadata with { Chat = Ready(metadata.Configuration).Chat, Tools = Ready(metadata.Configuration).Tools });
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class MutableCredentials : IAiCredentialStore
    {
        public string Secret { get; set; } = "first-synthetic-key";
        public bool TryReadSecret(AiProviderKind provider, out string? secret) { secret = Secret; return true; }
        public void WriteSecret(AiProviderKind provider, string secret) => throw new InvalidOperationException();
        public void DeleteSecret(AiProviderKind provider) => throw new InvalidOperationException();
    }
}
