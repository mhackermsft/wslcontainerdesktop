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
    public const string MemoryPolicy = "Standalone 0.10.3 metadata uses CLI process status and /v1/models. Model listing does not prove cached or loaded state. Model mutations and inference stay blocked until positive load/acquisition-safe evidence is available; no legacy management route or automatic fallback is used.";
    public event Action? StateChanged;

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
            throw AiProviderException.FromHttpFailure(AiProviderKind.FoundryLocal, "Foundry Local v1 metadata",
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
        var catalog = ids.Select(id => new FoundryLocalModel(id, "", "", "", "", "", null, "", "", null)).ToArray();
        // /v1/models enumerates advertised IDs. Cache/load evidence must come from
        // independently established runtime fields or a verified owned transition.
        return new(configuration, catalog, [], [], identity, CacheStateKnown: false, LoadStateKnown: false);
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

    public Task<FoundryLocalMutationResult> LoadAsync(AiChatConfiguration configuration, CancellationToken ct) =>
        BlockedMutation(configuration, ct);

    public Task<FoundryLocalMutationResult> UnloadAsync(AiChatConfiguration configuration, CancellationToken ct) =>
        BlockedMutation(configuration, ct);

    private static Task<FoundryLocalMutationResult> BlockedMutation(AiChatConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FoundryLocalRuntimeService.Validate(configuration);
        return Task.FromResult(new FoundryLocalMutationResult(false, LocalRuntimeResourceState.Unknown,
            LocalRuntimeResourceState.Unknown, "Standalone model mutation is not yet established by an observed lifecycle contract. No legacy endpoint, CLI mutation or acquisition was attempted."));
    }
}
