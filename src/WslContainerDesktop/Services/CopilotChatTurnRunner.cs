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

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

internal sealed class CopilotChatTurnRunner
{
    private readonly Func<AiChatConfiguration, IReadOnlyList<AiChatMessage>, IReadOnlyList<AiToolDefinition>,
        Func<AiToolCall, CancellationToken, Task<string>>, Action<AiChatProgress>, Action<string>, CancellationToken, Task<string>> _runSession;
    private readonly TimeSpan _inferenceTimeout;

    internal CopilotChatTurnRunner(
        Func<AiChatConfiguration, IReadOnlyList<AiChatMessage>, IReadOnlyList<AiToolDefinition>,
            Func<AiToolCall, CancellationToken, Task<string>>, Action<AiChatProgress>, Action<string>, CancellationToken, Task<string>> runSession,
        TimeSpan? inferenceTimeout = null)
    {
        _runSession = runSession;
        _inferenceTimeout = inferenceTimeout ?? TimeSpan.FromMinutes(3);
    }

    internal CopilotChatTurnRunner(
        Func<AiChatConfiguration, IReadOnlyList<AiChatMessage>, IReadOnlyList<AiToolDefinition>,
            Func<AiToolCall, CancellationToken, Task<string>>, Action<AiChatProgress>, CancellationToken, Task<string>> runSession,
        TimeSpan? inferenceTimeout = null)
        : this((configuration, history, tools, invoke, progress, complete, ct) =>
            runSession(configuration, history, tools, invoke, update =>
            {
                if (update.Kind == AiChatProgressKind.TextDelta)
                    complete(update.Text);
                progress(update);
            }, ct), inferenceTimeout)
    {
    }

    internal CopilotChatTurnRunner(
        Func<AiChatConfiguration, IReadOnlyList<AiChatMessage>, IReadOnlyList<AiToolDefinition>,
            Func<AiToolCall, CancellationToken, Task<string>>, CancellationToken, Task<string>> runSession)
        : this((configuration, history, tools, invoke, _, ct) => runSession(configuration, history, tools, invoke, ct))
    {
    }

    public async Task<AiChatTurnResult> RunTurnAsync(
        AiChatRequest request,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct)
    {
        var history = AiConversationContext.Prepare(request.History, tools, request.Configuration);
        var transcript = new List<AiChatMessage>();
        var definitions = tools.Select(AiTextSanitizer.SanitizeDefinition).ToArray();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_inferenceTimeout);
        var remaining = _inferenceTimeout;
        var inferenceClock = Stopwatch.StartNew();
        var turnToken = timeout.Token;
        // No wait handle is used. A session may fail while a callback is still unwinding,
        // so do not dispose its semaphore; retain the CTS until the last callback exits.
        var callbacks = new SemaphoreSlim(1, 1);
        var lifetime = new object();
        var inFlight = 0;
        var active = true;
        var sessionFinished = false;
        Exception? callbackFailure = null;
        var callIds = history.SelectMany(message => message.ToolCalls).Select(call => call.Id)
            .ToHashSet(StringComparer.Ordinal);

        void Report(AiChatProgress progress)
        {
            try
            {
                lock (lifetime)
                {
                    if (!active || turnToken.IsCancellationRequested || callbackFailure is not null)
                        return;
                    if (progress.Kind == AiChatProgressKind.Generating)
                        request.Progress?.Invoke(new(AiChatProgressKind.Generating, "Generating response…"));
                    else if (progress.Kind == AiChatProgressKind.TextDelta && !string.IsNullOrEmpty(progress.Text))
                    {
                        var text = AiTextSanitizer.Sanitize(progress.Text);
                        request.Progress?.Invoke(new(AiChatProgressKind.TextDelta, text));
                    }
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref callbackFailure, error, null);
                timeout.Cancel();
                throw;
            }
        }

