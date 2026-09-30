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

/// <summary>Values that describe ai support states or choices in WSL Container Desktop workflows.</summary>
public enum AiSupport { Unknown, Supported, Unsupported }
/// <summary>Values that describe ai observation source states or choices in WSL Container Desktop workflows.</summary>
public enum AiObservationSource { None, Metadata, HarmlessProbe }
/// <summary>Values that describe ai endpoint state states or choices in WSL Container Desktop workflows.</summary>
public enum AiEndpointState { Unknown, Reachable, Unreachable, InvalidConfiguration }
/// <summary>Values that describe ai authentication state states or choices in WSL Container Desktop workflows.</summary>
public enum AiAuthenticationState { Unknown, Accepted, RequiredOrRejected }
/// <summary>Values that describe ai runtime state states or choices in WSL Container Desktop workflows.</summary>
public enum AiRuntimeState { Unknown, Ready, Unavailable }
/// <summary>Values that describe ai model state states or choices in WSL Container Desktop workflows.</summary>
public enum AiModelState { Unknown, Available, Missing }
/// <summary>Values that describe ai download state states or choices in WSL Container Desktop workflows.</summary>
public enum AiDownloadState { Unknown, NotDownloaded, Downloaded, Downloading }
/// <summary>Values that describe ai load state states or choices in WSL Container Desktop workflows.</summary>
public enum AiLoadState { Unknown, Unloaded, Loading, Loaded }

/// <summary>Immutable or init-only data model that carries ai capability observation information between services and view models.</summary>
public sealed record AiCapabilityObservation(
    AiSupport Support = AiSupport.Unknown,
    AiObservationSource Source = AiObservationSource.None);

