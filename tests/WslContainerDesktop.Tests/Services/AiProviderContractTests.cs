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
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AiProviderContractTests
{
    public static TheoryData<AiProviderKind> Providers => new()
    {
        AiProviderKind.OpenAi, AiProviderKind.AzureOpenAi, AiProviderKind.Ollama,
    };

    public static TheoryData<AiProviderKind, int, AiFailureKind> HttpFailures
    {
        get
        {
            var data = new TheoryData<AiProviderKind, int, AiFailureKind>();
            foreach (var kind in new[] { AiProviderKind.OpenAi, AiProviderKind.AzureOpenAi, AiProviderKind.Ollama })
            {
                data.Add(kind, 401, AiFailureKind.Authentication);
                data.Add(kind, 403, AiFailureKind.Authentication);
                data.Add(kind, 404, AiFailureKind.NotFound);
                data.Add(kind, 429, AiFailureKind.RateLimited);
                data.Add(kind, 503, AiFailureKind.ServerError);
            }
            return data;
        }
    }

    private static readonly AiChatMessage[] History =
    [
        new() { Role = "system", Content = "Synthetic system prompt" },
        new() { Role = "user", Content = "Inspect the synthetic container" },
    ];

    private static readonly AiToolDefinition[] Tools =
    [
        new()
        {
            Name = "inspect_container",
            Description = "Read synthetic inventory",
            JsonSchemaParameters = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
        },
    ];

    private static IAiChatProvider Create(AiProviderKind kind, AiHttpClient http, ISettingsService settings, string? key = "synthetic-key-not-a-credential") =>
        kind switch
        {
            AiProviderKind.OpenAi => new OpenAiProvider(http, settings, new AiContractHarness.Credentials(key)),
            AiProviderKind.AzureOpenAi => new AzureOpenAiProvider(http, settings, new AiContractHarness.Credentials(key)),
            AiProviderKind.Ollama => new OllamaProvider(http, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string Response(AiProviderKind kind, string? text = null, params AiToolCall[] calls)
    {
        object message = kind == AiProviderKind.Ollama
            ? new
            {
                content = text,
                tool_calls = calls.Select(c => new { function = new { name = c.Name, arguments = JsonSerializer.Deserialize<JsonElement>(c.ArgumentsJson) } }),
            }
            : new
            {
                content = text,
                tool_calls = calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.ArgumentsJson } }),
            };
        return kind == AiProviderKind.Ollama
            ? JsonSerializer.Serialize(new { message })
            : JsonSerializer.Serialize(new { choices = new[] { new { message } } });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ToolCallsAndResultsRoundTripUsingProviderWireFormat(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var first = AiContractHarness.Call("inspect_container");
        var second = new AiToolCall { Id = "call-2", Name = "inspect_container", ArgumentsJson = """{"id":"second-id"}""" };
        handler.Enqueue(Response(kind, null, first, second));
        handler.Enqueue(Response(kind, "Final answer"));
        var calls = new List<AiToolCall>();
        var answer = await Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (call, _) =>
        {
            calls.Add(call);
            return Task.FromResult($"Evidence for {call.Id}");
        }, CancellationToken.None);

        Assert.Equal("Final answer", answer);
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, History.Length);
        Assert.Equal(first.Name, calls[0].Name);
        Assert.Equal("approved-id", JsonSerializer.Deserialize<JsonElement>(calls[0].ArgumentsJson).GetProperty("id").GetString());
        Assert.Equal("second-id", JsonSerializer.Deserialize<JsonElement>(calls[1].ArgumentsJson).GetProperty("id").GetString());
        Assert.NotEqual(calls[0].Id, calls[1].Id);
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        var root = request.RootElement;
        var messages = root.GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal(2, messages[2].GetProperty("tool_calls").GetArrayLength());
        for (var i = 0; i < calls.Count; i++)
        {
            var result = messages[3 + i];
            Assert.Equal("tool", result.GetProperty("role").GetString());
            Assert.Equal($"Evidence for {calls[i].Id}", result.GetProperty("content").GetString());
            var function = messages[2].GetProperty("tool_calls")[i].GetProperty("function");
            Assert.Equal(calls[i].Name, function.GetProperty("name").GetString());
            if (kind == AiProviderKind.Ollama)
            {
                Assert.Equal(calls[i].Name, result.GetProperty("tool_name").GetString());
                Assert.Equal(JsonValueKind.Object, function.GetProperty("arguments").ValueKind);
            }
            else
            {
                Assert.Equal(calls[i].Id, result.GetProperty("tool_call_id").GetString());
                Assert.Equal(calls[i].Name, result.GetProperty("name").GetString());
                Assert.Equal(calls[i].ArgumentsJson, function.GetProperty("arguments").GetString());
            }
        }
        var schema = root.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("id", schema.GetProperty("required")[0].GetString());
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.DoesNotContain("synthetic-key-not-a-credential", r.Body);
            Assert.DoesNotContain("synthetic-key-not-a-credential", r.Uri.ToString());
        });
        if (kind == AiProviderKind.OpenAi)
        {
            Assert.Equal("https://provider.invalid/v1/chat/completions", handler.Requests[0].Uri.ToString());
            Assert.Equal("Bearer synthetic-key-not-a-credential", handler.Requests[0].Headers["Authorization"]);
        }
        else if (kind == AiProviderKind.AzureOpenAi)
        {
            Assert.Contains("/openai/deployments/synthetic-deployment/chat/completions?api-version=", handler.Requests[0].Uri.ToString());
            Assert.Equal("synthetic-key-not-a-credential", handler.Requests[0].Headers["api-key"]);
        }
        else
        {
            Assert.Equal("http://ollama.invalid:11434/api/chat", handler.Requests[0].Uri.ToString());
            Assert.False(handler.Requests[0].Headers.ContainsKey("Authorization"));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransportRedactsHistoryToolArgumentsAndEveryResultWithoutChangingExecution(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var arguments = SensitiveArguments();
        var priorCall = new AiToolCall { Id = "prior-call-id", Name = "inspect_container", ArgumentsJson = arguments };
        AiChatMessage[] history =
        [
            History[0],
            new() { Role = "user", Content = "Inspect approved-id\npassword: synthetic-user-value" },
            new() { Role = "assistant", Content = "Previous context\npassword: synthetic-history-value", ToolCalls = [priorCall] },
            new() { Role = "tool", ToolCallId = priorCall.Id, ToolName = priorCall.Name, Content = SensitiveResult("succeeded") },
        ];
        AiToolDefinition[] tools =
        [
            new()
            {
                Name = "inspect_container",
                Description = "Inspect approved-id\npassword: synthetic-description-value",
                JsonSchemaParameters = """{"type":"object","properties":{"id":{"type":"string"},"password":{"type":"string","description":"Password supplied by user"},"env":{"type":"array","items":{"type":"object"}}},"required":["id","password"]}""",
            },
        ];
        var requestedCalls = new[] { "succeeded", "failed", "partial" }
            .Select(status => new AiToolCall { Id = $"call-{status}", Name = "inspect_container", ArgumentsJson = arguments })
            .ToArray();
        handler.Enqueue(Response(kind, "Inspecting approved-id\npassword: synthetic-assistant-value", requestedCalls));
        handler.Enqueue(Response(kind, "Final approved-id context\npassword: synthetic-final-value"));
        var executed = new List<AiToolCall>();
        var statuses = new Queue<string>(["succeeded", "failed", "partial"]);

        var answer = await Create(kind, http, h.Settings).RunTurnAsync(history, tools, (call, _) =>
        {
            executed.Add(call);
            return Task.FromResult(SensitiveResult(statuses.Dequeue()));
        }, CancellationToken.None);

        Assert.Equal(3, executed.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Final approved-id context", answer);
        Assert.DoesNotContain("synthetic-final-value", answer);
        Assert.Contains("<redacted>", answer);
        foreach (var call in executed)
        {
            Assert.Equal("inspect_container", call.Name);
            Assert.False(string.IsNullOrWhiteSpace(call.Id));
            using var original = JsonDocument.Parse(arguments);
            using var actual = JsonDocument.Parse(call.ArgumentsJson);
            Assert.True(JsonElement.DeepEquals(original.RootElement, actual.RootElement));
            Assert.Contains("synthetic-argument-value", call.ArgumentsJson);
        }
        Assert.Equal(3, executed.Select(c => c.Id).Distinct().Count());
        Assert.Same(priorCall, history[2].ToolCalls[0]);
        Assert.Equal(arguments, priorCall.ArgumentsJson);
        Assert.Contains("synthetic-history-value", history[2].Content);
        Assert.Contains("synthetic-description-value", tools[0].Description);

        foreach (var captured in handler.Requests)
        {
            AssertNoSensitiveValues(captured.Body);
            using var request = JsonDocument.Parse(captured.Body);
            var messages = request.RootElement.GetProperty("messages");
            Assert.Equal(History[0].Content, messages[0].GetProperty("content").GetString());
            Assert.Contains("Inspect approved-id", messages[1].GetProperty("content").GetString());
            Assert.Contains("Previous context", messages[2].GetProperty("content").GetString());
            AssertSafeArguments(kind, messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments"));
            var definition = request.RootElement.GetProperty("tools")[0].GetProperty("function");
            Assert.Equal("inspect_container", definition.GetProperty("name").GetString());
            Assert.Contains("Inspect approved-id", definition.GetProperty("description").GetString());
            using var schema = JsonDocument.Parse(tools[0].JsonSchemaParameters);
            Assert.True(JsonElement.DeepEquals(schema.RootElement, definition.GetProperty("parameters")));
            if (kind != AiProviderKind.Ollama)
            {
                Assert.Equal(priorCall.Id, messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
                Assert.Equal(priorCall.Id, messages[3].GetProperty("tool_call_id").GetString());
            }
        }

        using var secondRequest = JsonDocument.Parse(handler.Requests[1].Body);
        var secondMessages = secondRequest.RootElement.GetProperty("messages");
        Assert.Equal(8, secondMessages.GetArrayLength());
        Assert.Contains("Inspecting approved-id", secondMessages[4].GetProperty("content").GetString());
        for (var i = 0; i < executed.Count; i++)
        {
            var wireCall = secondMessages[4].GetProperty("tool_calls")[i];
            AssertSafeArguments(kind, wireCall.GetProperty("function").GetProperty("arguments"));
            var toolResult = secondMessages[5 + i];
            Assert.Equal("tool", toolResult.GetProperty("role").GetString());
            using var result = JsonDocument.Parse(toolResult.GetProperty("content").GetString()!);
            Assert.Equal(new[] { "succeeded", "failed", "partial" }[i], result.RootElement.GetProperty("status").GetString());
            Assert.Equal("approved-id", result.RootElement.GetProperty("id").GetString());
            Assert.Equal("Retained diagnostic context", result.RootElement.GetProperty("detail").GetProperty("context").GetString());
            if (kind == AiProviderKind.Ollama)
            {
                Assert.Equal(executed[i].Name, toolResult.GetProperty("tool_name").GetString());
            }
            else
            {
                Assert.Equal(requestedCalls[i].Id, executed[i].Id);
                Assert.Equal(executed[i].Id, wireCall.GetProperty("id").GetString());
                Assert.Equal(executed[i].Id, toolResult.GetProperty("tool_call_id").GetString());
                Assert.Equal(executed[i].Name, toolResult.GetProperty("name").GetString());
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OversizedStructuredArgumentsAndResultsAreRedactedBeforeTruncation(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var payload = JsonSerializer.Serialize(new
        {
            password = "synthetic-argument-value",
            padding = new string('x', 30_000),
            env = new[] { "API_TOKEN=synthetic-env-value" },
        });
        handler.Enqueue(Response(kind, null, AiContractHarness.Call("inspect_container", payload)));
        handler.Enqueue(Response(kind, "Done"));
        var executed = 0;

        await Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (call, _) =>
        {
            executed++;
            using var original = JsonDocument.Parse(payload);
            using var actual = JsonDocument.Parse(call.ArgumentsJson);
            Assert.True(JsonElement.DeepEquals(original.RootElement, actual.RootElement));
            return Task.FromResult(payload);
        }, CancellationToken.None);

        Assert.Equal(1, executed);
        Assert.Equal(2, handler.Requests.Count);
        AssertNoSensitiveValues(handler.Requests[1].Body);
        using var request = JsonDocument.Parse(handler.Requests[1].Body);
        var messages = request.RootElement.GetProperty("messages");
        var arguments = messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
        var argumentsJson = kind == AiProviderKind.Ollama ? arguments.GetRawText() : arguments.GetString()!;
        using var safeArguments = JsonDocument.Parse(argumentsJson);
        Assert.Equal(JsonValueKind.Object, safeArguments.RootElement.ValueKind);
        Assert.True(safeArguments.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(argumentsJson.Length <= 12_000);
        var resultJson = messages[3].GetProperty("content").GetString()!;
        using var safeResult = JsonDocument.Parse(resultJson);
        Assert.True(safeResult.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(resultJson.Length <= 12_000);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void MessageSerializersDefensivelyRedactDirectInputs(AiProviderKind kind)
    {
        var call = AiContractHarness.Call("inspect_container", SensitiveArguments());
        var assistant = new AiChatMessage
        {
            Role = "assistant", Content = "Retained context\npassword: synthetic-assistant-value", ToolCalls = [call],
        };
        var result = new AiChatMessage
        {
            Role = "tool", Content = SensitiveResult("partial"), ToolCallId = call.Id, ToolName = call.Name,
        };
        foreach (var message in new[] { assistant, result, new AiChatMessage { Role = "user", Content = SensitiveArguments() } })
        {
            var serialized = JsonSerializer.Serialize(kind == AiProviderKind.Ollama
                ? OllamaProvider.ToOllamaMessage(message)
                : OpenAiProvider.ToOpenAiMessage(message));
            AssertNoSensitiveValues(serialized);
            using var wire = JsonDocument.Parse(serialized);
            Assert.Equal(message.Role, wire.RootElement.GetProperty("role").GetString());
            if (message.ToolCalls.Count > 0)
            {
                AssertSafeArguments(kind, wire.RootElement.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments"));
            }
        }
        Assert.Contains("synthetic-argument-value", call.ArgumentsJson);
        Assert.Contains("synthetic-assistant-value", assistant.Content);
        Assert.Contains("synthetic-result-value", result.Content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DiagnosisSanitizesUserEvidenceButPreservesTrustedSystemPrompt(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Response(kind, """{"summary":"Diagnosis","likelyCause":"test","confidence":0.8}"""));
        const string system = "Trusted schema instructions: password: is a field name, not user evidence.";
        var evidence = JsonSerializer.Serialize(new
        {
            id = "approved-id",
            padding = new string('x', 16_000),
            arguments = JsonSerializer.Deserialize<JsonElement>(SensitiveArguments()),
        });
        await ((IAiProvider)Create(kind, http, h.Settings)).CompleteAsync(new AiPromptRequest(system, evidence), CancellationToken.None);

        var captured = Assert.Single(handler.Requests);
        AssertNoSensitiveValues(captured.Body);
        using var request = JsonDocument.Parse(captured.Body);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Equal(system, messages[0].GetProperty("content").GetString());
        using var safe = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("approved-id", safe.RootElement.GetProperty("id").GetString());
        Assert.Equal(16_000, safe.RootElement.GetProperty("padding").GetString()!.Length);
        Assert.Contains("synthetic-argument-value", evidence);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void ToolSerializersPreserveTrustedSchemaWhileRedactingDescription(AiProviderKind kind)
    {
        var tool = new AiToolDefinition
        {
            Name = "inspect_container",
            Description = "Retained tool context\npassword: synthetic-description-value",
            JsonSchemaParameters = """{"type":"object","properties":{"password":{"type":"string","description":"Password parameter"}},"required":["password"]}""",
        };
        var serialized = JsonSerializer.Serialize(kind == AiProviderKind.Ollama
            ? OllamaProvider.ToOllamaTool(tool)
            : OpenAiProvider.ToOpenAiTool(tool));

        AssertNoSensitiveValues(serialized);
        using var wire = JsonDocument.Parse(serialized);
        var function = wire.RootElement.GetProperty("function");
        Assert.Equal(tool.Name, function.GetProperty("name").GetString());
        Assert.Contains("Retained tool context", function.GetProperty("description").GetString());
        using var schema = JsonDocument.Parse(tool.JsonSchemaParameters);
        Assert.True(JsonElement.DeepEquals(schema.RootElement, function.GetProperty("parameters")));
        Assert.Contains("synthetic-description-value", tool.Description);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NestedHttpErrorBodiesAreRedactedIncludingOllamaBadRequest(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable })
        {
            using var handler = new AiContractHarness.ScriptedHttpHandler();
            using var http = new AiHttpClient(handler);
            handler.Enqueue(JsonSerializer.Serialize(new
            {
                error = new { message = "Synthetic request rejected", details = JsonSerializer.Deserialize<JsonElement>(SensitiveArguments()) },
            }), status);
            var error = await Assert.ThrowsAsync<AiProviderException>(() =>
                Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
                    throw new InvalidOperationException("No execution expected"), CancellationToken.None));

            Assert.Equal((int)status, error.StatusCode);
            Assert.Equal(kind == AiProviderKind.Ollama && status == HttpStatusCode.BadRequest
                ? AiFailureKind.Configuration
                : status == HttpStatusCode.ServiceUnavailable ? AiFailureKind.ServerError : AiFailureKind.Unexpected, error.Kind);
            Assert.NotNull(error.ResponseDetail);
            Assert.True(error.ResponseDetail.Length <= 400);
            AssertNoSensitiveValues(error.ToString());
            AssertNoSensitiveValues(error.ResponseDetail);
            Assert.Single(handler.Requests);
        }
    }

    private static string SensitiveArguments() => JsonSerializer.Serialize(new
    {
        id = "approved-id",
        password = "synthetic-argument-value",
        env = new[] { new { name = "API_TOKEN", value = "synthetic-env-value" }, new { name = "MODE", value = "development" } },
        environment = new[] { "DB_PASSWORD=synthetic-array-value", "MODE=development" },
        manifest = "apiVersion: v1\nkind: Secret\nmetadata:\n  name: retained-name\nstringData:\n  arbitrary: synthetic-yaml-value\n",
    });

    private static string SensitiveResult(string status) => JsonSerializer.Serialize(new
    {
        status,
        id = "approved-id",
        detail = new { password = "synthetic-result-value", context = "Retained diagnostic context" },
        output = SensitiveArguments(),
    });

    private static void AssertNoSensitiveValues(string text)
    {
        foreach (var name in new[] { "argument", "env", "array", "yaml", "user", "history", "description", "assistant", "result", "final" })
        {
            Assert.DoesNotContain($"synthetic-{name}-value", text);
        }
    }

    private static void AssertSafeArguments(AiProviderKind kind, JsonElement arguments)
    {
        using var parsed = JsonDocument.Parse(kind == AiProviderKind.Ollama ? arguments.GetRawText() : arguments.GetString()!);
        var root = parsed.RootElement;
        Assert.Equal("approved-id", root.GetProperty("id").GetString());
        Assert.Equal("<redacted>", root.GetProperty("password").GetString());
        Assert.Equal("API_TOKEN", root.GetProperty("env")[0].GetProperty("name").GetString());
        Assert.Equal("<redacted>", root.GetProperty("env")[0].GetProperty("value").GetString());
        Assert.Equal("development", root.GetProperty("env")[1].GetProperty("value").GetString());
        Assert.Equal("MODE=development", root.GetProperty("environment")[1].GetString());
        Assert.Contains("retained-name", root.GetProperty("manifest").GetString());
        AssertNoSensitiveValues(root.GetRawText());
    }

    [Theory]
    [MemberData(nameof(HttpFailures))]
    public async Task HttpFailuresAreTypedRedactedAndNotRetried(AiProviderKind kind, int status, AiFailureKind expected)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue("PASSWORD=synthetic-private-value; Bearer synthetic-bearer " + new string('x', 600), (HttpStatusCode)status);
        var executed = 0;
        var error = await Assert.ThrowsAsync<AiProviderException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
            {
                executed++;
                return Task.FromResult("must not execute");
            }, CancellationToken.None));
        Assert.Equal(expected, error.Kind);
        Assert.Equal(kind, error.Provider);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("Assistant chat", error.Operation);
        Assert.NotNull(error.ResponseDetail);
        Assert.DoesNotContain("synthetic-private-value", error.ResponseDetail);
        Assert.DoesNotContain("synthetic-bearer", error.ResponseDetail);
        Assert.Contains("<redacted>", error.ResponseDetail);
        Assert.True(error.ResponseDetail.Length <= 401);
        Assert.Equal(0, executed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailureAfterCompletedToolDoesNotReplayMutation(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Response(kind, null, AiContractHarness.Call()));
        handler.Enqueue("synthetic outage", HttpStatusCode.ServiceUnavailable);
        var executed = 0;
        await Assert.ThrowsAsync<AiProviderException>(() => Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
        {
            executed++;
            return Task.FromResult("Mutation completed");
        }, CancellationToken.None));
        Assert.Equal(1, executed);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancellationDuringHttpWaitStopsWithoutRetry(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var entered = AiContractHarness.Signal<bool>();
        handler.Responses.Enqueue(async ct =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        var executed = 0;
        var turn = Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
        {
            executed++;
            return Task.FromResult("must not execute");
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, executed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ToolFailureStopsRemainingCallsAndDoesNotRetry(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Response(kind, null, AiContractHarness.Call(), new AiToolCall { Id = "call-2", Name = "stop_container" }));
        var executed = 0;
        var failure = new InvalidOperationException("Synthetic partial operation failure");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
            {
                executed++;
                return Task.FromException<string>(failure);
            }, CancellationToken.None));
        Assert.Same(failure, error);
        Assert.Equal(1, executed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidResponseJsonDoesNotInvokeTools(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue("{broken response");
        var executed = 0;
        await Assert.ThrowsAnyAsync<JsonException>(() => Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
        {
            executed++;
            return Task.FromResult("must not execute");
        }, CancellationToken.None));
        Assert.Equal(0, executed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task IterationLimitBoundsRequests(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        for (var i = 0; i < 8; i++)
        {
            handler.Enqueue(Response(kind, null, new AiToolCall
            {
                Id = $"call-{i}", Name = "inspect_container", ArgumentsJson = JsonSerializer.Serialize(new { id = $"id-{i}" }),
            }));
        }
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (call, _) =>
            {
                calls.Add(call.ArgumentsJson);
                return Task.FromResult("read-only evidence");
            }, CancellationToken.None));
        Assert.Contains("iteration limit", error.Message);
        Assert.Equal(8, handler.Requests.Count);
        Assert.Equal(8, calls.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DiagnosisSerializesJsonModeAndParsesStructuredResponse(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Response(kind, """{"summary":" synthetic diagnosis ","likelyCause":"test","confidence":0.8}"""));
        var provider = (IAiProvider)Create(kind, http, h.Settings);
        var diagnosis = await provider.CompleteAsync(new AiPromptRequest("system", "synthetic evidence"), CancellationToken.None);
        Assert.Equal("synthetic diagnosis", diagnosis.Summary);
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var root = request.RootElement;
        Assert.Equal("synthetic evidence", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(kind == AiProviderKind.Ollama ? "json" : "json_object",
            kind == AiProviderKind.Ollama ? root.GetProperty("format").GetString() : root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancellationAfterCompletedMutationDoesNotReplayIt(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        handler.Enqueue(Response(kind, null, AiContractHarness.Call()));
        var executed = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
            {
                executed++;
                cancellation.Cancel();
                return Task.FromResult("Mutation completed before cancellation");
            }, cancellation.Token));
        Assert.Equal(1, executed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MissingModelOrDeploymentFailsBeforeNetworkOrTools(AiProviderKind kind)
    {
        var h = new AiContractHarness();
        h.SettingsValues[nameof(ISettingsService.AiOpenAiModel)] = "";
        h.SettingsValues[nameof(ISettingsService.AiAzureOpenAiDeployment)] = "";
        h.SettingsValues[nameof(ISettingsService.AiOllamaModel)] = "";
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        var error = await Assert.ThrowsAsync<AiProviderException>(() =>
            Create(kind, http, h.Settings).RunTurnAsync(History, Tools, (_, _) =>
                throw new InvalidOperationException("Must not execute tools"), CancellationToken.None));
        Assert.Equal(AiFailureKind.Configuration, error.Kind);
        Assert.Equal(kind, error.Provider);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenAiCompatibleEndpointCanBeKeyless()
    {
        var h = new AiContractHarness();
        using var handler = new AiContractHarness.ScriptedHttpHandler();
        using var http = new AiHttpClient(handler);
        handler.Enqueue(Response(AiProviderKind.OpenAi, "Local response"));
        await Create(AiProviderKind.OpenAi, http, h.Settings, key: null).RunTurnAsync(History, [], (_, _) =>
            throw new InvalidOperationException("No tool expected"), CancellationToken.None);
        Assert.False(Assert.Single(handler.Requests).Headers.ContainsKey("Authorization"));
    }

    [Theory]
    [InlineData("https://provider.invalid/v1", "https://provider.invalid/v1/chat/completions")]
    [InlineData("https://provider.invalid/v1/chat/completions/", "https://provider.invalid/v1/chat/completions")]
    [InlineData("http://localhost:12345/v1/", "http://localhost:12345/v1/chat/completions")]
    public void OpenAiEndpointNormalizationDoesNotDuplicateRoute(string input, string expected) =>
        Assert.Equal(expected, OpenAiProvider.BuildUri(input, "chat/completions").ToString());

    [Theory]
    [InlineData("file:///tmp/model")]
    [InlineData("not a URI")]
    public void OpenAiEndpointRejectsNonHttpAddresses(string input) =>
        Assert.Throws<InvalidOperationException>(() => OpenAiProvider.BuildUri(input, "chat/completions"));
}
