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
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;
using Xunit.Abstractions;
using static WslContainerDesktop.Tests.Services.ComposeNetworkSupervisorTests;

namespace WslContainerDesktop.Tests.Services;

public sealed class AssistantCatalogAdapterTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    internal static readonly string[] NewTools =
    [
        "engine_capabilities", "get_health_observations", "get_volume_usage",
        "start_compose_project", "stop_compose_project", "restart_compose_project", "down_compose_project",
    ];

    [Fact]
    public async Task FullCatalogLeavesRoomForPromptAndToolEvidence()
    {
        var h = new AiContractHarness();
        var f = new CatalogFixture();
        f.Configure(h);
        var definitions = await f.Tools(h.Settings).GetDefinitionsAsync(default);
        var size = AiConversationContext.Measure([], definitions);
        output.WriteLine($"Full catalog accounted bytes including fixed/per-tool reserves: {size}");
        Assert.True(size <= 24_576, $"Full catalog accounts for {size} bytes; reserve at least 8KB for trusted prompt, request and evidence.");
    }

    public static IEnumerable<object[]> FullCatalogCases =>
        from kind in new[] { AiProviderKind.OpenAi, AiProviderKind.AzureOpenAi, AiProviderKind.Ollama, AiProviderKind.GitHubCopilot }
        from mutate in new[] { false, true }
        from cluster in new[] { ClusterState.Running, ClusterState.Stopped }
        select new object[] { kind, mutate, cluster };

    [Theory]
    [MemberData(nameof(FullCatalogCases))]
    public async Task FullProductionCatalogRoutesThroughAdaptersAndRetainsPairedEvidence(
        AiProviderKind kind, bool mutate, ClusterState cluster)
    {
        var fixture = new CatalogFixture { Cluster = cluster };
        if (mutate)
        {
            Assert.True((await fixture.Compose.Supervisor.ApplyReviewedAsync(
                await fixture.Compose.Supervisor.PrepareReviewAsync(fixture.Compose.Project), true)).AllSucceeded);
            fixture.Compose.Engine.Mutations.Clear();
            fixture.Compose.Engine.Reads.Clear();
            fixture.Compose.SavedSnapshots.Clear();
        }
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var call = new AiToolCall
        {
            Id = "opaque-catalog-call", Name = mutate ? "stop_compose_project" : "get_health_observations",
            ArgumentsJson = mutate ? """{"projectName":"demo"}""" : "{}",
        };
        handler.Enqueue(Response(kind, null, call));
        handler.Enqueue(Response(kind, "Completed fixture turn"));
        handler.Enqueue(Response(kind, "Follow-up fixture turn"));
        var sdkHistories = new List<IReadOnlyList<AiChatMessage>>();
        var sdkDefinitions = new List<IReadOnlyList<AiToolDefinition>>();
        var sdkPrompts = new List<string>();
        var sdkTurns = 0;
        var capturedRequests = new List<AiChatRequest>();
        var capturedResults = new List<AiChatTurnResult>();
        var harness = new AiContractHarness(fixture.Tools, settings =>
        [
            new CapturedProvider(kind switch
            {
                AiProviderKind.OpenAi => new OpenAiProvider(http, settings, new AiContractHarness.Credentials()),
                AiProviderKind.AzureOpenAi => new AzureOpenAiProvider(http, settings, new AiContractHarness.Credentials()),
                AiProviderKind.Ollama => new OllamaProvider(http, settings),
                AiProviderKind.GitHubCopilot => new CopilotBridgeProvider(new CopilotChatTurnRunner(async (configuration, history, definitions, invoke, ct) =>
                {
                    sdkHistories.Add(history);
                    sdkDefinitions.Add(definitions);
                    sdkPrompts.Add(GitHubCopilotProvider.BuildCopilotChatPrompt(history.Where(m => m.Role != "system")));
                    Assert.True(AiConversationContext.Measure(history, definitions) <= AiConversationContext.InputByteLimit(configuration));
                    var config = GitHubCopilotProvider.BuildChatSessionConfig(configuration.Model, history, definitions,
                        invoke, error => throw new InvalidOperationException("SDK binding failed", error), "synthetic-workdir");
                    var mappedBytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        config.Model, config.SystemMessage, config.AvailableTools,
                        prompt = sdkPrompts[^1],
                        tools = config.Tools!.Select(t => new { t.Name, t.Description, parameters = t.JsonSchema }),
                    }).Length;
                    var accounted = AiConversationContext.Measure(history, definitions);
                    output.WriteLine($"SDK app-owned prompt/tool mapping: {mappedBytes} bytes; accounted: {accounted}");
                    Assert.True(mappedBytes <= accounted);
                    Assert.Equal(definitions.Select(d => d.Name), config.AvailableTools);
                    if (++sdkTurns == 1)
                    {
                        var function = Assert.IsAssignableFrom<AIFunction>(config.Tools!.Single(t => t.Name == call.Name));
                        await function.InvokeAsync(CopilotSdkBindingTests.Bind(call), ct);
                    }
                    return sdkTurns == 1 ? "Completed fixture turn" : "Follow-up fixture turn";
                })),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            }, capturedRequests, capturedResults),
        ], engineCapabilities: fixture.Capabilities);
        fixture.Configure(harness);
        harness.SettingsValues[nameof(ISettingsService.AiProvider)] = kind;
        var definitions = await fixture.Tools(harness.Settings).GetDefinitionsAsync(default);
        AssertFullCatalog(definitions);
        var approvalSignal = AiContractHarness.Signal<AssistantApprovalRequest>();
        var approvalCount = 0;
        harness.Assistant.ApprovalChanged += (_, approval) =>
        {
            if (approval is not null) { approvalCount++; approvalSignal.TrySetResult(approval); }
        };
        // A saved permission toggle must not bypass Compose consequence review, on any adapter.
        harness.AutoApproved.Add(call.Name);
        var turn = harness.Assistant.SendAsync(mutate ? "Stop the saved demo project" : "Read cached health");
        if (mutate)
        {
            await Task.WhenAny(turn, approvalSignal.Task).WaitAsync(Deadline);
            if (turn.IsCompleted) await turn;
            var approval = await approvalSignal.Task.WaitAsync(Deadline);
            Assert.Contains("demo", approval.Details);
            Assert.Contains("Stop", approval.Details);
            Assert.Empty(fixture.Compose.Engine.Mutations);
            Assert.Empty(fixture.Compose.SavedSnapshots);
            Assert.Equal(AssistantPermissionCategory.ComposeTemplate, approval.Category);
            await harness.Assistant.ApproveAsync(approval);
        }
        await turn.WaitAsync(Deadline);
        Assert.Equal(mutate ? 1 : 0, approvalCount);
        if (mutate)
            Assert.Equal(["stop:demo_web"], fixture.Compose.Engine.Mutations);
        else
        {
            Assert.Empty(fixture.Compose.Engine.Mutations);
            Assert.Empty(fixture.Compose.Engine.Reads);
        }
        await harness.Assistant.SendAsync("Summarize the recorded outcome").WaitAsync(Deadline);
        if (kind == AiProviderKind.GitHubCopilot)
        {
            Assert.Empty(handler.Requests);
            Assert.Equal(2, sdkHistories.Count);
            AssertSchemas(definitions, sdkDefinitions[0]);
            AssertPairedEvidence(sdkHistories[1], call.Name, mutate);
            Assert.Contains("call_id=opaque-catalog-call", sdkPrompts[1]);
            Assert.Contains("tool result for " + call.Name, sdkPrompts[1]);
            Assert.Contains("NetworkConnect: Supported", sdkHistories[0][0].Content);
        }
        else
        {
            Assert.Equal(3, handler.Requests.Count);
            for (var index = 0; index < handler.Requests.Count; index++)
            {
                var request = handler.Requests[index];
                var actualBytes = System.Text.Encoding.UTF8.GetByteCount(request.Body);
                IReadOnlyList<AiChatMessage> accountedHistory = index == 0 ? capturedRequests[0].History :
                    index == 1 ? [.. capturedRequests[0].History, .. capturedResults[0].Messages.SkipLast(1)] :
                    capturedRequests[1].History;
                var measured = AiConversationContext.Measure(accountedHistory, definitions);
                output.WriteLine($"{kind} request {index}: actual UTF8 {actualBytes}; accounted {measured}");
                Assert.True(actualBytes <= measured, "Accounting must conservatively bound the actual provider wire request.");
                Assert.True(measured <= 32_768, "Accounted payload must fit the unchanged application ceiling.");
                Assert.True(actualBytes <= 32_768,
                    "Actual provider payload must fit the unchanged application ceiling.");
                using var json = JsonDocument.Parse(request.Body);
                var wireTools = json.RootElement.GetProperty("tools").EnumerateArray()
                    .Select(t => t.GetProperty("function")).ToArray();
                Assert.Equal(definitions.Count, wireTools.Length);
                foreach (var definition in definitions)
                {
                    var function = Assert.Single(wireTools, t => t.GetProperty("name").GetString() == definition.Name);
                    using var schema = JsonDocument.Parse(definition.JsonSchemaParameters);
                    Assert.True(JsonElement.DeepEquals(schema.RootElement, function.GetProperty("parameters")), definition.Name);
                    Assert.Equal(AiTextSanitizer.SanitizeDefinition(definition).Description, function.GetProperty("description").GetString());
                }
                Assert.EndsWith(kind == AiProviderKind.Ollama ? "/api/chat" : "/chat/completions", request.Uri.AbsolutePath);
            }
            using var followUp = JsonDocument.Parse(handler.Requests[^1].Body);
            var messages = followUp.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Contains("NetworkConnect: Supported", messages[0].GetProperty("content").GetString());
            var outcome = Assert.Single(messages, m => m.GetProperty("role").GetString() == "tool");
            Assert.Contains(mutate ? "\"stopped\"" : "StatusMonitor", outcome.GetProperty("content").GetString());
            var requested = Assert.Single(messages, m => m.TryGetProperty("tool_calls", out _)).GetProperty("tool_calls")[0];
            Assert.Equal(call.Name, requested.GetProperty("function").GetProperty("name").GetString());
            if (kind != AiProviderKind.Ollama)
                Assert.Equal(requested.GetProperty("id").GetString(), outcome.GetProperty("tool_call_id").GetString());
            else
                Assert.Equal(call.Name, outcome.GetProperty("tool_name").GetString());
        }
    }

    internal static void AssertSchemas(IReadOnlyList<AiToolDefinition> expected, IReadOnlyList<AiToolDefinition> actual)
    {
        Assert.Equal(expected.Select(t => t.Name), actual.Select(t => t.Name));
        foreach (var definition in expected)
        {
            using var left = JsonDocument.Parse(definition.JsonSchemaParameters);
            using var right = JsonDocument.Parse(actual.Single(t => t.Name == definition.Name).JsonSchemaParameters);
            Assert.True(JsonElement.DeepEquals(left.RootElement, right.RootElement), definition.Name);
        }
    }

    internal static void AssertFullCatalog(IReadOnlyList<AiToolDefinition> definitions)
    {
        Assert.Equal(40, definitions.Count);
        foreach (var name in NewTools.Concat(["list_registry_tags", "list_registry_repositories", "cluster_start", "apply_yaml"]))
            Assert.Contains(definitions, d => d.Name == name);
        foreach (var name in NewTools)
        {
            using var schema = JsonDocument.Parse(definitions.Single(d => d.Name == name).JsonSchemaParameters);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            if (name.EndsWith("_project", StringComparison.Ordinal))
            {
                Assert.Equal("projectName", Assert.Single(schema.RootElement.GetProperty("required").EnumerateArray()).GetString());
                Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("projectName").GetProperty("type").GetString());
            }
            else Assert.Empty(schema.RootElement.GetProperty("properties").EnumerateObject());
        }
        using var logs = JsonDocument.Parse(definitions.Single(d => d.Name == "get_container_logs").JsonSchemaParameters);
        Assert.Equal(1, logs.RootElement.GetProperty("properties").GetProperty("tail").GetProperty("minimum").GetInt32());
        Assert.Equal(1000, logs.RootElement.GetProperty("properties").GetProperty("tail").GetProperty("maximum").GetInt32());
    }

    private static void AssertPairedEvidence(IReadOnlyList<AiChatMessage> history, string name, bool mutate)
    {
        var request = Assert.Single(history.SelectMany(m => m.ToolCalls));
        var outcome = Assert.Single(history, m => m.Role == "tool");
        Assert.Equal(name, request.Name);
        Assert.Equal(request.Id, outcome.ToolCallId);
        Assert.Equal(name, outcome.ToolName);
        Assert.Contains(mutate ? "\"stopped\"" : "StatusMonitor", outcome.Content);
    }

    private static string Response(AiProviderKind kind, string? text, params AiToolCall[] calls)
    {
        object message = kind == AiProviderKind.Ollama
            ? new { content = text, tool_calls = calls.Select(c => new { function = new
                { name = c.Name, arguments = JsonSerializer.Deserialize<JsonElement>(c.ArgumentsJson) } }) }
            : new { content = text, tool_calls = calls.Select(c => new { id = c.Id, type = "function", function = new
                { name = c.Name, arguments = c.ArgumentsJson } }) };
        return kind == AiProviderKind.Ollama ? JsonSerializer.Serialize(new { message }) :
            JsonSerializer.Serialize(new { choices = new[] { new { message } } });
    }

    private sealed class CopilotBridgeProvider(CopilotChatTurnRunner runner) : IAiChatProvider
    {
        public AiProviderKind Kind => AiProviderKind.GitHubCopilot;
        public Task<AiChatTurnResult> RunTurnAsync(AiChatRequest request, IReadOnlyList<AiToolDefinition> tools,
            Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync, CancellationToken ct) =>
            runner.RunTurnAsync(request, tools, invokeToolAsync, ct);
    }

    private sealed class CapturedProvider(IAiChatProvider inner, List<AiChatRequest> requests,
        List<AiChatTurnResult> results) : IAiChatProvider
    {
        public AiProviderKind Kind => inner.Kind;
        public async Task<AiChatTurnResult> RunTurnAsync(AiChatRequest request, IReadOnlyList<AiToolDefinition> tools,
            Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync, CancellationToken ct)
        {
            requests.Add(request);
            var result = await inner.RunTurnAsync(request, tools, invokeToolAsync, ct);
            results.Add(result);
            return result;
        }
    }

    internal sealed class CatalogFixture : IHealthObservationSource, IAppHealthObservationSource
    {
        internal ClusterState Cluster { get; init; } = ClusterState.Running;
        internal Fixture Compose { get; } = new(presenterAvailable: false);
        internal IWslcCapabilitiesService Capabilities => NetworkTestProxy.Create<IWslcCapabilitiesService>((_, _) =>
            Task.FromResult(Compose.Snapshot));
        public HealthObservationSnapshot GetSnapshot() => new("fixture-engine", DateTimeOffset.UtcNow, true,
            [new("fixture-id", "fixture-name", ContainerState.Running, NativeHealthState.Healthy, DateTimeOffset.UtcNow)]);
        public IReadOnlyList<ContainerHealthSnapshot> GetObservations() => [];
        internal void Configure(AiContractHarness harness)
        {
            harness.SettingsValues[nameof(ISettingsService.Registries)] =
                new List<RegistryEntry> { new() { Name = "Fixture ACR", Host = "fixture.azurecr.io", IsAzure = true } };
            harness.SettingsValues[nameof(ISettingsService.WslcPath)] = "fixture-engine";
        }
        internal AssistantToolset Tools(ISettingsService settings) => new(Compose.Engine.Service,
            NetworkTestProxy.Create<IKubernetesService>((method, _) => method.Name == nameof(IKubernetesService.GetStatusAsync)
                ? Task.FromResult(new ClusterStatus { State = Cluster })
                : throw new InvalidOperationException("Unexpected cluster action")),
            new TemplateCatalog(NetworkTestProxy.Create<IUserTemplateStore>((method, _) => method.Name switch
            {
                "get_Templates" => new List<StackTemplate>(),
                "add_Changed" => null,
                _ => throw new InvalidOperationException("Unexpected template call"),
            })),
            NetworkTestProxy.Create<IComposeProjectStore>((method, _) => method.Name == nameof(IComposeProjectStore.GetAll)
                ? new List<ComposeProject> { Compose.SavedProject ?? Compose.Project }
                : throw new InvalidOperationException("Only the supervisor may persist")),
            Compose.Supervisor, settings,
            NetworkTestProxy.Create<IRegistryCatalogService>((method, _) => method.Name == nameof(IRegistryCatalogService.CanBrowse)
                ? true : throw new InvalidOperationException("Unexpected registry query")),
            Capabilities, this, this);
    }
}