        void RecordMessage(string text)
        {
            try
            {
                lock (lifetime)
                {
                    if (!active || turnToken.IsCancellationRequested || callbackFailure is not null)
                        return;
                    if (string.IsNullOrWhiteSpace(text))
                        return;
                    transcript.Add(new AiChatMessage { Role = "assistant", Content = AiTextSanitizer.Sanitize(text) });
                    CheckBudget(history.Concat(transcript).ToArray());
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref callbackFailure, error, null);
                // Unlike tool callbacks, message completion only runs during the awaited SDK session.
                timeout.Cancel();
                throw;
            }
        }

        void CheckBudget(IReadOnlyList<AiChatMessage> retained)
        {
            _ = AiConversationContext.Prepare(retained, definitions, request.Configuration);
            // The SDK cannot prune its internal history, even if Prepare can prune our copy.
            if (AiConversationContext.Measure(retained, definitions)
                > AiConversationContext.InputByteLimit(request.Configuration))
                throw new InvalidOperationException(
                    "Assistant context budget exceeded during the Copilot session. Start a new turn to prune older context.");
        }

        async Task<string> InvokeAsync(AiToolCall call, CancellationToken callbackToken)
        {
            lock (lifetime)
            {
                if (!active)
                    throw new OperationCanceledException("The Copilot turn has already completed.");
                if (inFlight == 0)
                {
                    remaining -= inferenceClock.Elapsed;
                    inferenceClock.Reset();
                    if (remaining > TimeSpan.Zero)
                        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    else
                        timeout.Cancel();
                }
                inFlight++;
            }
            var entered = false;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(turnToken, callbackToken);
                await callbacks.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                linked.Token.ThrowIfCancellationRequested();
                if (!Volatile.Read(ref active))
                    throw new OperationCanceledException("The Copilot turn has already completed.");
                ValidateToolCall(call, definitions);
                if (!callIds.Add(call.Id))
                    throw new InvalidOperationException("Copilot returned a duplicate tool call ID.");
                var assistant = AiTextSanitizer.SanitizeMessage(new AiChatMessage { Role = "assistant", ToolCalls = [call] });
                var pending = new AiChatMessage
                {
                    Role = "tool", ToolCallId = call.Id, ToolName = call.Name,
                    Content = """{"status":"not-run","detail":"Tool execution has not started; its outcome is unknown."}""",
                };
                lock (lifetime)
                    CheckBudget(history.Concat(transcript).Concat([assistant, pending]).ToArray());
                linked.Token.ThrowIfCancellationRequested();
                if (!Volatile.Read(ref active))
                    throw new OperationCanceledException("The Copilot turn has already completed.");
                var result = await invokeToolAsync(call, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!Volatile.Read(ref active))
                    throw new OperationCanceledException("The Copilot turn has already completed.");
                var outcome = new AiChatMessage
                {
                    Role = "tool", ToolCallId = call.Id, ToolName = call.Name,
                    Content = AiTextSanitizer.Sanitize(result),
                };
                lock (lifetime)
                {
                    transcript.Add(assistant);
                    transcript.Add(outcome);
                    CheckBudget(history.Concat(transcript).ToArray());
                }
                linked.Token.ThrowIfCancellationRequested();
                if (!Volatile.Read(ref active))
                    throw new OperationCanceledException("The Copilot turn has already completed.");
                return outcome.Content!;
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref callbackFailure, ex, null);
                await timeout.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (entered)
                    callbacks.Release();
                lock (lifetime)
                {
                    inFlight--;
                    if (active && inFlight == 0 && !turnToken.IsCancellationRequested)
                    {
                        inferenceClock.Restart();
                        timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                    }
                    if (sessionFinished && inFlight == 0)
                        timeout.Dispose();
                }
            }
        }

        try
        {
            turnToken.ThrowIfCancellationRequested();
            var text = await _runSession(
                request.Configuration, history, definitions, InvokeAsync, Report, RecordMessage, turnToken).ConfigureAwait(false);
            await callbacks.WaitAsync(turnToken).ConfigureAwait(false);
            try
            {
                turnToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref callbackFailure) is { } failure)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                lock (lifetime)
                {
                    Volatile.Write(ref active, false);
                    var finalText = string.IsNullOrWhiteSpace(text) ? "Done." : AiTextSanitizer.Sanitize(text);
                    if (transcript.LastOrDefault() is not { Role: "assistant", ToolCalls.Count: 0 } last
                        || last.Content != finalText)
                    {
                        transcript.Add(new AiChatMessage { Role = "assistant", Content = finalText });
                        request.Progress?.Invoke(new(AiChatProgressKind.TextDelta, finalText));
                    }
                    return new AiChatTurnResult(finalText, transcript.ToArray());
                }
            }
            finally
            {
                callbacks.Release();
            }
        }
        catch (Exception) when (Volatile.Read(ref callbackFailure) is not null)
        {
            ExceptionDispatchInfo.Capture(Volatile.Read(ref callbackFailure)!).Throw();
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && turnToken.IsCancellationRequested)
        {
            throw new TimeoutException("Copilot inference exceeded its generation deadline. Approval and tool execution time are excluded; completed actions are not rolled back.");
        }
        finally
        {
            lock (lifetime)
            {
                Volatile.Write(ref active, false);
            }
            try
            {
                await timeout.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (lifetime)
                {
                    sessionFinished = true;
                    if (inFlight == 0)
                        timeout.Dispose();
                }
            }
        }
    }

    private static void ValidateToolCall(AiToolCall call, IReadOnlyList<AiToolDefinition> definitions)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 256
            || !definitions.Any(definition => definition.Name == call.Name))
            throw new InvalidOperationException("Copilot returned an invalid tool call identity.");
        if (call.ArgumentsJson is null || call.ArgumentsJson.Length > 128 * 1024)
            throw new InvalidOperationException("Copilot returned oversized or missing tool arguments.");
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Copilot tool arguments must be a JSON object.");
        ValidateProperties(document.RootElement);
    }

    private static void ValidateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException("Copilot tool arguments contain duplicate JSON properties.");
                ValidateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                ValidateProperties(item);
        }
    }

    internal sealed class MessageStream(Action<AiChatProgress> progress, Action<string> recordMessage)
    {
        private const int InputLimit = 128 * 1024;
        private readonly HashSet<string> _completedIds = new(StringComparer.Ordinal);
        private readonly StringBuilder _raw = new();
        private string? _messageId;
        private AiStreamingText? _text;

        public bool HasPendingMessage => _messageId is not null;

        public void Append(string messageId, string fragment)
        {
            EnsureMessage(messageId);
            if (fragment.Length > InputLimit - _raw.Length)
                throw new InvalidOperationException("Copilot assistant message exceeded its bounded streaming limit.");
            _raw.Append(fragment);
            _text!.Append(fragment);
        }

        public void Complete(string messageId, string content)
        {
            EnsureMessage(messageId);
            if (content.Length > InputLimit || !content.StartsWith(_raw.ToString(), StringComparison.Ordinal))
                throw new InvalidOperationException("Copilot completed message does not match its streamed text.");
            // A runtime can omit deltas, but only a correlated full-message event permits completion.
            var remainder = content[_raw.Length..];
            recordMessage(content);
            _text!.Append(remainder);
            _text.Complete();
            _completedIds.Add(messageId);
            _messageId = null;
            _text = null;
            _raw.Clear();
        }

        private void EnsureMessage(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 256
                || _completedIds.Contains(messageId) || _completedIds.Count >= 128)
                throw new InvalidOperationException("Copilot returned an invalid or repeated assistant message ID.");
            if (_messageId is not null && _messageId != messageId)
                throw new InvalidOperationException("Copilot interleaved incomplete assistant messages.");
            if (_messageId is null)
            {
                _messageId = messageId;
                _text = new AiStreamingText(progress);
            }
        }
    }
}
