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

public sealed class AzureOpenAiProvider(AiHttpClient http, ISettingsService settings, IAiCredentialStore credentials,
    IAiCapabilityService? capabilities = null) : IAiProvider, IAiChatProvider
{
    private const string ApiVersion = "2024-10-21";

    public AiProviderKind Kind => AiProviderKind.AzureOpenAi;

    public string DisplayName => Kind.DisplayName();

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var content = await SendAsync(request, "Diagnosis", ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        _ = await SendAsync(new AiPromptRequest("Return JSON only.", "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), "Provider test", ct).ConfigureAwait(false);
        return $"Azure OpenAI responded using deployment '{settings.AiAzureOpenAiDeployment}'.";
    }

    private async Task<string> SendAsync(AiPromptRequest request, string operation, CancellationToken ct)
    {
        if (!credentials.TryReadSecret(AiProviderKind.AzureOpenAi, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw ConfigurationError(operation, "Enter and save an Azure OpenAI key in Settings first.");
        }

        if (string.IsNullOrWhiteSpace(settings.AiAzureOpenAiEndpoint) || string.IsNullOrWhiteSpace(settings.AiAzureOpenAiDeployment))
        {
            throw ConfigurationError(operation, "Enter an Azure OpenAI endpoint and deployment in Settings first.");
        }

        var uri = CompletionUri();
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Add("api-key", key);
        var payload = new Dictionary<string, object>
        {
            ["temperature"] = 0.2,
            ["messages"] = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = AiTextSanitizer.Sanitize(request.UserPrompt, AiTextSanitizer.DiagnosticLimit) },
            },
        };
        if (capabilities?.GetCached(AiConversationContext.Capture(settings, Kind)).StructuredJson.Support == AiSupport.Supported)
            payload["response_format"] = new { type = "json_object" };
        message.Content = JsonContent.Create(payload);

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw AiProviderException.FromHttpFailure(Kind, operation, response.StatusCode, uri.ToString(), settings.AiAzureOpenAiDeployment, body);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
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
        if (!credentials.TryReadSecret(AiProviderKind.AzureOpenAi, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw ConfigurationError("Assistant chat", "Enter and save an Azure OpenAI key in Settings first.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Endpoint) || string.IsNullOrWhiteSpace(configuration.Model))
        {
            throw ConfigurationError("Assistant chat", "Enter an Azure OpenAI endpoint and deployment in Settings first.");
        }

        var uri = CompletionUri(configuration.Endpoint, configuration.Model);
        var messages = request.History.Select(AiTextSanitizer.SanitizeMessage).ToList();
        var transcript = new List<AiChatMessage>();
        for (var i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            messages = AiConversationContext.Prepare(messages, tools, configuration).ToList();
            using var message = new HttpRequestMessage(HttpMethod.Post, uri);
            message.Headers.Add("api-key", key);
            var payload = new Dictionary<string, object>
            {
                ["temperature"] = 0.2,
                ["messages"] = messages.Select(OpenAiProvider.ToOpenAiMessage).ToList(),
            };
            if (tools.Count > 0)
            {
                payload["tools"] = tools.Select(OpenAiProvider.ToOpenAiTool).ToList();
                payload["tool_choice"] = "auto";
            }
            message.Content = JsonContent.Create(payload);

            using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                throw AiProviderException.FromHttpFailure(Kind, "Assistant chat", response.StatusCode, uri.ToString(), configuration.Model, body);
            }

            using var doc = JsonDocument.Parse(body);
            var turn = OpenAiProvider.ParseToolTurn(doc.RootElement.GetProperty("choices")[0].GetProperty("message"));
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

    private static AiProviderException ConfigurationError(string operation, string message) => new(
        AiProviderKind.AzureOpenAi,
        operation,
        message,
        AiFailureKind.Configuration);

    private Uri CompletionUri() => CompletionUri(settings.AiAzureOpenAiEndpoint!, settings.AiAzureOpenAiDeployment!);

    private static Uri CompletionUri(string baseEndpoint, string model)
    {
        var endpoint = baseEndpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw ConfigurationError("Assistant chat", "Enter a valid absolute http(s) Azure OpenAI endpoint.");
        var deployment = Uri.EscapeDataString(model.Trim());
        return new Uri($"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}", UriKind.Absolute);
    }
}
