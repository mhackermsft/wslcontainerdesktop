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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Keyless, local-only provider. Local inference grants no tool execution permission.</summary>
public sealed class FoundryLocalProvider(
    FoundryLocalHttpClient http, ISettingsService settings,
    IFoundryLocalRuntimeService runtime, IAiCapabilityService capabilities) : IAiProvider, IAiChatProvider
{
    public AiProviderKind Kind => AiProviderKind.FoundryLocal;
    public string DisplayName => Kind.DisplayName();

    public async Task<AiDiagnosis> CompleteAsync(AiPromptRequest request, CancellationToken ct)
    {
        var configuration = AiConversationContext.Capture(settings, Kind);
        var proof = RequireLiveProof(configuration, false);
        await GuardAsync(configuration, false, proof, ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object>
        {
            ["model"] = configuration.Model,
            ["temperature"] = 0.2,
            ["stream"] = false,
            ["messages"] = new[]
            {
                new { role = "system", content = AiTextSanitizer.Sanitize(request.SystemPrompt) },
                new { role = "user", content = AiTextSanitizer.Sanitize(request.UserPrompt, AiTextSanitizer.DiagnosticLimit) },
            },
        };
        if (proof.StructuredJson.Support == AiSupport.Supported)
            payload["response_format"] = new { type = "json_object" };
        using var message = new HttpRequestMessage(HttpMethod.Post,
            FoundryLocalEndpoint.BuildUri(configuration.Endpoint, "v1/chat/completions"))
        { Content = JsonContent.Create(payload) };
        var turn = await AiHttpStreaming.SendAsync(http.Transport, message,
            new AiChatRequest(configuration, []), [], new(StringComparer.Ordinal), ct, false,
            () => RequireLiveProof(configuration, false, proof)).ConfigureAwait(false);
        RequireCurrent(configuration);
        return AiProviderJson.ParseDiagnosis(turn.AssistantText ?? "");
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        var configuration = AiConversationContext.Capture(settings, Kind);
        RequireCurrent(configuration);
        var snapshot = await capabilities.GetAsync(configuration, probe: true, ct).ConfigureAwait(false);
        RequireCurrent(configuration);
        return snapshot.StatusText;
    }

    public async Task<string> RunTurnAsync(IReadOnlyList<AiChatMessage> history,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync, CancellationToken ct) =>
        (await RunTurnAsync(new AiChatRequest(AiConversationContext.Capture(settings, Kind), history),
            tools, invokeToolAsync, ct).ConfigureAwait(false)).FinalText;

    public async Task<AiChatTurnResult> RunTurnAsync(AiChatRequest request,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync, CancellationToken ct)
    {
        RequireCurrent(request.Configuration);
        var proof = RequireLiveProof(request.Configuration, tools.Count > 0);
        var result = await OpenAiProvider.RunTurnCoreAsync(http.Transport, request, tools, invokeToolAsync,
            capabilities, null, ct, token => GuardAsync(request.Configuration, tools.Count > 0, proof, token),
            () => RequireLiveProof(request.Configuration, tools.Count > 0, proof)).ConfigureAwait(false);
        RequireCurrent(request.Configuration);
        return result;
    }

    private AiCapabilitySnapshot RequireLiveProof(AiChatConfiguration configuration, bool hasTools,
        AiCapabilitySnapshot? expected = null)
    {
        RequireCurrent(configuration);
        var proof = capabilities.GetCached(configuration);
        if (proof.Configuration != configuration || !proof.CanChat || hasTools && !proof.CanUseTools
            || expected is not null && !ReferenceEquals(expected, proof))
            throw new AiProviderException(Kind, "Foundry Local inference",
                "Capability evidence is missing or changed. Test capabilities for this exact Foundry Local configuration before starting a new turn.",
                AiFailureKind.Configuration);
        return proof;
    }

    private async Task GuardAsync(AiChatConfiguration configuration, bool hasTools,
        AiCapabilitySnapshot proof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireLiveProof(configuration, hasTools, proof);
        var inventory = await runtime.ReadInventoryAsync(configuration, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // The same immutable observation must remain live across the asynchronous inventory
        // read. Equal endpoint/model strings cannot resurrect an invalidated capability proof.
        RequireLiveProof(configuration, hasTools, proof);
        var identity = FoundryLocalCapabilityObserver.ModelIdentity(inventory);
        if (!inventory.IsCached || !inventory.IsLoaded || inventory.Selected is not { ModelType: "ONNX" }
            || inventory.RuntimeIdentity != proof.RuntimeIdentity || identity != proof.ModelIdentity)
        {
            capabilities.Invalidate();
            throw new AiProviderException(Kind, "Foundry Local inference",
                "The runtime/model state changed or the exact cached model is not loaded. Use an externally prepared, already-loaded host, refresh metadata and test capabilities. In-app model load and model/EP acquisition are blocked; runtime-only package registration is separate.",
                AiFailureKind.Configuration);
        }
    }

    private void RequireCurrent(AiChatConfiguration configuration)
    {
        FoundryLocalRuntimeService.Validate(configuration);
        if (!settings.AiFeaturesEnabled || settings.AiProvider != Kind
            || configuration != AiConversationContext.Capture(settings, Kind))
            throw new OperationCanceledException("AI was disabled or the Foundry Local configuration changed; this turn cannot be sent.");
    }
}
