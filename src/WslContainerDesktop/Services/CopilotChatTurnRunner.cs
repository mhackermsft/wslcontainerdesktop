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

using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

internal sealed class CopilotChatTurnRunner(
    Func<AiChatConfiguration, IReadOnlyList<AiChatMessage>, IReadOnlyList<AiToolDefinition>,
        Func<AiToolCall, CancellationToken, Task<string>>, CancellationToken, Task<string>> runSession)
{
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
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var turnToken = timeout.Token;
        // No wait handle is used. A session may fail while a callback is still unwinding,
        // so do not dispose its semaphore; retain the CTS until the last callback exits.
        var callbacks = new SemaphoreSlim(1, 1);
        var lifetime = new object();
        var inFlight = 0;
        var active = true;
        var sessionFinished = false;
        Exception? callbackFailure = null;

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
                var assistant = AiTextSanitizer.SanitizeMessage(new AiChatMessage { Role = "assistant", ToolCalls = [call] });
                var pending = new AiChatMessage
                {
                    Role = "tool", ToolCallId = call.Id, ToolName = call.Name,
                    Content = """{"status":"not-run","detail":"Tool execution has not started; its outcome is unknown."}""",
                };
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
                transcript.Add(assistant);
                transcript.Add(outcome);
                CheckBudget(history.Concat(transcript).ToArray());
                linked.Token.ThrowIfCancellationRequested();
                if (!Volatile.Read(ref active))
                    throw new OperationCanceledException("The Copilot turn has already completed.");
                return outcome.Content!;
            }
            catch (Exception ex) when (IsSupportedFailure(ex))
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
                    if (sessionFinished && inFlight == 0)
                        timeout.Dispose();
                }
            }
        }

        try
        {
            turnToken.ThrowIfCancellationRequested();
            var text = await runSession(
                request.Configuration, history, definitions, InvokeAsync, turnToken).ConfigureAwait(false);
            await callbacks.WaitAsync(turnToken).ConfigureAwait(false);
            try
            {
                turnToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref callbackFailure) is { } failure)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                Volatile.Write(ref active, false);
                var finalText = string.IsNullOrWhiteSpace(text) ? "Done." : AiTextSanitizer.Sanitize(text);
                transcript.Add(new AiChatMessage { Role = "assistant", Content = finalText });
                return new AiChatTurnResult(finalText, transcript.ToArray());
            }
            finally
            {
                callbacks.Release();
            }
        }
        catch (Exception ex) when (IsSupportedFailure(ex) && Volatile.Read(ref callbackFailure) is not null)
        {
            ExceptionDispatchInfo.Capture(Volatile.Read(ref callbackFailure)!).Throw();
            throw;
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

    private static bool IsSupportedFailure(Exception error) => error is
        InvalidOperationException or IOException or Win32Exception or JsonException
        or TimeoutException or ArgumentException or OperationCanceledException;
}
