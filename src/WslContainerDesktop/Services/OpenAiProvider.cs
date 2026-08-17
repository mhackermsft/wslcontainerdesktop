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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class OpenAiProvider(AiHttpClient http, ISettingsService settings, IAiCredentialStore credentials) : IAiProvider, IAiChatProvider
{
    public AiProviderKind Kind => AiProviderKind.OpenAi;

    /// <summary>Base URL used when the user has not configured one.</summary>
    public const string DefaultEndpoint = "https://api.openai.com/v1";

    public string DisplayName => Kind.DisplayName();

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var content = await SendAsync(request, "Diagnosis", ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        _ = await SendAsync(new AiPromptRequest("Return JSON only.", "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), "Provider test", ct).ConfigureAwait(false);
        return $"OpenAI-compatible provider responded from {ChatCompletionsUri()} using model '{settings.AiOpenAiModel}'.";
    }

    private async Task<string> SendAsync(AiPromptRequest request, string operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.AiOpenAiModel))
        {
            throw new InvalidOperationException("Choose an OpenAI-compatible model in Settings first.");
        }

        var uri = ChatCompletionsUri();
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        ApplyAuthorization(message);
        message.Content = JsonContent.Create(new
        {
            model = settings.AiOpenAiModel.Trim(),
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            },
        });

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw AiProviderException.FromHttpFailure(Kind, operation, response.StatusCode, uri.ToString(), settings.AiOpenAiModel, body);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    public async Task<string> RunTurnAsync(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.AiOpenAiModel))
        {
            throw new InvalidOperationException("Choose an OpenAI-compatible model in Settings first.");
        }

        var uri = ChatCompletionsUri();
        var messages = history.ToList();
        for (var i = 0; i < 8; i++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri);
            ApplyAuthorization(message);
            message.Content = JsonContent.Create(new
            {
                model = settings.AiOpenAiModel.Trim(),
                temperature = 0.2,
                messages = messages.Select(ToOpenAiMessage).ToList(),
                tools = tools.Select(ToOpenAiTool).ToList(),
                tool_choice = "auto",
            });

            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw AiProviderException.FromHttpFailure(Kind, "Assistant chat", response.StatusCode, uri.ToString(), settings.AiOpenAiModel, body);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var turn = ParseToolTurn(root);
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

    private void ApplyAuthorization(HttpRequestMessage message)
    {
        // Local OpenAI-compatible servers (Ollama /v1, LM Studio, llama.cpp, vLLM) usually accept
        // no auth at all, so the key is optional: only send the header when one is saved.
        if (credentials.TryReadSecret(AiProviderKind.OpenAi, out var key) && !string.IsNullOrWhiteSpace(key))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private Uri ChatCompletionsUri() => BuildUri(settings.AiOpenAiEndpoint, "chat/completions");

    /// <summary>
    /// Resolves a user-supplied OpenAI-compatible base URL into an absolute endpoint for
    /// <paramref name="relativePath"/>, defaulting to the public OpenAI endpoint when blank.
    /// </summary>
    public static Uri BuildUri(string? endpoint, string relativePath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        var trimmed = baseUrl.TrimEnd('/');

        // Tolerate a base URL that already points at the chat-completions route.
        const string ChatSuffix = "/chat/completions";
        if (trimmed.EndsWith(ChatSuffix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^ChatSuffix.Length];
        }

        var candidate = trimmed + "/" + relativePath;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"The OpenAI-compatible endpoint '{baseUrl}' is not a valid absolute http(s) URL. " +
                "Example: http://localhost:11434/v1");
        }

        return uri;
    }

    internal static object ToOpenAiMessage(AiChatMessage message)
    {
        if (message.Role == "tool")
        {
            return new
            {
                role = "tool",
                tool_call_id = message.ToolCallId,
                name = message.ToolName,
                content = message.Content ?? string.Empty,
            };
        }

        if (message.ToolCalls.Count > 0)
        {
            return new
            {
                role = "assistant",
                content = message.Content,
                tool_calls = message.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.ArgumentsJson },
                }).ToList(),
            };
        }

        return new { role = message.Role, content = message.Content ?? string.Empty };
    }

    internal static object ToOpenAiTool(AiToolDefinition tool)
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

    internal static AiToolTurn ParseToolTurn(JsonElement message)
    {
        var text = message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null
            ? content.GetString()
            : null;
        var calls = new List<AiToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var function))
                {
                    continue;
                }

                var name = function.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                calls.Add(new AiToolCall
                {
                    Id = call.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    Name = name,
                    ArgumentsJson = function.TryGetProperty("arguments", out var args) ? args.GetString() ?? "{}" : "{}",
                });
            }
        }

        return new AiToolTurn { AssistantText = text, ToolCalls = calls };
    }
}
