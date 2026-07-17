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

public sealed class OllamaProvider(HttpClient http, ISettingsService settings) : IAiProvider, IAiChatProvider
{
    public AiProviderKind Kind => AiProviderKind.Ollama;

    public string DisplayName => "Ollama";

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var content = await SendAsync(request, ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        _ = await SendAsync(new AiPromptRequest("Return JSON only.", "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), ct).ConfigureAwait(false);
        return $"Ollama responded using model '{settings.AiOllamaModel}'.";
    }

    private async Task<string> SendAsync(AiPromptRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.AiOllamaModel))
        {
            throw new InvalidOperationException("Choose an Ollama model in Settings first.");
        }

        var endpoint = NormalizeBase(settings.AiOllamaEndpoint, "http://localhost:11434");
        using var response = await http.PostAsJsonAsync(new Uri(endpoint, "/api/chat"), new
        {
            model = settings.AiOllamaModel.Trim(),
            stream = false,
            format = "json",
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            },
            options = new { temperature = 0.2 },
        }, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama request failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static Uri NormalizeBase(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return new Uri(text.EndsWith('/') ? text : text + "/", UriKind.Absolute);
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500] + "…";

    public async Task<string> RunTurnAsync(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.AiOllamaModel))
        {
            throw new InvalidOperationException("Choose an Ollama model in Settings first.");
        }

        var endpoint = NormalizeBase(settings.AiOllamaEndpoint, "http://localhost:11434");
        var model = settings.AiOllamaModel.Trim();
        var messages = history.ToList();
        for (var i = 0; i < 8; i++)
        {
            using var response = await http.PostAsJsonAsync(new Uri(endpoint, "/api/chat"), new
            {
                model,
                stream = false,
                messages = messages.Select(ToOllamaMessage).ToList(),
                tools = tools.Select(ToOllamaTool).ToList(),
                options = new { temperature = 0.2 },
            }, ct).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Ollama chat request failed ({(int)response.StatusCode}): {Trim(body)}. The model '{model}' must support tool calling (e.g. llama3.1, qwen2.5, mistral-nemo).");
            }

            using var doc = JsonDocument.Parse(body);
            var messageElement = doc.RootElement.GetProperty("message");
            var turn = ParseToolTurn(messageElement);
            if (turn.ToolCalls.Count == 0)
            {
                return string.IsNullOrWhiteSpace(turn.AssistantText) ? "Done." : turn.AssistantText!;
            }

            messages.Add(new AiChatMessage { Role = "assistant", Content = turn.AssistantText, ToolCalls = turn.ToolCalls });
            foreach (var call in turn.ToolCalls)
            {
                var toolResult = await invokeToolAsync(call, ct).ConfigureAwait(false);
                messages.Add(new AiChatMessage
                {
                    Role = "tool",
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Content = toolResult,
                });
            }
        }

        throw new InvalidOperationException("Stopped because the assistant reached the tool-iteration limit.");
    }

    private static object ToOllamaMessage(AiChatMessage message)
    {
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

    private static object ToOllamaTool(AiToolDefinition tool)
    {
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
