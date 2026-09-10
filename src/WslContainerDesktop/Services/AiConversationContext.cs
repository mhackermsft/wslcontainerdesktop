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

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public static class AiConversationContext
{
    public const string TruncationNotice =
        "[Conversation truncated: older complete turns and their tool evidence were omitted. " +
        "Missing outcomes are unknown; inspect current state and obtain fresh approval, never replay an action.]";

    public static AiChatConfiguration Capture(ISettingsService settings, AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenAi => new(kind, Endpoint(settings.AiOpenAiEndpoint, OpenAiProvider.DefaultEndpoint),
            settings.AiOpenAiModel.Trim()),
        AiProviderKind.AzureOpenAi => new(kind, Endpoint(settings.AiAzureOpenAiEndpoint, ""),
            settings.AiAzureOpenAiDeployment.Trim()),
        AiProviderKind.Ollama => new(kind, Endpoint(settings.AiOllamaEndpoint, "http://localhost:11434"),
            settings.AiOllamaModel.Trim()),
        AiProviderKind.GitHubCopilot => new(kind, "github-copilot", settings.AiGitHubCopilotModel.Trim()),
        _ => throw new InvalidOperationException("Choose an assistant provider before starting a conversation."),
    };

    // Application input ceilings, not claims about a server's negotiated context size.
    // UTF-8 JSON bytes conservatively account for non-ASCII text, escaping and protocol overhead.
    // Unknown/custom models get the smaller ceiling until capability observations are available.
    public static int InputByteLimit(AiChatConfiguration configuration) =>
        configuration.Model is "gpt-4o" or "gpt-4o-mini" or "llama3.1" or "qwen2.5"
            ? 65_536 : 32_768;

    public static int Measure(
        IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools) =>
        checked(JsonSerializer.SerializeToUtf8Bytes(new { messages, tools }).Length
            + 2_048 + messages.Count * 128 + tools.Count * 128
            + (messages.Any(m => m.Role == "system" && m.Content == TruncationNotice) ? 0 : 1_024));

    public static IReadOnlyList<AiChatMessage> Prepare(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        AiChatConfiguration configuration)
    {
        var messages = history.Select(AiTextSanitizer.SanitizeMessage).ToList();
        var definitions = tools.Select(AiTextSanitizer.SanitizeDefinition).ToArray();
        while (Measure(messages, definitions) > InputByteLimit(configuration))
        {
            var firstUser = messages.FindIndex(m => m.Role == "user");
            var nextUser = firstUser < 0 ? -1 : messages.FindIndex(firstUser + 1, m => m.Role == "user");
            if (nextUser < 0)
            {
                throw new InvalidOperationException(
                    "Assistant context budget exceeded by the current turn or tool definitions. " +
                    "Evidence was not silently dropped and no action was retried. Reset or use a smaller request.");
            }

            messages.RemoveRange(firstUser, nextUser - firstUser);
            if (!messages.Any(m => m.Role == "system" && m.Content == TruncationNotice))
                messages.Insert(firstUser, new AiChatMessage { Role = "system", Content = TruncationNotice });
        }

        return messages.ToArray();
    }

    private static string Endpoint(string? value, string fallback) =>
        (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()).TrimEnd('/');
}
