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

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// CLI REST slice documented at learn.microsoft.com/azure/foundry-local/reference/reference-rest.
/// Does not own/start the runtime, download assets, choose EPs, or use container services.
/// </summary>
public sealed class FoundryLocalRuntimeService(
    FoundryLocalHttpClient http, ISettingsService settings) : IFoundryLocalRuntimeService
{
    public const string AcquisitionGuidance = "Model load and model/EP acquisition are blocked in this app. Runtime-only package setup is separate: registration does not authorize runtime initialization or model/EP downloads. Loading cached model data may acquire execution providers, and no authoritative verification of prepared EP artifacts is available. Use an externally prepared, already-loaded host after an independent audit of exact versions, integrity, licenses and publication dates (at least seven days old). Catalog metadata and user attestation are not substitutes for that audit; device/EP hints are not hardware compatibility measurements.";
    public const string MemoryPolicy = "This integration targets the preview Foundry Local CLI REST API, which may change; parity with an SDK's optional REST server is not guaranteed. Inference requires an already-loaded cached model. The only supported explicit memory mutation is selected-model unload, without choosing an EP or forcing past its TTL. The app does not load models, keep models alive, unload other models, or stop the shared runtime. Cancellation is not rollback; refresh observed state.";
    public event Action? StateChanged;
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public async Task<FoundryLocalInventory> ReadInventoryAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        Validate(configuration, requireModel: false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var status = await ReadAsync(configuration, "openai/status", token).ConfigureAwait(false);
        // The advertised endpoint is evidence only, NEVER a new destination.
        if (status.RootElement.ValueKind != JsonValueKind.Object
            || !status.RootElement.TryGetProperty("Endpoints", out var endpoints)
            || endpoints.ValueKind != JsonValueKind.Array)
            throw InvalidMetadata();
        using var catalog = await ReadAsync(configuration, "foundry/list", token).ConfigureAwait(false);
        if (catalog.RootElement.ValueKind != JsonValueKind.Object
            || !catalog.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array
            || models.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.Object))
            throw InvalidMetadata();
        var entries = models.EnumerateArray().Select(m => new FoundryLocalModel(
            Text(m, "name"), Text(m, "version"), Text(m, "task"), Text(m, "modelType"),
            m.TryGetProperty("runtime", out var runtime) ? Text(runtime, "deviceType") : "",
            m.TryGetProperty("runtime", out runtime) ? Text(runtime, "executionProvider") : "",
            m.TryGetProperty("fileSizeMb", out var size) && size.ValueKind == JsonValueKind.Number
                && size.TryGetDouble(out var n) && n >= 0 && double.IsFinite(n) ? n : null,
            Text(m, "license"), Text(m, "licenseDescription"),
            m.TryGetProperty("supportsToolCalling", out var tools) && tools.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? tools.GetBoolean() : null)).ToArray();
        if (entries.Any(m => string.IsNullOrWhiteSpace(m.Id)) ||
            entries.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw InvalidMetadata();
        using var cached = await ReadAsync(configuration, "openai/models", token).ConfigureAwait(false);
        using var loaded = await ReadAsync(configuration, "openai/loadedmodels", token).ConfigureAwait(false);
        return new(configuration, entries, Names(cached), Names(loaded),
            AiCapabilityService.HashIdentity(status.RootElement.GetRawText()));
    }

    public Task<FoundryLocalMutationResult> LoadAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireCurrent(configuration);
        // No request, even for cached/already-loaded ONNX models: cached data cannot establish
        // the authoritative native/EP artifact audit needed to permit a load operation.
        return Task.FromResult(new FoundryLocalMutationResult(false, LocalRuntimeResourceState.Unknown,
            LocalRuntimeResourceState.Unknown, "Load blocked. " + AcquisitionGuidance));
    }

    public async Task<FoundryLocalMutationResult> UnloadAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        RequireCurrent(configuration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        EventHandler changed = (_, _) =>
        {
            if (!IsCurrent(configuration)) deadline.Cancel();
        };
        settings.Changed += changed;
        try
        {
            await _mutation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                RequireCurrent(configuration);
                StateChanged?.Invoke();
                var before = await ReadInventoryAsync(configuration, token).ConfigureAwait(false);
                RequireCurrent(configuration);
                token.ThrowIfCancellationRequested();
                if (!before.IsLoaded)
                    return new(true, LocalRuntimeResourceState.Absent,
                        before.IsCached ? LocalRuntimeResourceState.Retained : LocalRuntimeResourceState.Absent,
                        "The selected model was already observed unloaded. No mutation was sent.");
                // Documented GET mutation, used ONLY by this explicit command. No load, download,
                // EP override, force-unload, unload-all or invented SDK route.
                using var response = await SendAsync(configuration,
                    "openai/unload/" + Uri.EscapeDataString(configuration.Model), token).ConfigureAwait(false);
                var after = await ReadInventoryAsync(configuration, token).ConfigureAwait(false);
                RequireCurrent(configuration);
                var confirmed = !after.IsLoaded;
                return new(confirmed, after.IsLoaded ? LocalRuntimeResourceState.Retained : LocalRuntimeResourceState.Absent,
                    after.IsCached ? LocalRuntimeResourceState.Retained : LocalRuntimeResourceState.Absent,
                    confirmed ? "Requested model state observed. " + MemoryPolicy
                        : "The request returned, but the requested memory state is not yet observed. Refresh; do not assume rollback or retry automatically.");
            }
            finally
            {
                StateChanged?.Invoke();
                _mutation.Release();
            }
        }
        finally
        {
            settings.Changed -= changed;
        }
    }

    private void RequireCurrent(AiChatConfiguration configuration)
    {
        Validate(configuration, requireModel: true);
        if (!IsCurrent(configuration))
            throw new OperationCanceledException("Foundry Local configuration changed. Refresh before requesting model changes.");
    }

    private bool IsCurrent(AiChatConfiguration configuration) =>
        settings.AiProvider == AiProviderKind.FoundryLocal
        && configuration == AiConversationContext.Capture(settings, AiProviderKind.FoundryLocal);

    internal static void Validate(AiChatConfiguration configuration, bool requireModel = true)
    {
        if (configuration.Kind != AiProviderKind.FoundryLocal)
            throw new ArgumentException("A Foundry Local configuration is required.");
        FoundryLocalEndpoint.Validate(configuration.Endpoint);
        if (requireModel && (string.IsNullOrWhiteSpace(configuration.Model) ||
            configuration.Model.Length > 512 || configuration.Model.Any(char.IsControl)
            || configuration.Model is "." or ".." || configuration.Model.IndexOfAny(['/', '\\']) >= 0))
            throw new ArgumentException("Choose the actual Foundry Local model ID; no default model or alias is inferred.");
    }

    private async Task<JsonDocument> ReadAsync(AiChatConfiguration configuration, string route, CancellationToken ct)
    {
        using var response = await SendAsync(configuration, route, ct).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> SendAsync(AiChatConfiguration configuration, string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FoundryLocalEndpoint.BuildUri(configuration.Endpoint, route));
        var response = await http.Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode;
            response.Dispose();
            throw AiProviderException.FromHttpFailure(AiProviderKind.FoundryLocal, "Foundry Local runtime",
                code, configuration.Endpoint, configuration.Model, "");
        }
        return response;
    }

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? "" : "";
    private static string[] Names(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(m.GetString())))
            throw InvalidMetadata();
        return document.RootElement.EnumerateArray().Select(m => m.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static InvalidDataException InvalidMetadata() => new("Foundry Local returned unrecognized inventory metadata. No model readiness or acquisition approval was inferred.");
}
