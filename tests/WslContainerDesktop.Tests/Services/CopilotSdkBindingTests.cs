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
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

#pragma warning disable GHCP001
public sealed class CopilotSdkBindingTests
{
    // SDK 1.0.7's pinned Session.ExecuteToolAndRespondAsync and CopilotTool binding use
    // AIFunctionArguments.Context[typeof(ToolInvocation)]. Exercise that real binder, not
    // a fabricated AiToolCall callback or an invented SDK key; no session/client is started.
    internal static AIFunctionArguments Bind(AiToolCall call) => new()
    {
        Context = new Dictionary<object, object?>
        {
            [typeof(ToolInvocation)] = new ToolInvocation
            {
                SessionId = "synthetic-session", ToolCallId = call.Id, ToolName = call.Name,
                Arguments = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson),
            },
        },
    };

    [Fact]
    public async Task ActualSdkFunctionsPreserveFullCatalogAndOriginalInvocation()
    {
        var h = new AiContractHarness();
        var f = new AssistantCatalogAdapterTests.CatalogFixture();
        f.Configure(h);
        var definitions = await f.Tools(h.Settings).GetDefinitionsAsync(default);
        AssistantCatalogAdapterTests.AssertFullCatalog(definitions);
        var calls = new List<AiToolCall>();
        var failures = new List<Exception>();
        var declarations = GitHubCopilotProvider.BuildCopilotTools(definitions, (call, _) =>
        {
            calls.Add(call);
            return Task.FromResult("evidence");
        }, failures.Add);
        Assert.Equal(definitions.Count, declarations.Count);
        foreach (var definition in definitions)
        {
            var function = Assert.IsAssignableFrom<AIFunction>(declarations.Single(t => t.Name == definition.Name));
            Assert.Equal(AiTextSanitizer.SanitizeDefinition(definition).Description, function.Description);
            using var schema = JsonDocument.Parse(definition.JsonSchemaParameters);
            Assert.True(JsonElement.DeepEquals(schema.RootElement, function.JsonSchema), definition.Name);
            Assert.False(function.AdditionalProperties.ContainsKey("skip_permission"));
        }
        var original = new AiToolCall { Id = "opaque-sdk-call/1", Name = "start_compose_project",
            ArgumentsJson = """{ "projectName" : "fixture", "password": "synthetic-private", "duplicate":1,"duplicate":2 }""" };
        var arguments = Bind(original);
        arguments["projectName"] = "provider-facing-decoy";
        var result = await Assert.IsAssignableFrom<AIFunction>(
            declarations.Single(t => t.Name == original.Name)).InvokeAsync(arguments);
        Assert.Equal("evidence", Assert.IsType<JsonElement>(result).GetString());
        var actual = Assert.Single(calls);
        Assert.Equal(original.Id, actual.Id);
        Assert.Equal(original.Name, actual.Name);
        Assert.Equal(original.ArgumentsJson, actual.ArgumentsJson);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("mismatched-name")]
    [InlineData("missing-arguments")]
    [InlineData("cancelled")]
    public async Task ActualSdkBindingFailuresReachFailBridgeWithoutExecuting(string fault)
    {
        var definition = new AiToolDefinition { Name = "start_compose_project", Description = "fixture",
            JsonSchemaParameters = """{"type":"object","properties":{"projectName":{"type":"string"}},"required":["projectName"],"additionalProperties":false}""" };
        var executions = 0;
        var failures = new List<Exception>();
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(GitHubCopilotProvider.BuildCopilotTools([definition],
            (_, _) => { executions++; return Task.FromResult("must not run"); }, failures.Add)));
        var call = new AiToolCall { Id = "sdk-id", Name = definition.Name, ArgumentsJson = """{"projectName":"fixture"}""" };
        var args = fault == "missing" ? new AIFunctionArguments() : Bind(call);
        if (fault == "wrong-type")
            args.Context![typeof(ToolInvocation)] = "not an SDK invocation";
        if (fault is "mismatched-name" or "missing-arguments")
            args.Context![typeof(ToolInvocation)] = new ToolInvocation
            {
                SessionId = "synthetic-session", ToolCallId = call.Id,
                ToolName = fault == "mismatched-name" ? "host-shell" : definition.Name,
                Arguments = fault == "missing-arguments" ? null : JsonSerializer.Deserialize<JsonElement>("{}"),
            };
        using var cancellation = new CancellationTokenSource();
        if (fault == "cancelled") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => function.InvokeAsync(args, cancellation.Token).AsTask());
        Assert.Equal(0, executions);
        // AIFunction itself may reject pre-cancellation before entering the app wrapper.
        if (fault != "cancelled") Assert.Single(failures);
    }

    [Fact]
    public async Task AppSessionPoliciesAndPermissionMappingCannotGrantHostOrProjectApproval()
    {
        var h = new AiContractHarness();
        var f = new AssistantCatalogAdapterTests.CatalogFixture();
        f.Configure(h);
        var definitions = await f.Tools(h.Settings).GetDefinitionsAsync(default);
        var calls = 0;
        var config = GitHubCopilotProvider.BuildChatSessionConfig("captured-model",
            [new() { Role = "system", Content = "trusted-system" }, new() { Role = "user", Content = "not-system" }],
            definitions, (_, _) => { calls++; return Task.FromResult("unexpected"); }, _ => { }, "synthetic-directory");
        Assert.Equal("captured-model", config.Model);
        Assert.Equal("trusted-system", config.SystemMessage!.Content);
        Assert.Equal(SystemMessageMode.Replace, config.SystemMessage.Mode);
        Assert.True(config.Streaming);
        Assert.False(config.InfiniteSessions!.Enabled);
        Assert.False(config.EnableSkills);
        Assert.False(config.EnableConfigDiscovery);
        Assert.True(config.SkipCustomInstructions);
        Assert.False(config.EnableHostGitOperations);
        Assert.False(config.EnableSessionStore);
        Assert.Equal("synthetic-directory", config.WorkingDirectory);
        Assert.Equal(definitions.Select(d => d.Name), config.AvailableTools);
        foreach (var name in AssistantCatalogAdapterTests.NewTools)
        {
            var decision = await config.OnPermissionRequest!(new PermissionRequestCustomTool { ToolName = name, ToolDescription = "fixture" }, new());
            Assert.IsType<PermissionDecisionApproveOnce>(decision);
        }
        foreach (var name in new[] { "host_shell", "STOP_COMPOSE_PROJECT", "", "stop_compose_project " })
            Assert.IsType<PermissionDecisionReject>(await config.OnPermissionRequest!(
                new PermissionRequestCustomTool { ToolName = name, ToolDescription = "fixture" }, new()));
        Assert.IsType<PermissionDecisionReject>(GitHubCopilotProvider.HandleToolPermissionRequest(
            new PermissionRequestShell { FullCommandText = "whoami", Intention = "fixture",
                CanOfferSessionApproval = false, Commands = [], HasWriteFileRedirection = false, PossiblePaths = [], PossibleUrls = [] },
            definitions.Select(d => d.Name).ToHashSet(StringComparer.Ordinal)));
        Assert.Equal(0, calls);
        Assert.Empty(f.Compose.Engine.Mutations);
    }

    [Fact]
    public void AppPromptSerializationRedactsEvidenceButPreservesCallResultPairing()
    {
        var call = new AiToolCall { Id = "opaque-prompt-id", Name = "start_compose_project",
            ArgumentsJson = """{"projectName":"fixture","password":"synthetic-private"}""" };
        var prompt = GitHubCopilotProvider.BuildCopilotChatPrompt(
        [
            new() { Role = "user", Content = "Review fixture" },
            new() { Role = "assistant", Content = "Reviewing", ToolCalls = [call] },
            new() { Role = "tool", ToolName = call.Name, ToolCallId = call.Id,
                Content = """{"status":"partial","password":"synthetic-private","outcomes":[{"instance":"web","status":"failed"}]}""" },
        ]);
        Assert.DoesNotContain("synthetic-private", prompt);
        Assert.Equal(2, prompt.Split("call_id=opaque-prompt-id").Length - 1);
        Assert.Contains("assistant requested tools: start_compose_project", prompt);
        Assert.Contains("tool result for start_compose_project", prompt);
        Assert.Contains("partial", prompt);
        Assert.Contains("failed", prompt);
    }
}
#pragma warning restore GHCP001
