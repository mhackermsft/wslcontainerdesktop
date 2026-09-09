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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Metadata plus at most three synthetic, non-streaming generations. No app tool callbacks.</summary>
public sealed class HttpAiCapabilityObserver(
    AiProviderKind kind, AiHttpClient http, IAiCredentialStore credentials) : IAiCapabilityObserver
{
    public AiProviderKind Kind => kind;
    private const string ProbeTool = "capability_ack";
    private static readonly AiCapabilityObservation Proven = new(AiSupport.Supported, AiObservationSource.HarmlessProbe);
    private static readonly AiCapabilityObservation Rejected = new(AiSupport.Unsupported, AiObservationSource.HarmlessProbe);

    public async Task<AiCapabilitySnapshot> ReadMetadataAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidEndpoint(configuration))
            return new(configuration) { Endpoint = AiEndpointState.InvalidConfiguration };
        var key = CaptureKey();
        var state = new AiCapabilitySnapshot(configuration) { CredentialIdentity = AiCapabilityService.HashIdentity(key ?? "") };
        if (string.IsNullOrWhiteSpace(configuration.Model)) return state with { Model = AiModelState.Missing };
        try
        {
            if (Kind == AiProviderKind.AzureOpenAi)
            {
                // Deployment-list APIs need different credentials/permissions; don't infer from a route.
                return string.IsNullOrWhiteSpace(key)
                    ? state with { Authentication = AiAuthenticationState.RequiredOrRejected } : state;
            }

            var inventory = await SendAsync(configuration, HttpMethod.Get,
                Kind == AiProviderKind.Ollama ? "api/tags" : "models", null, key, ct).ConfigureAwait(false);
            state = ApplyStatus(state, inventory);
            if (!inventory.IsSuccess) return state;
            using var doc = JsonDocument.Parse(inventory.Body);
            var property = Kind == AiProviderKind.Ollama ? "models" : "data";
            if (!doc.RootElement.TryGetProperty(property, out var models) || models.ValueKind != JsonValueKind.Array)
                return state;
            var matches = models.EnumerateArray().Where(m =>
                String(m, Kind == AiProviderKind.Ollama ? "name" : "id") == configuration.Model
                || (Kind == AiProviderKind.Ollama && String(m, "name") == configuration.Model + ":latest")).ToArray();
            if (matches.Length != 1)
                return Kind == AiProviderKind.Ollama && matches.Length == 0
                    ? state with { Model = AiModelState.Missing, Download = AiDownloadState.NotDownloaded }
                    : state; // OpenAI inventories may be partial, not deployment readiness guarantees.
            state = state with { Model = AiModelState.Available };
            if (Kind != AiProviderKind.Ollama) return state;
            state = state with
            {
                Download = AiDownloadState.Downloaded,
                ModelIdentity = AiCapabilityService.HashIdentity(String(matches[0], "digest")),
            };

            var version = await SendAsync(configuration, HttpMethod.Get, "api/version", null, key, ct).ConfigureAwait(false);
            if (version.IsSuccess)
            {
                using var info = JsonDocument.Parse(version.Body);
                state = state with { RuntimeIdentity = AiCapabilityService.HashIdentity(String(info.RootElement, "version")) };
            }
            var show = await SendAsync(configuration, HttpMethod.Post, "api/show",
                new { model = configuration.Model }, key, ct).ConfigureAwait(false);
            if (show.IsSuccess)
            {
                using var info = JsonDocument.Parse(show.Body);
                if (info.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                    && caps.EnumerateArray().All(c => c.ValueKind == JsonValueKind.String))
                {
                    var names = caps.EnumerateArray().Select(c => c.GetString()).ToHashSet(StringComparer.Ordinal);
                    state = state with
                    {
                        Chat = new(names.Contains("completion") ? AiSupport.Supported : AiSupport.Unsupported, AiObservationSource.Metadata),
                        Tools = new(names.Contains("tools") ? AiSupport.Supported : AiSupport.Unsupported, AiObservationSource.Metadata),
                    };
                }
                if (info.RootElement.TryGetProperty("model_info", out var modelInfo) && modelInfo.ValueKind == JsonValueKind.Object)
                {
                    var limits = modelInfo.EnumerateObject()
                        .Where(p => p.Name.EndsWith(".context_length", StringComparison.Ordinal))
                        .Select(p => p.Value.TryGetInt64(out var n) ? n : 0).Where(n => n > 0).ToArray();
                    if (limits.Length == 1)
                        state = state with { Context = new(AiSupport.Supported, limits[0], Source: AiObservationSource.Metadata) };
                }
            }
            var running = await SendAsync(configuration, HttpMethod.Get, "api/ps", null, key, ct).ConfigureAwait(false);
            if (running.IsSuccess)
            {
                using var info = JsonDocument.Parse(running.Body);
                if (info.RootElement.TryGetProperty("models", out var loaded) && loaded.ValueKind == JsonValueKind.Array)
                    state = state with
                    {
                        Load = loaded.EnumerateArray().Any(m => String(m, "digest") == String(matches[0], "digest")
                            && !string.IsNullOrEmpty(String(m, "digest"))) ? AiLoadState.Loaded : AiLoadState.Unloaded,
                    };
            }
            return state;
        }
        catch (HttpRequestException) { return state with { Endpoint = AiEndpointState.Unreachable }; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Unrecognized metadata is unknown. Raw bodies/exception text are deliberately not retained.
            return state;
        }
    }

    public async Task<AiCapabilitySnapshot> ProbeAsync(AiCapabilitySnapshot metadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = metadata;
        if (!IsValidEndpoint(state.Configuration))
            return state with { Endpoint = AiEndpointState.InvalidConfiguration };
        var key = CaptureKey();
        if (metadata.CredentialIdentity.Length > 0 && metadata.CredentialIdentity != AiCapabilityService.HashIdentity(key ?? ""))
            throw new OperationCanceledException("AI credentials changed.");
        if (state.Model == AiModelState.Missing || state.Authentication == AiAuthenticationState.RequiredOrRejected)
            return state;
        try
        {
            foreach (var feature in new[] { "chat", "tools", "json" })
            {
                ct.ThrowIfCancellationRequested();
                if (feature == "tools" && state.Tools.Support != AiSupport.Unknown) continue;
                var reply = await SendAsync(state.Configuration, HttpMethod.Post,
                    Kind == AiProviderKind.Ollama ? "api/chat" : "chat/completions",
                    ProbeBody(state.Configuration, feature), key, ct).ConfigureAwait(false);
                state = ApplyStatus(state, reply);
                if (!reply.IsSuccess)
                {
                    if (ExplicitlyUnsupported(reply, feature, state.Configuration.Model))
                    {
                        state = feature switch
                        {
                            "tools" => state with { Tools = Rejected },
                            "json" => state with { StructuredJson = Rejected },
                            _ => state,
                        };
                        continue;
                    }
                    return state;
                }
                using var doc = JsonDocument.Parse(reply.Body);
                var message = Kind == AiProviderKind.Ollama
                    ? doc.RootElement.GetProperty("message")
                    : doc.RootElement.GetProperty("choices")[0].GetProperty("message");
                var content = String(message, "content");
                if (feature == "chat" && !string.IsNullOrWhiteSpace(content))
                    state = state with { Chat = Proven, Model = AiModelState.Available, Load = AiLoadState.Loaded };
                if (feature == "tools" && ValidAck(message))
                    state = state with { Tools = Proven };
                if (feature == "json" && ValidJsonAck(content))
                    state = state with { StructuredJson = Proven };
            }
        }
        catch (HttpRequestException) { state = state with { Endpoint = AiEndpointState.Unreachable }; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
        {
            // A malformed/ignored response proves neither support nor lack of support.
        }
        ct.ThrowIfCancellationRequested();
        return state;
    }

    private object ProbeBody(AiChatConfiguration configuration, string feature)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = configuration.Model,
            ["stream"] = false,
            ["messages"] = new[]
            {
                new { role = "user", content = feature == "tools"
                    ? "Call capability_ack exactly once with {\"ok\":true}. It is a harmless capability test."
                    : feature == "json" ? "Return the JSON object {\"ok\":true} only." : "Reply OK only." },
            },
        };
        if (Kind == AiProviderKind.Ollama)
            body["options"] = new { num_predict = 64, temperature = 0 };
        else body["max_tokens"] = 64;
        if (feature == "json")
        {
            if (Kind == AiProviderKind.Ollama) body["format"] = "json";
            else body["response_format"] = new { type = "json_object" };
        }
        if (feature == "tools")
        {
            body["tools"] = new[]
            {
                new { type = "function", function = new
                {
                    name = ProbeTool, description = "Harmless acknowledgement. No side effects.",
                    parameters = new { type = "object", properties = new { ok = new { type = "boolean" } },
                        required = new[] { "ok" }, additionalProperties = false },
                } },
            };
            if (Kind != AiProviderKind.Ollama)
                body["tool_choice"] = new { type = "function", function = new { name = ProbeTool } };
        }
        return body;
    }

    private static bool ValidAck(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() != 1)
            return false;
        var function = calls[0].GetProperty("function");
        if (String(function, "name") != ProbeTool) return false;
        var args = function.GetProperty("arguments");
        return ValidJsonAck(args.ValueKind == JsonValueKind.String ? args.GetString()! : args.GetRawText());
    }

    private static bool ValidJsonAck(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Count() == 1
                && doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static AiCapabilitySnapshot ApplyStatus(AiCapabilitySnapshot state, Reply reply)
    {
        state = state with { Endpoint = AiEndpointState.Reachable };
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return state with { Authentication = AiAuthenticationState.RequiredOrRejected };
        if (reply.IsSuccess)
            return state with { Authentication = AiAuthenticationState.Accepted, Runtime = AiRuntimeState.Ready };
        var code = ErrorCode(reply);
        if (code is "model_not_found" or "deployment_not_found" or "DeploymentNotFound")
            return state with { Model = AiModelState.Missing };
        if (code is "model_loading" or "model_not_ready")
            return state with { Load = AiLoadState.Loading };
        if (code == "model_downloading")
            return state with { Download = AiDownloadState.Downloading };
        // A generic 503 is not proof of loading; a generic 404 is not proof of a missing model.
        return (int)reply.Status >= 500 ? state with { Runtime = AiRuntimeState.Unavailable } : state;
    }

    private static string ErrorCode(Reply reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            return doc.RootElement.TryGetProperty("error", out var error) ? String(error, "code") : "";
        }
        catch (JsonException) { return ""; }
    }

    private bool ExplicitlyUnsupported(Reply reply, string feature, string model)
    {
        if (reply.Status != HttpStatusCode.BadRequest || feature == "chat") return false;
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            var error = doc.RootElement.GetProperty("error");
            if (Kind == AiProviderKind.Ollama && error.ValueKind == JsonValueKind.String)
                return feature == "tools" && error.GetString() == $"{model} does not support tools";
            return String(error, "code") is "unsupported_parameter" or "unsupported_value" or "unsupported_feature"
                && String(error, "param") == (feature == "tools" ? "tools" : Kind == AiProviderKind.Ollama ? "format" : "response_format");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { return false; }
    }

    private async Task<Reply> SendAsync(AiChatConfiguration configuration, HttpMethod method,
        string route, object? body, string? key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var uri = Kind switch
        {
            AiProviderKind.Ollama => new Uri(new Uri(configuration.Endpoint.TrimEnd('/') + "/"), "/" + route),
            AiProviderKind.AzureOpenAi => new Uri(configuration.Endpoint.TrimEnd('/') +
                "/openai/deployments/" + Uri.EscapeDataString(configuration.Model) +
                "/chat/completions?api-version=2024-10-21"),
            _ => OpenAiProvider.BuildUri(configuration.Endpoint, route),
        };
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Enter an absolute HTTP(S) endpoint without embedded credentials.");
        using var request = new HttpRequestMessage(method, uri);
        if (Kind != AiProviderKind.Ollama && !string.IsNullOrWhiteSpace(key))
        {
            if (Kind == AiProviderKind.AzureOpenAi) request.Headers.Add("api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Bound even a misbehaving compatible endpoint's metadata/probe response.
        await response.Content.LoadIntoBufferAsync(262_144, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new(response.StatusCode, text);
    }

    private static string String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text)
        && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";

    private static bool IsValidEndpoint(AiChatConfiguration configuration) =>
        Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint)
        && endpoint.IsWellFormedOriginalString()
        && endpoint.Scheme is "http" or "https"
        && !string.IsNullOrEmpty(endpoint.Host)
        && string.IsNullOrEmpty(endpoint.UserInfo);

    private string? CaptureKey()
    {
        if (Kind == AiProviderKind.Ollama) return null;
        credentials.TryReadSecret(Kind, out var key);
        return key;
    }

    private sealed record Reply(HttpStatusCode Status, string Body)
    {
        public bool IsSuccess => (int)Status is >= 200 and <= 299;
    }
}
