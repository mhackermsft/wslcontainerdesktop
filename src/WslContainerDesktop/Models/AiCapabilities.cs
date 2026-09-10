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

namespace WslContainerDesktop.Models;

public enum AiSupport { Unknown, Supported, Unsupported }
public enum AiObservationSource { None, Metadata, HarmlessProbe }
public enum AiEndpointState { Unknown, Reachable, Unreachable, InvalidConfiguration }
public enum AiAuthenticationState { Unknown, Accepted, RequiredOrRejected }
public enum AiRuntimeState { Unknown, Ready, Unavailable }
public enum AiModelState { Unknown, Available, Missing }
public enum AiDownloadState { Unknown, NotDownloaded, Downloaded, Downloading }
public enum AiLoadState { Unknown, Unloaded, Loading, Loaded }

public sealed record AiCapabilityObservation(
    AiSupport Support = AiSupport.Unknown,
    AiObservationSource Source = AiObservationSource.None);

/// <summary>
/// Tokens and application-accounted UTF-8 JSON bytes are different units. Only an explicitly
/// byte-accounted limit can reduce the application's byte ceiling; never convert tokens to bytes.
/// </summary>
public sealed record AiContextObservation(
    AiSupport Support = AiSupport.Unknown,
    long? ContextTokens = null,
    int? InputByteCeiling = null,
    AiObservationSource Source = AiObservationSource.None);

/// <summary>
/// Memory-only observations, not provider guarantees. Identity strings are opaque hashes, never
/// raw provider evidence. Configuration is an execution destination, not display/log content.
/// Runtime integrations (#90/#92) must invalidate before replacement/download/load changes.
/// </summary>
public sealed record AiCapabilitySnapshot(AiChatConfiguration Configuration)
{
    public AiCapabilityObservation Chat { get; init; } = new();
    public AiCapabilityObservation Tools { get; init; } = new();
    public AiCapabilityObservation StructuredJson { get; init; } = new();
    public AiCapabilityObservation Streaming { get; init; } = new();
    public AiContextObservation Context { get; init; } = new();
    public AiEndpointState Endpoint { get; init; }
    public AiAuthenticationState Authentication { get; init; }
    public AiRuntimeState Runtime { get; init; }
    public AiModelState Model { get; init; }
    public AiDownloadState Download { get; init; }
    public AiLoadState Load { get; init; }
    public string RuntimeIdentity { get; init; } = "";
    public string ModelIdentity { get; init; } = "";
    public DateTimeOffset ObservedAt { get; init; }
    public bool ProbeTimedOut { get; init; }
    internal string CredentialIdentity { get; init; } = "";

    public bool CanChat => Chat.Support == AiSupport.Supported
        && Endpoint == AiEndpointState.Reachable
        && Authentication != AiAuthenticationState.RequiredOrRejected
        && Runtime != AiRuntimeState.Unavailable && Model != AiModelState.Missing
        && Load != AiLoadState.Loading && Download != AiDownloadState.Downloading && !ProbeTimedOut
        && (Configuration.Kind != AiProviderKind.FoundryLocal
            || Runtime == AiRuntimeState.Ready && Model == AiModelState.Available
            && Download == AiDownloadState.Downloaded && Load == AiLoadState.Loaded);
    public bool CanUseTools => CanChat && Tools.Support == AiSupport.Supported;

    // Only fixed app-owned vocabulary enters status. No endpoint, model name, body or error detail.
    public string StatusText =>
        $"Endpoint: {Endpoint}; authentication: {Authentication}; runtime: {Runtime}; " +
        $"model: {Model}; download: {Download}; loading: {Load}.\n" +
        $"Chat: {Chat.Support}; tools: {Tools.Support}; structured JSON: {StructuredJson.Support}; " +
        $"streaming: {Streaming.Support}; context: {Context.Support}.\n" +
        $"Context tokens: {Context.ContextTokens?.ToString() ?? "Unknown"}; observed accounted input bytes: " +
        $"{Context.InputByteCeiling?.ToString() ?? "Unknown"} (default application ceiling: 32768 bytes; tokens are not bytes).\n" + NextStep;

    public string NextStep => Endpoint == AiEndpointState.InvalidConfiguration
        ? Configuration.Kind == AiProviderKind.FoundryLocal
            ? "Enter the actual Foundry Local loopback HTTP(S) URL with an explicit port and actual model ID. Remote hosts, credentials, queries and fragments are not supported; no defaults are inferred."
            : "Fix the endpoint in Settings: enter a valid absolute HTTP(S) URL without embedded credentials. Save any API key separately, then test capabilities."
        : Authentication == AiAuthenticationState.RequiredOrRejected
        ? Configuration.Kind == AiProviderKind.FoundryLocal
            ? "The local endpoint rejected keyless access. Check your Foundry Local runtime configuration; this provider never sends credentials or falls back to cloud."
            : "Save valid credentials or sign in, then test again."
        : Model == AiModelState.Missing
        ? Configuration.Kind == AiProviderKind.FoundryLocal
            ? "Choose the exact cached ONNX catalog model ID on an externally prepared, already-loaded Foundry Local host. In-app model load and model/EP downloads are blocked; runtime-only package registration does not make a model ready."
            : "Choose an installed/served model. Downloading a model is a separate, explicit operation."
        : Download == AiDownloadState.Downloading
        ? "Wait for the separately started model download to finish, then test capabilities."
        : Load == AiLoadState.Loading || ProbeTimedOut
        ? "The model may still be warming up. Wait for the runtime to finish loading, then test again after the one-minute cooldown. No automatic generation retry or download was started."
        : Endpoint == AiEndpointState.Unreachable
        ? "Check the configured endpoint and start your runtime, then test again."
        : Runtime == AiRuntimeState.Unavailable
        ? "Start or repair the configured runtime. For Copilot, check CLI installation, sign-in and entitlement, then test again."
        : Configuration.Kind == AiProviderKind.FoundryLocal && Load == AiLoadState.Unloaded
        ? "Use an externally prepared, already-loaded Foundry Local host, then refresh metadata and test capabilities. In-app model load and model/EP acquisition are blocked because authoritative EP preparation cannot be verified; runtime-only package registration is separate."
        : !CanUseTools
        ? "Use Test capabilities in Settings to verify support. Unknown or unsupported tools cannot enable assistant actions; chat-only diagnosis remains separate. Generation checks are cached for up to 10 minutes (1 minute when chat is not ready)."
        : "Tool support observed. Every action still passes through the app's approval gate. Generation checks are cached for up to 10 minutes.";
}
