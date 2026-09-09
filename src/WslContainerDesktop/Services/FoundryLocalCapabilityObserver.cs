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

public sealed class FoundryLocalCapabilityObserver(
    IFoundryLocalRuntimeService runtime, FoundryLocalHttpClient http) : IAiCapabilityObserver
{
    public AiProviderKind Kind => AiProviderKind.FoundryLocal;

    public async Task<AiCapabilitySnapshot> ReadMetadataAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = new AiCapabilitySnapshot(configuration);
        try
        {
            FoundryLocalRuntimeService.Validate(configuration, requireModel: false);
            var inventory = await runtime.ReadInventoryAsync(configuration, ct).ConfigureAwait(false);
            return state with
            {
                Endpoint = AiEndpointState.Reachable,
                Authentication = AiAuthenticationState.Accepted,
                Runtime = AiRuntimeState.Ready,
                RuntimeIdentity = inventory.RuntimeIdentity,
                ModelIdentity = ModelIdentity(inventory),
                Model = inventory.Selected is { ModelType: "ONNX" } && inventory.IsCached
                    ? AiModelState.Available : AiModelState.Missing,
                Download = inventory.IsCached ? AiDownloadState.Downloaded : AiDownloadState.NotDownloaded,
                Load = inventory.IsLoaded ? AiLoadState.Loaded : AiLoadState.Unloaded,
                // An advertisement alone is not verified assistant protocol support.
                Tools = inventory.Selected?.SupportsToolCalling == false
                    ? new(AiSupport.Unsupported, AiObservationSource.Metadata) : new(),
            };
        }
        // Expected configuration/transport failures become fixed, user-visible readiness states;
        // retaining raw exception details here would leak provider evidence into the capability cache.
        catch (ArgumentException) { return state with { Endpoint = AiEndpointState.InvalidConfiguration }; }
        catch (HttpRequestException) { return state with { Endpoint = AiEndpointState.Unreachable, Runtime = AiRuntimeState.Unavailable }; }
        catch (AiProviderException ex)
        {
            return state with
            {
                Endpoint = AiEndpointState.Reachable,
                Authentication = ex.StatusCode is 401 or 403 ? AiAuthenticationState.RequiredOrRejected : AiAuthenticationState.Unknown,
                Runtime = AiRuntimeState.Unavailable,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return state; // Unknown schema proves nothing. Never retain body/error text.
        }
    }

    internal static string ModelIdentity(FoundryLocalInventory inventory) =>
        AiCapabilityService.HashIdentity(JsonSerializer.Serialize(new
        {
            inventory.Selected, inventory.IsCached, inventory.IsLoaded,
        }));

    public async Task<AiCapabilitySnapshot> ProbeAsync(AiCapabilitySnapshot metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Probes cannot become implicit model load/download operations.
        if (metadata.Configuration.Kind != Kind || metadata.Endpoint != AiEndpointState.Reachable
            || metadata.Model != AiModelState.Available || metadata.Load != AiLoadState.Loaded)
            return metadata;
        var state = await new HttpAiCapabilityObserver(Kind, http.Transport, NoCredentials.Instance)
            .ProbeAsync(metadata, ct).ConfigureAwait(false);
        if (!state.CanChat) return state;
        if (state.Tools.Support == AiSupport.Supported)
        {
            // Verify result-envelope acceptance too. This is synthetic protocol evidence, never
            // an app tool invocation, permission grant or execution of provider-supplied code.
            state = state with { Tools = new() };
            try
            {
                AiChatMessage[] history =
                [
                    new() { Role = "user", Content = "Call capability_ack, then reply OK after receiving its result." },
                    new() { Role = "assistant", ToolCalls = [new()
                    {
                        Id = "capability_roundtrip", Name = "capability_ack", ArgumentsJson = "{\"ok\":true}",
                    }] },
                    new() { Role = "tool", ToolCallId = "capability_roundtrip", ToolName = "capability_ack", Content = "{\"ok\":true}" },
                ];
                using var resultMessage = new HttpRequestMessage(HttpMethod.Post,
                    FoundryLocalEndpoint.BuildUri(state.Configuration.Endpoint, "v1/chat/completions"))
                {
                    Content = JsonContent.Create(new
                    {
                        model = state.Configuration.Model, stream = false, max_tokens = 64,
                        messages = history.Select(OpenAiProvider.ToOpenAiMessage).ToArray(),
                    }),
                };
                var result = await AiHttpStreaming.SendAsync(http.Transport, resultMessage,
                    new AiChatRequest(state.Configuration, history), [], new(StringComparer.Ordinal), ct, false).ConfigureAwait(false);
                if (result.AssistantText?.Trim() == "OK")
                    state = state with { Tools = new(AiSupport.Supported, AiObservationSource.HarmlessProbe) };
            }
            catch (Exception ex) when (ex is InvalidDataException or AiProviderException or HttpRequestException)
            {
                // Tool-call output alone cannot prove acceptance of a complete tool roundtrip.
            }
        }
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                FoundryLocalEndpoint.BuildUri(state.Configuration.Endpoint, "v1/chat/completions"));
            message.Content = JsonContent.Create(new
            {
                model = state.Configuration.Model, stream = true, max_tokens = 64,
                messages = new[] { new { role = "user", content = "Reply OK only." } },
            });
            var turn = await AiHttpStreaming.SendAsync(http.Transport, message,
                new AiChatRequest(state.Configuration, []), [], new(StringComparer.Ordinal), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(turn.AssistantText))
                state = state with { Streaming = new(AiSupport.Supported, AiObservationSource.HarmlessProbe) };
        }
        catch (Exception ex) when (ex is InvalidDataException or AiProviderException or HttpRequestException)
        {
            // No retry or non-stream fallback inside a probe. Lack of proof stays unknown.
        }
        ct.ThrowIfCancellationRequested();
        return state;
    }

    private sealed class NoCredentials : IAiCredentialStore
    {
        internal static readonly NoCredentials Instance = new();
        public bool TryReadSecret(AiProviderKind provider, out string secret) { secret = ""; return false; }
        public void WriteSecret(AiProviderKind provider, string secret) => throw new NotSupportedException();
        public void DeleteSecret(AiProviderKind provider) => throw new NotSupportedException();
    }
}
