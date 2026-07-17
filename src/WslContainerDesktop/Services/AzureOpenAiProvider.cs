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

public sealed class AzureOpenAiProvider(HttpClient http, ISettingsService settings, IAiCredentialStore credentials) : IAiProvider
{
    private const string ApiVersion = "2024-10-21";

    public AiProviderKind Kind => AiProviderKind.AzureOpenAi;

    public string DisplayName => "Azure OpenAI";

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var content = await SendAsync(request, ct).ConfigureAwait(false);
        return AiProviderJson.ParseDiagnosis(content);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        _ = await SendAsync(new AiPromptRequest("Return JSON only.", "Return {\"summary\":\"ok\",\"likelyCause\":\"configured\",\"evidenceCited\":[],\"suggestedFix\":{\"description\":\"none\",\"commands\":[],\"fileEdits\":[]},\"confidence\":1}"), ct).ConfigureAwait(false);
        return $"Azure OpenAI responded using deployment '{settings.AiAzureOpenAiDeployment}'.";
    }

    private async Task<string> SendAsync(AiPromptRequest request, CancellationToken ct)
    {
        if (!credentials.TryReadSecret(AiProviderKind.AzureOpenAi, out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Enter and save an Azure OpenAI key in Settings first.");
        }

        if (string.IsNullOrWhiteSpace(settings.AiAzureOpenAiEndpoint) || string.IsNullOrWhiteSpace(settings.AiAzureOpenAiDeployment))
        {
            throw new InvalidOperationException("Enter an Azure OpenAI endpoint and deployment in Settings first.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, CompletionUri());
        message.Headers.Add("api-key", key);
        message.Content = JsonContent.Create(new
        {
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
            throw new InvalidOperationException($"Azure OpenAI request failed ({(int)response.StatusCode}): {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private Uri CompletionUri()
    {
        var endpoint = settings.AiAzureOpenAiEndpoint!.Trim().TrimEnd('/');
        var deployment = Uri.EscapeDataString(settings.AiAzureOpenAiDeployment!.Trim());
        return new Uri($"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}", UriKind.Absolute);
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
