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

using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public static class AiConversationContext
{
    public const string TruncationNotice =
        "[Conversation truncated: older complete turns and their tool evidence were omitted. " +
        "Missing outcomes are unknown; inspect current state and obtain fresh approval, never replay an action.]";

    /// <summary>Marks the digest message so it can be recognized, replaced, and budget-reserved.</summary>
    internal const string DigestPrefix = "[Earlier turns were summarized to fit the context budget.";

    private const string DigestSuffix =
        " Only the facts listed above survive; treat anything not listed as unknown, inspect current " +
        "state before acting on it, and never replay an action on the strength of this summary.]";

    /// <summary>
    /// Ceiling for the summary itself. Without it the digest could grow as fast as the turns it
    /// replaces and the eviction loop would stop making progress.
    /// </summary>
    private const int MaxDigestBytes = 1_536;

    public static AiChatConfiguration Capture(ISettingsService settings, AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenAi => new(kind, Endpoint(settings.AiOpenAiEndpoint, OpenAiProvider.DefaultEndpoint),
            settings.AiOpenAiModel.Trim()),
        AiProviderKind.FoundryLocal => new(kind, Endpoint(settings.AiFoundryLocalEndpoint, ""),
            settings.AiFoundryLocalModel.Trim()),
        AiProviderKind.AzureOpenAi => new(kind, Endpoint(settings.AiAzureOpenAiEndpoint, ""),
            settings.AiAzureOpenAiDeployment.Trim()),
        AiProviderKind.Ollama => new(kind, Endpoint(settings.AiOllamaEndpoint, "http://localhost:11434"),
            settings.AiOllamaModel.Trim()),
        AiProviderKind.GitHubCopilot => new(kind, "github-copilot", settings.AiGitHubCopilotModel.Trim()),
        _ => throw new InvalidOperationException("Choose an assistant provider before starting a conversation."),
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<AiChatConfiguration,
        (int Bytes, DateTimeOffset Expires)> ObservedLimits = new();

    /// <summary>Ceiling used when the model's own context window is unknown.</summary>
    public const int DefaultInputByteLimit = 32_768;

    /// <summary>
    /// Upper bound regardless of what a model reports, so an implausible or malformed context
    /// figure cannot turn into unbounded request growth.
    /// </summary>
    public const int MaxInputByteLimit = 262_144;

    /// <summary>
    /// Share of the context window reserved for the model's reply. The window covers input *and*
    /// output, so spending all of it on input leaves nothing to answer with.
    /// </summary>
    private const double InputShareOfWindow = 0.75;

    /// <summary>
    /// Bytes assumed per token when deriving a budget. Deliberately below what real text encodes to
    /// (English prose is nearer 4, tool JSON nearer 3): the conversion has to under-estimate the
    /// room available, because over-estimating means the provider rejects the request outright.
    /// </summary>
    private const double ConservativeBytesPerToken = 2.5;

    /// <summary>
    /// The accounted-byte budget for one request. A byte-accounted observation is authoritative and
    /// can only LOWER the ceiling. Absent that, a reported context window raises it — conservatively
    /// and within <see cref="MaxInputByteLimit"/> — because capping a 32k-token model at the
    /// small-model default wastes most of what it can actually hold.
    /// </summary>
    public static int InputByteLimit(AiChatConfiguration configuration)
    {
        if (ObservedLimits.TryGetValue(configuration, out var limit) && limit.Expires > DateTimeOffset.UtcNow)
            return Math.Clamp(limit.Bytes, 1, MaxInputByteLimit);
        ForgetObservedLimit(configuration);
        return DefaultInputByteLimit;
    }

    /// <summary>Converts a reported context window into a deliberately pessimistic byte budget.</summary>
    public static int DeriveByteLimitFromContextTokens(long contextTokens)
    {
        if (contextTokens <= 0)
            return DefaultInputByteLimit;
        var derived = contextTokens * InputShareOfWindow * ConservativeBytesPerToken;
        if (derived <= DefaultInputByteLimit)
            return DefaultInputByteLimit;
        return derived >= MaxInputByteLimit ? MaxInputByteLimit : (int)derived;
    }

    /// <summary>
    /// Records the ceiling for a configuration. <paramref name="bytes"/> is an explicitly
    /// byte-accounted observation and always wins, because it is measured rather than inferred;
    /// <paramref name="contextTokens"/> is only used when no such measurement exists.
    /// </summary>
    internal static void SetObservedLimit(AiChatConfiguration configuration, int? bytes,
        DateTimeOffset expires, long? contextTokens = null)
    {
        ForgetObservedLimit(configuration);
        var resolved = bytes is > 0
            ? Math.Min(bytes.Value, MaxInputByteLimit)
            : contextTokens is > 0 ? DeriveByteLimitFromContextTokens(contextTokens.Value) : 0;
        if (resolved > 0)
        {
            if (ObservedLimits.Count >= 32) ObservedLimits.Clear();
            ObservedLimits[configuration] = (resolved, expires);
        }
    }

    internal static void ForgetObservedLimit(AiChatConfiguration configuration) =>
        ObservedLimits.TryRemove(configuration, out _);

    /// <summary>
    /// Accounted size of one request: the wire-shaped payload plus reserves for what the provider
    /// adds around it.
    ///
    /// The tool array is serialized in the exact shape adapters send, so its own reserve only has to
    /// cover per-provider envelope differences, not the definitions themselves — measured against a
    /// real OpenAI-shaped body, the previous 128 bytes per tool was the single largest source of a
    /// 43% overshoot. The fixed reserve still covers top-level fields (model, stream, options), and
    /// messages keep the larger allowance because their envelopes vary more between providers.
    /// </summary>
    public static int Measure(
        IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools) =>
        checked(JsonSerializer.SerializeToUtf8Bytes(new
        {
            messages,
            // Every adapter sends a schema object, not JSON embedded in an escaped string.
            // Account for the function envelope too, while retaining the protocol reserves.
            tools = tools.Select(tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = ReadSchema(tool) },
            }),
        }).Length
            + 2_048 + messages.Count * 128 + tools.Count * 48
            + (messages.Any(m => m.Role == "system" && IsDigest(m.Content)) ? 0 : 1_024));

    private static JsonElement ReadSchema(AiToolDefinition tool)
    {
        try
        {
            using var document = JsonDocument.Parse(tool.JsonSchemaParameters);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Assistant tool schema must be a JSON object.");
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Assistant tool schema is invalid; no request was sent.", ex);
        }
    }

    public static IReadOnlyList<AiChatMessage> Prepare(
        IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        AiChatConfiguration configuration)
    {
        var messages = history.Select(AiTextSanitizer.SanitizeMessage).ToList();
        var definitions = tools.Select(AiTextSanitizer.SanitizeDefinition).ToArray();
        var facts = new List<string>();

        while (Measure(messages, definitions) > InputByteLimit(configuration))
        {
            var firstUser = messages.FindIndex(m => m.Role == "user");
            var nextUser = firstUser < 0 ? -1 : messages.FindIndex(firstUser + 1, m => m.Role == "user");
            if (nextUser < 0)
            {
                // Nothing left to evict but the live turn. Fall back to the bare notice rather than
                // dropping the marker outright: a history that was truncated must never look
                // complete, or the model treats missing outcomes as absent ones.
                if (ReduceDigestToNotice(messages))
                    continue;

                throw new InvalidOperationException(
                    "Assistant context budget exceeded by the current turn or tool definitions. " +
                    "Evidence was not silently dropped and no action was retried. Reset or use a smaller request.");
            }

            // Summarize before discarding: what the assistant already did is the part of an older
            // turn that still matters, and a bare "some turns were dropped" throws it away.
            CollectFacts(messages.GetRange(firstUser, nextUser - firstUser), facts);
            messages.RemoveRange(firstUser, nextUser - firstUser);
            RemoveDigest(messages);
            messages.Insert(messages.FindIndex(m => m.Role == "user") is var at && at >= 0 ? at : messages.Count,
                new AiChatMessage { Role = "system", Content = BuildDigest(facts) });
        }

        return messages.ToArray();
    }

    private static bool RemoveDigest(List<AiChatMessage> messages)
    {
        var index = messages.FindIndex(m => m.Role == "system" && IsDigest(m.Content));
        if (index < 0)
            return false;
        messages.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Shrinks an over-large digest to the fixed truncation notice. Returns false when it is already
    /// the notice, so the caller stops rather than looping.
    /// </summary>
    private static bool ReduceDigestToNotice(List<AiChatMessage> messages)
    {
        var index = messages.FindIndex(m => m.Role == "system" && IsDigest(m.Content));
        if (index < 0 || messages[index].Content == TruncationNotice)
            return false;
        messages[index] = new AiChatMessage { Role = "system", Content = TruncationNotice };
        return true;
    }

    internal static bool IsDigest(string? content) =>
        content is not null
        && (content.StartsWith(DigestPrefix, StringComparison.Ordinal) || content == TruncationNotice);

    /// <summary>
    /// Extracts the durable facts from turns about to be discarded: what was asked, which tools ran,
    /// and how they finished. This is deliberately mechanical rather than model-written — it costs
    /// no round trip, cannot fail mid-turn, and for a tool-calling assistant the actionable history
    /// is the record of actions, not the prose around them.
    /// </summary>
    private static void CollectFacts(IEnumerable<AiChatMessage> evicted, List<string> facts)
    {
        var batch = evicted as IReadOnlyList<AiChatMessage> ?? evicted.ToArray();

        // A call message always precedes its result, so "has a result already been recorded" cannot
        // be answered while walking forward. Pre-scan for the paired ids first, otherwise every
        // normally-completed call is reported as "outcome unknown" alongside its real outcome.
        var answered = new HashSet<string>(
            batch.Where(m => m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId)).Select(m => m.ToolCallId!),
            StringComparer.Ordinal);

        foreach (var message in batch)
        {
            switch (message.Role)
            {
                case "user" when !string.IsNullOrWhiteSpace(message.Content):
                    facts.Add("asked: " + Clip(message.Content, 96));
                    break;
                case "tool" when !string.IsNullOrWhiteSpace(message.ToolName):
                    facts.Add($"{message.ToolName} -> {Outcome(message.Content)}");
                    break;
                case "assistant":
                    foreach (var call in message.ToolCalls)
                    {
                        // Only calls whose result was not also evicted are genuinely unknown.
                        if (!answered.Contains(call.Id))
                            facts.Add($"{call.Name} -> outcome unknown");
                    }

                    break;
            }
        }
    }

    /// <summary>Reads a structured tool result's status, falling back to its opening text.</summary>
    private static string Outcome(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "no output";
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String)
            {
                return status.GetString() ?? "unknown";
            }
        }
        catch (JsonException)
        {
            // Not structured evidence; summarize the text instead.
        }

        return Clip(content, 72);
    }

    private static string BuildDigest(List<string> facts)
    {
        // Newest facts matter most, so drop from the front when the summary will not fit.
        var text = Compose(facts);
        while (facts.Count > 1 && Encoding.UTF8.GetByteCount(text) > MaxDigestBytes)
        {
            facts.RemoveAt(0);
            text = Compose(facts);
        }

        return text;

        static string Compose(List<string> lines) =>
            DigestPrefix + " Facts from them:\n- " + string.Join("\n- ", lines) + DigestSuffix;
    }

    private static string Clip(string value, int max)
    {
        var text = value.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static string Endpoint(string? value, string fallback) =>
        (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()).TrimEnd('/');
}
