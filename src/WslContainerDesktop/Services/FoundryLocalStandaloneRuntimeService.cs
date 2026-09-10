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

/// <summary>
/// Standalone 0.10.3 has a v1 inference API, not the legacy /openai management API.
/// CLI process identity brackets metadata reads; model listing never implies loaded state.
/// </summary>
public sealed class FoundryLocalStandaloneRuntimeService(
    FoundryLocalHttpClient http, FoundryLocalCli cli) : IFoundryLocalRuntimeService
{
    public const string MemoryPolicy = "The external daemon owns memory and idle policy. Setup loads only the pinned CPU model; it never evicts other models or stops a pre-existing server automatically. Load proof is bound to a verified CLI load, synthetic completion and process/start identity, not model listing. Refresh after external lifecycle changes. Cancellation is not rollback.";
    public const string AcquisitionGuidance = "Setup audits and pins the runtime and model files it downloads. Starting Microsoft Foundry Local may cause Windows to install execution-provider packages selected by Microsoft, using the network. This app neither selects, pins nor audits those vendor-managed versions. The 0.10.3 model-list endpoint requires online catalog access, even with cached model files; inference runs locally.";
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private string? _loadedIdentity;
    public event Action? StateChanged;

    internal void InvalidateLoadProof()
    {
        Volatile.Write(ref _loadedIdentity, null);
        StateChanged?.Invoke();
    }

    public async Task<FoundryLocalInventory> ReadInventoryAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        FoundryLocalRuntimeService.Validate(configuration, requireModel: false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var before = await cli.ReadServerStatusAsync(token).ConfigureAwait(false);
        RequireMatchingHost(before, configuration.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            FoundryLocalEndpoint.BuildUri(configuration.Endpoint, "v1/models"));
        using var response = await http.Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw AiProviderException.FromHttpFailure(AiProviderKind.FoundryLocal, "Foundry Local v1 metadata (0.10.3 catalog requires network access)",
                response.StatusCode, configuration.Endpoint, configuration.Model, "");
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token).ConfigureAwait(false);
        using var models = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var ids = ParseModelIds(models.RootElement);
        var after = await cli.ReadServerStatusAsync(token).ConfigureAwait(false);
        RequireMatchingHost(after, configuration.Endpoint);
        var identity = RuntimeIdentity(before, configuration.Endpoint);
        if (identity != RuntimeIdentity(after, configuration.Endpoint))
        {
            StateChanged?.Invoke();
            throw new InvalidOperationException("Foundry Local restarted during metadata observation. Refresh before inference.");
        }
        var catalog = ids.Select(id => id is FoundryLocalModelArtifacts.CatalogId or FoundryLocalModelArtifacts.ModelId
            ? new FoundryLocalModel(FoundryLocalModelArtifacts.ModelId, "4", "chat", "ONNX", "CPU",
                "CPUExecutionProvider", FoundryLocalModelArtifacts.TotalBytes / 1_000_000d,
                FoundryLocalModelArtifacts.License, FoundryLocalModelArtifacts.LicenseUrl, null)
            : new FoundryLocalModel(id, "", "", "", "", "", null, "", "", null)).DistinctBy(m => m.Id).ToArray();
        // /v1/models enumerates advertised IDs. Cache/load evidence must come from
        // independently established runtime fields or a verified owned transition.
        var loaded = Volatile.Read(ref _loadedIdentity) == identity;
        return new(configuration, catalog,
            loaded ? [FoundryLocalModelArtifacts.ModelId] : [],
            loaded ? [FoundryLocalModelArtifacts.ModelId] : [], identity,
            CacheStateKnown: loaded, LoadStateKnown: loaded);
    }

    internal static string[] ParseModelIds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidDataException("Unknown Foundry v1 model-list schema.");
        var ids = new List<string>();
        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                || id.GetString()!.Length > 512 || id.GetString()!.Any(char.IsControl)
                || model.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                throw new InvalidDataException("Unknown Foundry v1 model identity.");
            ids.Add(id.GetString()!);
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new InvalidDataException("Duplicate Foundry v1 model identities.");
        return ids.ToArray();
    }

    internal static void RequireMatchingHost(FoundryLocalServerStatus status, string endpoint)
    {
        if (!status.Running)
            throw new InvalidOperationException("Foundry Local is stopped; stale stored URLs cannot authorize a connection.");
        if (status.Pid is null || status.StartedAt is null
            || !status.Endpoints.Any(url => Authority(url) == Authority(endpoint)))
            throw new InvalidOperationException("Configured Foundry endpoint does not match the observed standalone process. No fallback attempted.");
    }

    internal static string RuntimeIdentity(FoundryLocalServerStatus status, string endpoint) =>
        AiCapabilityService.HashIdentity(JsonSerializer.Serialize(new { Version = "0.10.3", status.Pid, status.StartedAt, Endpoint = Authority(endpoint) }));

    private static string Authority(string endpoint) => FoundryLocalEndpoint.Validate(endpoint).GetLeftPart(UriPartial.Authority);

    public Task<FoundryLocalMutationResult> LoadAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FoundryLocalRuntimeService.Validate(configuration);
        return Task.FromResult(new FoundryLocalMutationResult(false, LocalRuntimeResourceState.Unknown,
            LocalRuntimeResourceState.Unknown, "Use initial-model setup to verify and register the pinned files before loading. No implicit model download."));
    }

    internal async Task<FoundryLocalMutationResult> LoadRegisteredAsync(AiChatConfiguration configuration,
        IProgress<string>? progress, CancellationToken ct, Func<bool>? isCurrent = null,
        string? expectedRuntimeIdentity = null)
    {
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (isCurrent?.Invoke() == false)
                throw new InvalidOperationException("Setup approval was invalidated. No further load or inference requested.");
        }
        Check();
        FoundryLocalRuntimeService.Validate(configuration);
        if (configuration.Model != FoundryLocalModelArtifacts.ModelId)
            throw new InvalidOperationException("Load requires the exact registered CPU model version.");
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _loadedIdentity, null);
            StateChanged?.Invoke();
            var before = await cli.ReadServerStatusAsync(ct).ConfigureAwait(false);
            RequireMatchingHost(before, configuration.Endpoint);
            var identity = RuntimeIdentity(before, configuration.Endpoint);
            if (expectedRuntimeIdentity is not null && identity != expectedRuntimeIdentity)
                throw new InvalidOperationException("The prepared server was replaced before model loading. No replacement server was adopted.");
            progress?.Report("Loading the registered CPU model; no CLI model-download command is used...");
            Check();
            await cli.LoadModelAsync(configuration.Model, ct).ConfigureAwait(false);
            var afterLoad = await cli.ReadServerStatusAsync(ct).ConfigureAwait(false);
            RequireMatchingHost(afterLoad, configuration.Endpoint);
            if (identity != RuntimeIdentity(afterLoad, configuration.Endpoint))
                throw new InvalidOperationException("Foundry restarted during model loading. Readiness was not accepted.");
            progress?.Report("Verifying local readiness with one synthetic 'Reply OK only' completion...");
            Check();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                FoundryLocalEndpoint.BuildUri(configuration.Endpoint, "v1/chat/completions"))
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    model = configuration.Model, stream = false, max_tokens = 8,
                    messages = new[] { new { role = "user", content = "Reply OK only." } },
                }),
            };
            var result = await AiHttpStreaming.SendAsync(http.Transport, request,
                new AiChatRequest(configuration, []), [], new(StringComparer.Ordinal), ct, false).ConfigureAwait(false);
            if (result.AssistantText?.Trim() != "OK")
                throw new InvalidDataException("The loaded model did not return the expected synthetic readiness response.");
            var after = await cli.ReadServerStatusAsync(ct).ConfigureAwait(false);
            RequireMatchingHost(after, configuration.Endpoint);
            if (identity != RuntimeIdentity(after, configuration.Endpoint))
                throw new InvalidOperationException("Foundry restarted during readiness verification.");
            Check();
            Volatile.Write(ref _loadedIdentity, identity);
            return new(true, LocalRuntimeResourceState.Retained, LocalRuntimeResourceState.Retained,
                "Pinned CPU model loaded and synthetic local response verified. Assistant tool/JSON/stream support still requires independent capability checks.");
        }
        finally
        {
            StateChanged?.Invoke();
            _lifecycleGate.Release();
        }
    }

    public async Task<FoundryLocalMutationResult> UnloadAsync(AiChatConfiguration configuration, CancellationToken ct)
    {
        FoundryLocalRuntimeService.Validate(configuration);
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = await cli.ReadServerStatusAsync(ct).ConfigureAwait(false);
            RequireMatchingHost(before, configuration.Endpoint);
            if (configuration.Model != FoundryLocalModelArtifacts.ModelId
                || Volatile.Read(ref _loadedIdentity) != RuntimeIdentity(before, configuration.Endpoint))
                return new(false, LocalRuntimeResourceState.Unknown, LocalRuntimeResourceState.Retained,
                    "No current app-verified load of this model on this process. No other user's model was unloaded.");
            Volatile.Write(ref _loadedIdentity, null);
            StateChanged?.Invoke();
            await cli.UnloadModelAsync(configuration.Model, ct).ConfigureAwait(false);
            return new(true, LocalRuntimeResourceState.Removed, LocalRuntimeResourceState.Retained,
                "Foundry acknowledged unloading the selected model. Files retained; this does not measure GPU/RAM release.");
        }
        finally
        {
            StateChanged?.Invoke();
            _lifecycleGate.Release();
        }
    }
}
