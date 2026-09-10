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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Source-linked SDK seam. Only synthetic acknowledgement callbacks; never app tools.</summary>
internal static class CopilotCapabilityProbe
{
    internal static async Task<AiCapabilitySnapshot> RunAsync(AiCapabilitySnapshot metadata,
        Func<AiChatRequest, IReadOnlyList<AiToolDefinition>, Func<AiToolCall, CancellationToken, Task<string>>,
            CancellationToken, Task<AiChatTurnResult>> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (metadata.Model == AiModelState.Missing || metadata.Authentication == AiAuthenticationState.RequiredOrRejected)
            return metadata;
        var state = metadata;
        var chat = await run(new(metadata.Configuration,
            [new() { Role = "user", Content = "Reply OK only. This is a harmless capability test." }]),
            [], (_, _) => throw new InvalidOperationException("No tools were offered."), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(chat.FinalText))
            state = state with
            {
                Chat = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
                Endpoint = AiEndpointState.Reachable, Runtime = AiRuntimeState.Ready,
                Authentication = AiAuthenticationState.Accepted, Model = AiModelState.Available,
            };
        var calls = 0;
        var active = true;
        var failed = false;
        var sync = new object();
        try
        {
            await run(new(metadata.Configuration,
                [new() { Role = "user", Content = "Call capability_ack exactly once with {\"ok\":true}, then reply OK." }]),
                [new()
                {
                    Name = "capability_ack", Description = "Harmless acknowledgement; no side effects.",
                    JsonSchemaParameters = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""",
                }],
                (call, token) =>
                {
                    ct.ThrowIfCancellationRequested();
                    token.ThrowIfCancellationRequested();
                    lock (sync)
                    {
                        if (!active || failed || calls != 0 || call.Name != "capability_ack")
                        {
                            failed = true;
                            throw new InvalidOperationException("Unexpected capability callback.");
                        }
                        // Mark failed before parsing as well as before semantic validation: an SDK
                        // may swallow either exception and attempt another callback in the same check.
                        failed = true;
                        using var doc = JsonDocument.Parse(call.ArgumentsJson);
                        if (doc.RootElement.ValueKind != JsonValueKind.Object
                            || doc.RootElement.EnumerateObject().Count() != 1
                            || !doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("Invalid capability acknowledgement.");
                        failed = false;
                        calls++;
                    }
                    return Task.FromResult("""{"ok":true}""");
                }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (sync)
                if (calls == 1 && !failed) state = state with { Tools = new(AiSupport.Supported, AiObservationSource.HarmlessProbe) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or AiProviderException or JsonException)
        {
            // A rejected or malformed tool check does not erase a completed plain-chat observation.
            // It also cannot turn arbitrary error prose into an Unsupported feature claim.
        }
        finally { lock (sync) active = false; }
        // A prompt asking for JSON is not proof of a negotiated JSON mode. Streaming remains unknown.
        return state;
    }
}
