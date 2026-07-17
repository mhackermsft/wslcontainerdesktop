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

    public Task<AiToolTurn> ChatAsync(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken ct) =>
        throw new NotSupportedException("The Container AI Assistant requires a tool-capable provider. Choose GitHub Copilot, Azure OpenAI, or OpenAI-compatible in Settings.");
}
