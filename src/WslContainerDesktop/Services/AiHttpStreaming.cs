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

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads bounded streaming transports. Narration passes through the conservative prose
/// gate; structured/credential-rich text remains buffered until the validated terminal.
/// </summary>
internal static class AiHttpStreaming
{
    internal const int MaxBytes = 2 * 1024 * 1024;
    internal const int MaxLine = 128 * 1024;
    internal const int MaxSegment = 64 * 1024;
    private const int MaxCalls = 32;

    internal static async Task<AiToolTurn> SendAsync(
        AiHttpClient http, HttpRequestMessage message, AiChatRequest request,
        IReadOnlyList<AiToolDefinition> tools, HashSet<string> seenIds, CancellationToken ct, bool streamResponse = true)
    {
        // This deadline ends before returning to approval/tool execution. HttpClient's own
        // timeout covers headers only with ResponseHeadersRead, not subsequent body reads.
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        generation.CancelAfter(TimeSpan.FromMinutes(5));
        var token = generation.Token;
        request.Progress?.Invoke(new(AiChatProgressKind.Generating, "Generating response…"));
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Do not buffer or expose an untrusted (potentially unbounded) error body.
                throw AiProviderException.FromHttpFailure(request.Configuration.Kind, "Assistant chat",
                    response.StatusCode, message.RequestUri!.ToString(), request.Configuration.Model, string.Empty);
            }

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var narration = new AiStreamingText(request.Progress);
            var turn = !streamResponse
                ? await ReadJsonAsync(stream, request.Configuration.Kind, token).ConfigureAwait(false)
                : request.Configuration.Kind == AiProviderKind.Ollama
                ? await ReadOllamaAsync(stream, narration, token).ConfigureAwait(false)
                : await ReadOpenAiAsync(stream, narration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var currentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in turn.ToolCalls)
            {
                if (!ValidIdentifier(call.Id, 256) || !ValidIdentifier(call.Name, 128) ||
                    !tools.Any(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal)) ||
                    !currentIds.Add(call.Id) || seenIds.Contains(call.Id))
                    throw InvalidStream();
                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                    throw InvalidStream();
                RejectDuplicateProperties(arguments.RootElement);
            }
            seenIds.UnionWith(currentIds);
            if (!streamResponse) narration.Append(turn.AssistantText ?? string.Empty);
            narration.Complete();
            return turn;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or
            KeyNotFoundException or DecoderFallbackException or IOException or HttpRequestException or FormatException or OverflowException)
        {
            // Parser/transport exception messages can contain model output and secrets.
            throw InvalidStream();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("HTTP inference exceeded its generation deadline. Approval and tool execution are outside this deadline; completed actions are not rolled back.");
        }
    }

    private static InvalidDataException InvalidStream() =>
        new("The provider returned an incomplete or invalid streaming response. No pending tool calls were executed.");

    private static bool ValidIdentifier(string value, int limit) =>
        value.Length is > 0 && value.Length <= limit &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool HasValue(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidStream();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void Append(StringBuilder buffer, string? text, int limit = MaxSegment)
    {
        if (text == null || text.Length > limit - buffer.Length) throw InvalidStream();
        buffer.Append(text);
    }

    private static async IAsyncEnumerable<string> Lines(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var bytes = new byte[4096];
        var line = new List<byte>();
        var total = 0;
        var encoding = new UTF8Encoding(false, true);
        while (true)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > MaxBytes) throw InvalidStream();
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (bytes[i] == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
                    yield return encoding.GetString(line.ToArray());
                    line.Clear();
                }
                else
                {
                    if (line.Count >= MaxLine) throw InvalidStream();
                    line.Add(bytes[i]);
                }
            }
        }
        if (line.Count > 0) yield return encoding.GetString(line.ToArray());
    }

    private static async IAsyncEnumerable<string> Events(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var data = new StringBuilder();
        await foreach (var line in Lines(stream, ct).ConfigureAwait(false))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString().TrimEnd('\n');
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "data")
            {
                Append(data, value, MaxLine * 2);
                Append(data, "\n", MaxLine * 2);
            }
            else if (field == "event" && value == "error") throw InvalidStream();
        }
        // An unterminated SSE event must never turn a disconnect into a valid completion.
        if (data.Length != 0) throw InvalidStream();
    }

    private sealed class PendingCall
    {
        internal string? Id;
        internal readonly StringBuilder Name = new();
        internal readonly StringBuilder Arguments = new();
    }

    private static async Task<AiToolTurn> ReadJsonAsync(Stream stream, AiProviderKind kind, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (body.Length + count > MaxBytes) throw InvalidStream();
            body.Write(buffer, 0, count);
        }
        using var doc = JsonDocument.Parse(body.ToArray());
        var root = doc.RootElement;
        RejectDuplicateProperties(root);
        if (root.TryGetProperty("error", out _)) throw InvalidStream();
        JsonElement message;
        string? finish = null;
        if (kind == AiProviderKind.Ollama)
        {
            if (!root.GetProperty("done").GetBoolean()) throw InvalidStream();
            if (root.TryGetProperty("done_reason", out var reason) && reason.GetString() is not ("stop" or "tool_calls"))
                throw InvalidStream();
            message = root.GetProperty("message");
        }
        else
        {
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() != 1) throw InvalidStream();
            var choice = choices[0];
            if (choice.GetProperty("index").GetInt32() != 0) throw InvalidStream();
            finish = choice.GetProperty("finish_reason").GetString();
            message = choice.GetProperty("message");
        }
        if (message.GetProperty("role").GetString() != "assistant" ||
            HasValue(message, "function_call", out _)) throw InvalidStream();
        var text = message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null
            ? content.GetString() : null;
        if (text?.Length > MaxSegment) throw InvalidStream();
        var calls = new List<AiToolCall>();
        if (HasValue(message, "tool_calls", out var toolCalls))
        {
            if (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() > MaxCalls) throw InvalidStream();
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (HasValue(call, "type", out var type) && type.GetString() != "function") throw InvalidStream();
                if (call.TryGetProperty("index", out var index) && index.GetInt32() != calls.Count) throw InvalidStream();
                var function = call.GetProperty("function");
                if (function.TryGetProperty("index", out index) && index.GetInt32() != calls.Count) throw InvalidStream();
                var arguments = function.GetProperty("arguments");
                if (kind == AiProviderKind.Ollama && arguments.ValueKind != JsonValueKind.Object) throw InvalidStream();
                var argumentsJson = kind == AiProviderKind.Ollama ? arguments.GetRawText() : arguments.GetString();
                if (argumentsJson == null || argumentsJson.Length > MaxSegment) throw InvalidStream();
                calls.Add(new AiToolCall
                {
                    Id = call.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty
                        : kind == AiProviderKind.Ollama ? Guid.NewGuid().ToString("N") : string.Empty,
                    Name = function.GetProperty("name").GetString() ?? string.Empty,
                    ArgumentsJson = argumentsJson,
                });
            }
        }
        if (kind != AiProviderKind.Ollama && (calls.Count > 0 ? finish != "tool_calls" : finish != "stop"))
            throw InvalidStream();
        return new AiToolTurn { AssistantText = text, ToolCalls = calls };
    }

    private static async Task<AiToolTurn> ReadOpenAiAsync(Stream stream, AiStreamingText narration, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, PendingCall>();
        string? finish = null;
        await foreach (var data in Events(stream, ct).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                if (finish == null || (calls.Count > 0 ? finish != "tool_calls" : finish != "stop"))
                    throw InvalidStream();
                if (calls.Keys.Where((index, position) => index != position).Any()) throw InvalidStream();
                return new AiToolTurn
                {
                    AssistantText = text.ToString(),
                    ToolCalls = calls.Values.Select(c => new AiToolCall
                    {
                        Id = c.Id ?? string.Empty, Name = c.Name.ToString(), ArgumentsJson = c.Arguments.ToString(),
                    }).ToArray(),
                };
            }
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            RejectDuplicateProperties(root);
            if (root.TryGetProperty("error", out _)) throw InvalidStream();
            var choices = root.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array) throw InvalidStream();
            // Azure prompt annotations and OpenAI usage frames carry no choice.
            if (choices.GetArrayLength() == 0) continue;
            if (choices.GetArrayLength() != 1 || finish != null) throw InvalidStream();
            var choice = choices[0];
            if (choice.GetProperty("index").GetInt32() != 0) throw InvalidStream();
            var delta = choice.GetProperty("delta");
            if (delta.ValueKind != JsonValueKind.Object) throw InvalidStream();
            if (HasValue(delta, "role", out var role) && role.GetString() != "assistant") throw InvalidStream();
            if (HasValue(delta, "function_call", out _)) throw InvalidStream();
            if (delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
            {
                Append(text, content.GetString()!);
                narration.Append(content.GetString()!);
            }
            if (HasValue(delta, "tool_calls", out var fragments))
            {
                if (fragments.ValueKind != JsonValueKind.Array) throw InvalidStream();
                var frameIndexes = new HashSet<int>();
                foreach (var fragment in fragments.EnumerateArray())
                {
                    var index = fragment.GetProperty("index").GetInt32();
                    if (index < 0 || index >= MaxCalls || !frameIndexes.Add(index)) throw InvalidStream();
                    if (!calls.TryGetValue(index, out var call))
                    {
                        call = new PendingCall();
                        calls.Add(index, call);
                    }
                    if (HasValue(fragment, "id", out var id))
                    {
                        if (call.Id != null) throw InvalidStream();
                        call.Id = id.GetString() ?? throw InvalidStream();
                    }
                    if (HasValue(fragment, "type", out var type) && type.GetString() != "function") throw InvalidStream();
                    if (HasValue(fragment, "function", out var function))
                    {
                        if (function.ValueKind != JsonValueKind.Object) throw InvalidStream();
                        if (HasValue(function, "name", out var name)) Append(call.Name, name.GetString()!, 128);
                        if (HasValue(function, "arguments", out var arguments)) Append(call.Arguments, arguments.GetString()!);
                    }
                }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                finish = reason.GetString();
                if (finish is not ("stop" or "tool_calls")) throw InvalidStream();
            }
        }
        throw InvalidStream();
    }

    private static async Task<AiToolTurn> ReadOllamaAsync(Stream stream, AiStreamingText narration, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, AiToolCall>();
        await foreach (var line in Lines(stream, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            RejectDuplicateProperties(root);
            if (root.TryGetProperty("error", out _)) throw InvalidStream();
            var done = root.GetProperty("done").GetBoolean();
            if (root.TryGetProperty("message", out var message))
            {
                if (HasValue(message, "role", out var role) && role.GetString() != "assistant") throw InvalidStream();
                if (HasValue(message, "content", out var content))
                {
                    Append(text, content.GetString()!);
                    narration.Append(content.GetString()!);
                }
                if (HasValue(message, "tool_calls", out var fragments))
                {
                    if (fragments.ValueKind != JsonValueKind.Array) throw InvalidStream();
                    foreach (var fragment in fragments.EnumerateArray())
                    {
                        var function = fragment.GetProperty("function");
                        var index = fragment.TryGetProperty("index", out var topIndex) ? topIndex.GetInt32()
                            : function.TryGetProperty("index", out var functionIndex) ? functionIndex.GetInt32() : calls.Count;
                        if (fragment.TryGetProperty("index", out topIndex) &&
                            function.TryGetProperty("index", out functionIndex) &&
                            topIndex.GetInt32() != functionIndex.GetInt32()) throw InvalidStream();
                        if (index < 0 || index >= MaxCalls || calls.ContainsKey(index)) throw InvalidStream();
                        if (fragment.TryGetProperty("type", out var type) && type.GetString() != "function") throw InvalidStream();
                        var arguments = function.GetProperty("arguments");
                        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > MaxSegment)
                            throw InvalidStream();
                        calls.Add(index, new AiToolCall
                        {
                            Id = fragment.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : Guid.NewGuid().ToString("N"),
                            Name = function.GetProperty("name").GetString() ?? string.Empty,
                            ArgumentsJson = arguments.GetRawText(),
                        });
                    }
                }
            }
            else if (!done) throw InvalidStream();
            if (done)
            {
                if (root.TryGetProperty("done_reason", out var reason) &&
                    reason.GetString() is not ("stop" or "tool_calls")) throw InvalidStream();
                if (calls.Keys.Where((index, position) => index != position).Any()) throw InvalidStream();
                return new AiToolTurn { AssistantText = text.ToString(), ToolCalls = calls.Values.ToArray() };
            }
        }
        throw InvalidStream();
    }
}