/// <summary>
/// Tokens and application-accounted UTF-8 JSON bytes are different units. An explicitly
/// byte-accounted limit is measured and always wins. A reported context window is only ever used
/// to raise the default ceiling through a deliberately pessimistic conversion, never treated as a
/// byte count in its own right.
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
    /// <summary>Gets or sets the chat.</summary>
    public AiCapabilityObservation Chat { get; init; } = new();
    /// <summary>Gets or sets the tools.</summary>
    public AiCapabilityObservation Tools { get; init; } = new();
    /// <summary>Gets or sets the structured json.</summary>
    public AiCapabilityObservation StructuredJson { get; init; } = new();
    /// <summary>Gets or sets the streaming.</summary>
    public AiCapabilityObservation Streaming { get; init; } = new();
    /// <summary>Gets or sets the context.</summary>
    public AiContextObservation Context { get; init; } = new();
    /// <summary>Gets or sets the endpoint.</summary>
    public AiEndpointState Endpoint { get; init; }
    /// <summary>Gets or sets the authentication.</summary>
    public AiAuthenticationState Authentication { get; init; }
    /// <summary>Gets or sets the runtime.</summary>
    public AiRuntimeState Runtime { get; init; }
    /// <summary>Gets or sets the model.</summary>
    public AiModelState Model { get; init; }
    /// <summary>Gets or sets the download.</summary>
    public AiDownloadState Download { get; init; }
    /// <summary>Gets or sets the load.</summary>
    public AiLoadState Load { get; init; }
    /// <summary>Gets or sets the runtime identity.</summary>
    public string RuntimeIdentity { get; init; } = "";
    /// <summary>Gets or sets the model identity.</summary>
    public string ModelIdentity { get; init; } = "";
    /// <summary>Gets or sets the observed at.</summary>
    public DateTimeOffset ObservedAt { get; init; }
    /// <summary>Gets or sets a value indicating whether the probe timed out flag is set.</summary>
    public bool ProbeTimedOut { get; init; }
    /// <summary>Gets or sets the credential identity.</summary>
    internal string CredentialIdentity { get; init; } = "";

    /// <summary>Gets a value indicating whether this value can chat.</summary>
    public bool CanChat => Chat.Support == AiSupport.Supported
        && Endpoint == AiEndpointState.Reachable
        && Authentication != AiAuthenticationState.RequiredOrRejected
        && Runtime != AiRuntimeState.Unavailable && Model != AiModelState.Missing
        && Load != AiLoadState.Loading && Download != AiDownloadState.Downloading && !ProbeTimedOut
        && (Configuration.Kind != AiProviderKind.FoundryLocal
            || Runtime == AiRuntimeState.Ready && Model == AiModelState.Available
            && Download == AiDownloadState.Downloaded && Load == AiLoadState.Loaded);
    /// <summary>Gets a value indicating whether this value can use tools.</summary>
    public bool CanUseTools => CanChat && Tools.Support == AiSupport.Supported;

    /// <summary>
    /// The first unmet condition preventing assistant actions, as one sentence; empty when tools
    /// are usable. Ordered like <see cref="CanChat"/> so the reason shown is the one to fix first.
    /// Fixed app-owned vocabulary only: no endpoint, model name, body or error detail.
    /// </summary>
    public string Blocker =>
        CanUseTools ? ""
        : Endpoint == AiEndpointState.InvalidConfiguration ? "The configured endpoint is not a usable URL."
        : Endpoint == AiEndpointState.Unreachable ? "The configured endpoint could not be reached."
        : Endpoint == AiEndpointState.Unknown ? "The provider has not been checked recently."
        : Authentication == AiAuthenticationState.RequiredOrRejected ? "The provider required or rejected credentials."
        : Runtime == AiRuntimeState.Unavailable ? "The configured runtime is not running."
        : Model == AiModelState.Missing ? "The configured model is not available to the runtime."
        : Download == AiDownloadState.Downloading ? "The model is still downloading."
        : Load == AiLoadState.Loading ? "The model is still loading."
        : ProbeTimedOut ? "The capability check timed out before the runtime answered."
        : Configuration.Kind == AiProviderKind.FoundryLocal
            && (Runtime != AiRuntimeState.Ready || Model != AiModelState.Available
                || Download != AiDownloadState.Downloaded || Load != AiLoadState.Loaded)
            ? "Foundry Local has not shown that the pinned model is prepared and loaded."
        : Chat.Support == AiSupport.Unsupported ? "This provider did not show chat support."
        : Chat.Support == AiSupport.Unknown ? "Chat support has not been observed yet."
        : Tools.Support == AiSupport.Unsupported
            ? "This model did not show support for tool calling, so the assistant cannot take actions."
        : "Tool support has not been observed yet, so actions stay unavailable.";

    // Only fixed app-owned vocabulary enters status. No endpoint, model name, body or error detail.
    /// <summary>Gets the status text.</summary>
    public string StatusText =>
        $"Endpoint: {Endpoint}; authentication: {Authentication}; runtime: {Runtime}; " +
        $"model: {Model}; download: {Download}; loading: {Load}.\n" +
        $"Chat: {Chat.Support}; tools: {Tools.Support}; structured JSON: {StructuredJson.Support}; " +
        $"streaming: {Streaming.Support}; context: {Context.Support}.\n" +
        $"Context tokens: {Context.ContextTokens?.ToString() ?? "Unknown"}; observed accounted input bytes: " +
        $"{Context.InputByteCeiling?.ToString() ?? "Unknown"} (default application ceiling: 32768 bytes; tokens are not bytes).\n" + NextStep;

    /// <summary>Gets the next step.</summary>
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
            ? "Use Foundry Local initial-model setup to prepare and verify the pinned CPU model, then test capabilities. Runtime-only package registration does not make a model ready."
            : "Choose an installed/served model. Downloading a model is a separate, explicit operation."
        : Download == AiDownloadState.Downloading
        ? "Wait for the separately started model download to finish, then test capabilities."
        : Load == AiLoadState.Loading || ProbeTimedOut
        ? "The model may still be warming up. Wait for the runtime to finish loading, then test again after the one-minute cooldown. No automatic generation retry or download was started."
        : Endpoint == AiEndpointState.Unreachable
        ? "Check the configured endpoint and start your runtime, then test again."
        : Runtime == AiRuntimeState.Unavailable
        ? "Start or repair the configured runtime. For Copilot, check CLI installation, sign-in and entitlement, then test again."
        : Configuration.Kind == AiProviderKind.FoundryLocal && Load is AiLoadState.Unloaded or AiLoadState.Unknown
        ? "Use Foundry Local initial-model setup (or prepare/reload) to establish current load evidence, then test capabilities. Catalog access requires network; starting the vendor runtime may install Microsoft-selected execution providers."
        : !CanUseTools
        ? "Use Test capabilities in Settings to verify support. Unknown or unsupported tools cannot enable assistant actions; chat-only diagnosis remains separate. Generation checks are cached for up to 10 minutes (1 minute when chat is not ready)."
        : "Tool support observed. Every action still passes through the app's approval gate. Generation checks are cached for up to 10 minutes.";
}
