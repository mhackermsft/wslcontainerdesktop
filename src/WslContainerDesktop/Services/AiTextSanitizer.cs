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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Privacy boundary for assistant evidence and display/audit copies, never execution input.
/// Recognizes structured sensitive fields and common text credential shapes, not arbitrary secrets.
/// </summary>
public static partial class AiTextSanitizer
{
    public const int EvidenceLimit = 12_000;
    public const int DiagnosticLimit = 48_000;
    private const string Mask = "<redacted>";
    private const string Cut = "\n...[truncated]...\n";

    /// <summary>Redacts complete input before bounding it. Oversized JSON remains valid JSON.</summary>
    public static string Sanitize(string text, int maxChars = EvidenceLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 128);
        var safe = Redact(text);
        if (safe.Length <= maxChars)
            return safe;
        if (TryReadJson(safe, out var document))
        {
            using (document)
            {
                if (TryBoundOutcomes(document.RootElement, maxChars, out var result))
                    return result;
                // Budget against the serialized size, including escaping and the omission marker.
                var preview = RedactText("[excerpt]\n" + TruncateMiddle(safe, maxChars / 12));
                return JsonSerializer.Serialize(new { truncated = true, preview });
            }
        }
        return TruncateMiddle(safe, maxChars);
    }

    private static bool TryBoundOutcomes(JsonElement root, int limit, out string result)
    {
        result = "";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() is not ("succeeded" or "failed" or "partial" or "cancelled" or "no_targets") ||
            !root.TryGetProperty("outcomes", out var outcomes) || outcomes.ValueKind != JsonValueKind.Array)
            return false;

        var bounded = new Dictionary<string, object?>
        {
            ["status"] = status.GetString(),
            ["truncated"] = true,
        };
        // Keep the shared Compose outcome discriminator and success flag when evidence is bounded.
        // Losing them would make a partial/blocked result look like an ordinary excerpt.
        foreach (var name in new[] { "kind", "allSucceeded", "message", "retentionNotice" })
            if (root.TryGetProperty(name, out var value) && value.GetRawText().Length <= 1024)
                bounded[name] = value.Clone();
        if (root.TryGetProperty("retainedResources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            var retained = new List<JsonElement>();
            var resourceBudget = limit / 4;
            foreach (var resource in resources.EnumerateArray())
            {
                var size = resource.GetRawText().Length + 1;
                if (size > resourceBudget) continue;
                retained.Add(resource.Clone());
                resourceBudget -= size;
            }
            bounded["retainedResources"] = retained;
            bounded["omittedResources"] = resources.GetArrayLength() - retained.Count;
        }
        var kept = new List<Dictionary<string, object?>>();
        var budget = limit - JsonSerializer.Serialize(bounded).Length - 128;
        foreach (var outcome in outcomes.EnumerateArray())
        {
            if (outcome.ValueKind != JsonValueKind.Object)
                return false;
            var item = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in outcome.EnumerateObject())
                item[property.Name] = property.Name.Equals("detail", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String
                        ? TruncateMiddle(property.Value.GetString()!, Math.Max(32, Math.Min(512, limit / 10)))
                        : property.Value.Clone();
            var size = JsonSerializer.Serialize(item).Length + 1;
            if (size > budget)
                continue;
            kept.Add(item);
            budget -= size;
        }
        bounded["outcomes"] = kept;
        bounded["omittedOutcomes"] = outcomes.GetArrayLength() - kept.Count;
        result = JsonSerializer.Serialize(bounded);
        return result.Length <= limit;
    }

    /// <summary>Copies a wire/history message; IDs, tool names and execution objects are untouched.</summary>
    public static AiChatMessage SanitizeMessage(AiChatMessage message) => new()
    {
        Role = message.Role,
        ToolCallId = message.ToolCallId,
        ToolName = message.ToolName,
        Content = message.Content is null ? null : Sanitize(message.Content),
        ToolCalls = message.ToolCalls.Select(call => new AiToolCall
        {
            Id = call.Id,
            Name = call.Name,
            // Compose can hide credentials under arbitrary environment/build keys or aliases.
            // Preserve protocol identity, but never echo executable YAML to history/providers.
            ArgumentsJson = call.Name == "deploy_compose"
                ? """{"yaml":"<Compose input withheld; use the reviewed consequences and outcomes>"}"""
                : Sanitize(call.ArgumentsJson),
        }).ToArray(),
    };

    public static AiToolDefinition SanitizeDefinition(AiToolDefinition tool) => new()
    {
        Name = tool.Name,
        Description = Sanitize(tool.Description),
        JsonSchemaParameters = tool.JsonSchemaParameters,
    };

    /// <summary>Prevents SDK logging from retaining raw structured state, scopes or exceptions.</summary>
    public static ILogger WrapLogger(ILogger logger) => new SanitizedLogger(logger);

    private sealed class SanitizedLogger(ILogger inner) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            inner.BeginScope<string>(Sanitize(state.ToString() ?? string.Empty));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var detail = Sanitize(formatter(state, exception));
            if (exception is not null)
                detail = Sanitize($"{detail}\n{exception.GetType().Name}: {Sanitize(exception.Message)}");
            var safeId = new EventId(eventId.Id, eventId.Name is null ? null : Sanitize(eventId.Name));
            inner.Log(logLevel, safeId, detail, null, static (text, _) => text);
        }
    }

    /// <summary>Truncates from the end, appending an ellipsis when content was cut.</summary>
    public static string Truncate(string text, int maxChars = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "…";
    }

    /// <summary>Truncates from the middle, keeping head and tail context — used for large payloads
    /// (e.g. logs, inspect JSON) where both ends carry useful information.</summary>
    public static string TruncateMiddle(string text, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= Cut.Length)
            return Cut[..maxChars];
        var head = (maxChars - Cut.Length) / 2;
        var tail = maxChars - Cut.Length - head;
        // Do not cut a UTF-16 surrogate pair.
        if (head > 0 && char.IsHighSurrogate(text[head - 1]))
            head--;
        if (tail > 0 && char.IsLowSurrogate(text[text.Length - tail]))
            tail--;
        return text[..head] + Cut + (tail == 0 ? "" : text[^tail..]);
    }

    /// <summary>Redacts JSON recursively, YAML sensitive blocks and common free-text shapes.
    /// Unrecognized/encoded secrets still require user review. Never truncate input first.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return RedactValue(text, 0);
    }

    private static string RedactValue(string text, int depth)
    {
        if (depth > 32)
            return Mask;
        // Normalize only recognized terminal controls on evidence copies, including decoded JSON strings.
        text = TerminalControlRegex().Replace(text, string.Empty);
        if (TryReadJson(text, out var document))
        {
            using (document)
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                    WriteJson(writer, document.RootElement, depth + 1);
                var safe = Encoding.UTF8.GetString(stream.ToArray());
                // Preserve formatting of unchanged arguments/context.
                using var original = JsonDocument.Parse(safe);
                return JsonElement.DeepEquals(document.RootElement, original.RootElement) ? text : safe;
            }
        }

        // A broken structured payload cannot safely fall back to key-blind text matching.
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith("[{", StringComparison.Ordinal))
            return "[unreadable structured evidence omitted]";
        return RedactText(RedactEmbeddedJson(text, depth));
    }

    private static string RedactEmbeddedJson(string text, int depth)
    {
        var result = new StringBuilder();
        var copied = 0;
        // Failed candidates may overlap. Charge every scanned character against a linear budget,
        // and omit unexamined evidence rather than letting adversarial prefixes cause quadratic work.
        var remaining = (long)text.Length * 4;
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] is not ('{' or '['))
                continue;
            var nesting = 0;
            var quoted = false;
            var escaped = false;
            var end = start;
            for (; end < text.Length; end++)
            {
                if (remaining-- == 0)
                    return result.Append("[structured evidence scan limit; remainder omitted]").ToString();
                var ch = text[end];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') quoted = false;
                }
                else if (ch == '"') quoted = true;
                else if (ch is '{' or '[') nesting++;
                else if (ch is '}' or ']' && --nesting == 0) break;
            }
            if (end == text.Length)
                continue;
            var candidate = text[start..(end + 1)];
            if (TryReadJson(candidate, out var document))
            {
                document.Dispose();
                result.Append(text, copied, start - copied);
                result.Append(RedactValue(candidate, depth + 1));
                copied = end + 1;
                start = end;
            }
        }
        return copied == 0 ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    // ECMA-48 CSI (including SGR colors) and OSC terminated by BEL or ST.
    // Unrecognized/incomplete controls remain text; they must not disable structured scanning.
    [GeneratedRegex(@"(?:\x1B\[|\u009B)[0-?]*[ -/]*[@-~]|(?:\x1B\]|\u009D)[^\x07\x1B\u009C]*(?:\x07|\x1B\\|\u009C)", RegexOptions.NonBacktracking)]
    private static partial Regex TerminalControlRegex();

    private static bool TryReadJson(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            // Free text/YAML is expected. Structured-looking malformed input is omitted by the caller.
            document = null!;
            return false;
        }
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonElement element, int depth, bool environment = false)
    {
        if (depth > 32)
        {
            writer.WriteStringValue(Mask);
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var secretResource = element.EnumerateObject().Any(p =>
                TerminalControlRegex().Replace(p.Name, string.Empty).Equals("kind", StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.String &&
                TerminalControlRegex().Replace(p.Value.GetString()!, string.Empty).Equals("Secret", StringComparison.OrdinalIgnoreCase));
            var secretPair = element.EnumerateObject().Any(p =>
                (TerminalControlRegex().Replace(p.Name, string.Empty).Equals("name", StringComparison.OrdinalIgnoreCase) ||
                 TerminalControlRegex().Replace(p.Name, string.Empty).Equals("key", StringComparison.OrdinalIgnoreCase)) &&
                p.Value.ValueKind == JsonValueKind.String && IsSensitive(p.Value.GetString()!));
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                var field = TerminalControlRegex().Replace(property.Name, string.Empty);
                // Classify normalized evidence names, but preserve original names to avoid key collisions.
                writer.WritePropertyName(property.Name);
                if (IsSensitive(field) ||
                    (secretResource && field.Equals("data", StringComparison.OrdinalIgnoreCase)) ||
                    (secretPair && field.Equals("value", StringComparison.OrdinalIgnoreCase)))
                    writer.WriteStringValue(Mask);
                else
                    WriteJson(writer, property.Value, depth + 1,
                        field.Equals("env", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("environment", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("environmentVariables", StringComparison.OrdinalIgnoreCase));
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                WriteJson(writer, item, depth + 1, environment);
            writer.WriteEndArray();
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            var equals = text.IndexOf('=');
            writer.WriteStringValue(environment && equals > 0 && IsSensitive(text[..equals])
                ? text[..(equals + 1)] + Mask
                : RedactValue(text, depth + 1));
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private static bool IsSensitive(string name)
    {
        name = TerminalControlRegex().Replace(name, string.Empty);
        var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("passwd", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("credential", StringComparison.Ordinal) ||
            normalized.Contains("connectionstring", StringComparison.Ordinal) ||
            normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("accesskey", StringComparison.Ordinal) ||
            normalized.Contains("accountkey", StringComparison.Ordinal) ||
            normalized.Contains("privatekey", StringComparison.Ordinal) ||
            normalized.EndsWith("key", StringComparison.Ordinal) &&
                (normalized is "key" or "apikey" or "accesskey" or "accountkey" or "sharedaccesskey" or
                    "privatekey" or "clientkey" or "signingkey" or "encryptionkey" ||
                 name.Length > 3 && (!char.IsLetterOrDigit(name[^4]) ||
                    char.IsLower(name[^4]) && char.IsUpper(name[^3]))) ||
            normalized is "pwd" or "authorization" or "proxyauthorization" or "auth" or
                "cookie" or "setcookie" or "stringdata" or "dockerconfigjson" or "sig" or "signature";
    }

    private static string RedactText(string text)
    {
        var safe = PrivateKeyRegex().Replace(text, Mask);
        safe = RedactYamlBlocks(safe);
        safe = CommandSecretRegex().Replace(safe, "$1" + Mask);
        safe = AssignmentRegex().Replace(safe, match => IsSensitive(match.Groups["key"].Value)
            ? match.Groups["prefix"].Value + Mask
            : match.Value);
        safe = EnvironmentLineRegex().Replace(safe, match => IsSensitive(match.Groups["key"].Value)
            ? match.Groups["prefix"].Value + Mask
            : match.Value);
        safe = ConnectionStringRegex().Replace(safe, "$1" + Mask);
        safe = BearerRegex().Replace(safe, "$1 " + Mask);
        return UriCredentialsRegex().Replace(safe, "$1" + Mask + "@");
    }

    private static string RedactYamlBlocks(string text)
    {
        var lines = text.Split('\n');
        int? hiddenIndent = null;
        var secretResource = SecretKindRegex().IsMatch(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line.TakeWhile(char.IsWhiteSpace).Count();
            if (hiddenIndent is { } hidden && (string.IsNullOrWhiteSpace(line) || indent > hidden ||
                indent == hidden && (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.Trim() == "-")))
            {
                lines[i] = "";
                continue;
            }
            hiddenIndent = null;
            var match = YamlFieldRegex().Match(line);
            if (!match.Success)
                continue;
            var key = match.Groups["key"].Value;
            if (IsSensitive(key) || (secretResource && key == "data") ||
                key.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = match.Groups["prefix"].Value + Mask;
                hiddenIndent = indent;
            }
        }
        return string.Join('\n', lines);
    }

    [GeneratedRegex("""(?im)(?<prefix>["']?(?<key>[A-Z0-9_.-]*(?:PASSWORD|PASSWD|PWD|TOKEN|SECRET|KEY|CREDENTIAL|CONNECTION[_-]?STRING|AUTHORIZATION|AUTH|COOKIE|STRINGDATA|DOCKERCONFIGJSON|SIG|SIGNATURE)[A-Z0-9_.-]*)["']?\s*[:=]\s*)(?:"(?:\\.|[^"\\])*"|'(?:''|[^'])*'|[^\s,;}\]\r\n]+)""", RegexOptions.NonBacktracking)]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex("""(?m)^(?<prefix>\s*(?:-\s+)?(?<key>[A-Za-z_][A-Za-z0-9_.-]*)=)[^\r\n]*""")]
    private static partial Regex EnvironmentLineRegex();

    [GeneratedRegex("""(?i)(--(?:password|passwd|token|api-key|access-key|client-secret)(?:=|\s+))("(?:\\.|[^"\\])*"|'[^']*'|[^\s]+)""")]
    private static partial Regex CommandSecretRegex();

    [GeneratedRegex("""^(?<prefix>\s*(?:-\s+)?["']?(?<key>[A-Za-z_][A-Za-z0-9_.-]*)["']?\s*:\s*)(?<value>.*)$""")]
    private static partial Regex YamlFieldRegex();

    [GeneratedRegex("""(?im)^\s*kind:\s*["']?Secret["']?\s*$""")]
    private static partial Regex SecretKindRegex();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z]+ )*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)([a-z][a-z0-9+.-]*://)[^\s/@]+:[^\s/@]+@")]
    private static partial Regex UriCredentialsRegex();

    [GeneratedRegex("""(?im)\b((?:AccountKey|SharedAccessKey|Password|User ID|Uid|Pwd)\s*=\s*)("[^"]*"|'[^']*'|[^;,\r\n]+)""")]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"(?im)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();
}
