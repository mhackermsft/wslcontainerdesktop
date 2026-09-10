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

using System.Net.Http.Json;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class OllamaProvider(AiHttpClient http, ISettingsService settings) : IAiProvider, IAiChatProvider
{
    public AiProviderKind Kind => AiProviderKind.Ollama;

    public string DisplayName => Kind.DisplayName();

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var content = await SendAsync(request, "Diagnosis", ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        _ = await SendAsync(new AiPromptRequest("Return JSON only.", "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), "Provider test", ct).ConfigureAwait(false);
        return $"Ollama responded using model '{settings.AiOllamaModel}'.";
    }

    private async Task<string> SendAsync(AiPromptRequest request, string operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.AiOllamaModel))
        {
            throw MissingModel(operation);
        }

        var endpoint = NormalizeBase(settings.AiOllamaEndpoint, "http://localhost:11434");
        var uri = new Uri(endpoint, "/api/chat");
        using var response = await http.PostAsJsonAsync(uri, new
        {
            model = settings.AiOllamaModel.Trim(),
            stream = false,
            format = "json",
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = AiTextSanitizer.Sanitize(request.UserPrompt, AiTextSanitizer.DiagnosticLimit) },
            },
            options = new { temperature = 0.2 },
        }, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw AiProviderException.FromHttpFailure(Kind, operation, response.StatusCode, uri.ToString(), settings.AiOllamaModel, body);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static Uri NormalizeBase(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Enter a valid absolute http(s) Ollama endpoint.");
        return uri;
    }

    public async Task<string> RunTurnAsync(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
        => (await RunTurnAsync(new AiChatRequest(AiConversationContext.Capture(settings, Kind), history),
            tools, invokeToolAsync, ct).ConfigureAwait(false)).FinalText;

    public async Task<AiChatTurnResult> RunTurnAsync(
        AiChatRequest request,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
    {
        var configuration = request.Configuration;
        if (string.IsNullOrWhiteSpace(configuration.Model))
        {
            throw MissingModel("Assistant chat");
        }

        var endpoint = NormalizeBase(configuration.Endpoint, "http://localhost:11434");
        var uri = new Uri(endpoint, "/api/chat");
        var model = configuration.Model.Trim();
        var messages = request.History.Select(AiTextSanitizer.SanitizeMessage).ToList();
        var transcript = new List<AiChatMessage>();
        for (var i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            messages = AiConversationContext.Prepare(messages, tools, configuration).ToList();
            using var response = await http.PostAsJsonAsync(uri, new
            {
                model,
                stream = false,
                messages = messages.Select(ToOllamaMessage).ToList(),
                tools = tools.Select(ToOllamaTool).ToList(),
                options = new { temperature = 0.2 },
            }, ct).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    throw new AiProviderException(
                        Kind,
                        "Assistant chat",
                        $"Ollama rejected the chat request. The model '{model}' may not support tool calling (try llama3.1, qwen2.5, or mistral-nemo).",
                        AiFailureKind.Configuration,
                        (int)response.StatusCode,
                        uri.ToString(),
                        model,
                        body);
                }

                throw AiProviderException.FromHttpFailure(Kind, "Assistant chat", response.StatusCode, uri.ToString(), model, body);
            }

            using var doc = JsonDocument.Parse(body);
            var messageElement = doc.RootElement.GetProperty("message");
            var turn = ParseToolTurn(messageElement);
            if (turn.ToolCalls.Count == 0)
            {
                var finalText = string.IsNullOrWhiteSpace(turn.AssistantText) ? "Done." : AiTextSanitizer.Sanitize(turn.AssistantText!);
                transcript.Add(new AiChatMessage { Role = "assistant", Content = finalText });
                return new AiChatTurnResult(finalText, transcript.ToArray());
            }

            // The execution calls stay original; only redacted copies enter conversation history.
            var assistant = AiTextSanitizer.SanitizeMessage(new AiChatMessage { Role = "assistant", Content = turn.AssistantText, ToolCalls = turn.ToolCalls });
            messages.Add(assistant);
            transcript.Add(assistant);
            foreach (var call in turn.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var toolResult = await invokeToolAsync(call, ct).ConfigureAwait(false);
                var outcome = new AiChatMessage
                {
                    Role = "tool",
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Content = AiTextSanitizer.Sanitize(toolResult),
                };
                messages.Add(outcome);
                transcript.Add(outcome);
            }
        }

        throw new InvalidOperationException("Stopped because the assistant reached the tool-iteration limit.");
    }

    private static AiProviderException MissingModel(string operation) => new(
        AiProviderKind.Ollama,
        operation,
        "Choose an Ollama model in Settings first.",
        AiFailureKind.Configuration);

    internal static object ToOllamaMessage(AiChatMessage message)
    {
        message = AiTextSanitizer.SanitizeMessage(message);
        if (message.Role == "tool")
        {
            // Ollama identifies tool results by name, not an id.
            return new
            {
                role = "tool",
                tool_name = message.ToolName ?? string.Empty,
                content = message.Content ?? string.Empty,
            };
        }

        if (message.ToolCalls.Count > 0)
        {
            return new
            {
                role = "assistant",
                content = message.Content ?? string.Empty,
                tool_calls = message.ToolCalls.Select(c => new
                {
                    function = new
                    {
                        name = c.Name,
                        // Ollama expects arguments as a JSON object, not a stringified payload.
                        arguments = ParseArguments(c.ArgumentsJson),
                    },
                }).ToList(),
            };
        }

        return new { role = message.Role, content = message.Content ?? string.Empty };
    }

    internal static object ToOllamaTool(AiToolDefinition tool)
    {
        tool = AiTextSanitizer.SanitizeDefinition(tool);
        using var schema = JsonDocument.Parse(tool.JsonSchemaParameters);
        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = schema.RootElement.Clone(),
            },
        };
    }

    private static JsonElement ParseArguments(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private static AiToolTurn ParseToolTurn(JsonElement message)
    {
        var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
        var calls = new List<AiToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function) ||
                    !function.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Ollama returns arguments as a JSON object; normalize to the string form our model uses.
                var argumentsJson = "{}";
                if (function.TryGetProperty("arguments", out var arguments))
                {
                    argumentsJson = arguments.ValueKind == JsonValueKind.String
                        ? arguments.GetString() ?? "{}"
                        : arguments.GetRawText();
                }

                calls.Add(new AiToolCall
                {
                    // Ollama does not supply tool-call ids; synthesize one for internal tracking.
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    ArgumentsJson = argumentsJson,
                });
            }
        }

        return new AiToolTurn { AssistantText = text, ToolCalls = calls };
    }
}
