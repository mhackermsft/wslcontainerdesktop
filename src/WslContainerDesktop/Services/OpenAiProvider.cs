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

public sealed class OpenAiProvider(HttpClient http, ISettingsService settings, IAiCredentialStore credentials) : IAiProvider, IAiChatProvider
{
    public AiProviderKind Kind => AiProviderKind.OpenAi;

    public string DisplayName => "OpenAI";

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var content = await SendAsync(request, ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        _ = await SendAsync(new AiPromptRequest("Return JSON only.", "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), ct).ConfigureAwait(false);
        return $"OpenAI-compatible provider responded using model '{settings.AiOpenAiModel}'.";
    }

    private async Task<string> SendAsync(AiPromptRequest request, CancellationToken ct)
    {
        if (!credentials.TryReadSecret(AiProviderKind.OpenAi, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Enter and save an OpenAI API key in Settings first.");
        }

        if (string.IsNullOrWhiteSpace(settings.AiOpenAiModel))
        {
            throw new InvalidOperationException("Choose an OpenAI model in Settings first.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUri());
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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
            throw new InvalidOperationException($"OpenAI request failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    public async Task<AiToolTurn> ChatAsync(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken ct)
    {
        if (!credentials.TryReadSecret(AiProviderKind.OpenAi, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Enter and save an OpenAI API key in Settings first.");
        }

        if (string.IsNullOrWhiteSpace(settings.AiOpenAiModel))
        {
            throw new InvalidOperationException("Choose an OpenAI model in Settings first.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUri());
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new
        {
            model = settings.AiOpenAiModel.Trim(),
            temperature = 0.2,
            messages = history.Select(ToOpenAiMessage).ToList(),
            tools = tools.Select(ToOpenAiTool).ToList(),
            tool_choice = "auto",
        });

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI chat request failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        return ParseToolTurn(root);
    }

    private Uri ChatCompletionsUri()
    {
        var endpoint = string.IsNullOrWhiteSpace(settings.AiOpenAiEndpoint)
            ? "https://api.openai.com/v1"
            : settings.AiOpenAiEndpoint.Trim();
        if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(endpoint, UriKind.Absolute);
        }

        endpoint = endpoint.TrimEnd('/');
        return new Uri(endpoint + "/chat/completions", UriKind.Absolute);
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500] + "…";

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
