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

using System.Collections.Concurrent;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Works out which containers are attached to each network, for the Networks page's "Used by"
/// column.
/// </summary>
/// <remarks>
/// <c>wslc network inspect</c> lists only running containers, so a stopped container that will
/// rejoin a network on start would be missed. Each container's own inspect output, by contrast,
/// keeps its network list (<c>NetworkSettings.Networks</c>) while stopped, so that is what is read
/// here. If any container cannot be inspected the answer is incomplete: a network with no users
/// found is then reported as unknown rather than as not in use.
/// </remarks>
public static class NetworkUsageResolver
{
    /// <param name="networks">Networks to fill in.</param>
    /// <param name="containers">All containers, including stopped ones.</param>
    /// <param name="inspect">Read-only container inspect, called at most four times at once.</param>
    /// <param name="ct">Cancels the refresh; nothing is written to the networks when cancelled.</param>
    /// <remarks>
    /// Results are written after awaiting without <c>ConfigureAwait(false)</c>, so when called from
    /// the UI thread the property changes (and their bindings) also happen on the UI thread.
    /// </remarks>
    /// <returns>Problems found while inspecting, for logging.</returns>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        IReadOnlyList<NetworkInfo> networks,
        IReadOnlyList<ContainerInfo> containers,
        Func<string, CancellationToken, Task<CommandResult>> inspect,
        CancellationToken ct = default)
    {
        var attachments = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var diagnostics = new ConcurrentQueue<string>();
        var valid = containers.Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .DistinctBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (valid.Length != containers.Count)
        {
            diagnostics.Enqueue("Container list contains missing or duplicate identities; network usage is incomplete.");
        }

        await Parallel.ForEachAsync(valid,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (container, token) =>
            {
                var result = await inspect(container.Id, token);
                if (!result.Success)
                {
                    diagnostics.Enqueue($"Could not inspect container {container.Id}: {result.ErrorText}");
                    return;
                }

                if (TryParseNetworkNames(result.StandardOutput, out var names))
                    attachments[container.Id] = names;
                else
                    diagnostics.Enqueue($"Container {container.Id}: inspect output has no network list.");
            });
        ct.ThrowIfCancellationRequested();

        var complete = diagnostics.IsEmpty && attachments.Count == containers.Count;
        var usersByNetwork = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var container in valid)
        {
            if (!attachments.TryGetValue(container.Id, out var names))
                continue;

            foreach (var name in names)
            {
                if (!usersByNetwork.TryGetValue(name, out var users))
                {
                    users = [];
                    usersByNetwork.Add(name, users);
                }
                users.Add(string.IsNullOrWhiteSpace(container.Name) ? container.Id : container.Name);
            }
        }

        foreach (var network in networks)
        {
            network.ContainerUsers = usersByNetwork.TryGetValue(network.Name, out var users)
                ? users.Order(StringComparer.Ordinal).ToArray()
                : [];
            network.UsageComplete = complete;
            network.UsagePending = false;
        }

        return diagnostics.ToArray();
    }

    /// <summary>
    /// Reads the network names (the keys of <c>NetworkSettings.Networks</c>) from container inspect
    /// JSON. Returns false when the output has no such map, so the caller treats usage as unknown.
    /// </summary>
    internal static bool TryParseNetworkNames(string json, out IReadOnlyList<string> names)
    {
        names = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
                root = root[0];

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("NetworkSettings", out var settings) || settings.ValueKind != JsonValueKind.Object ||
                !settings.TryGetProperty("Networks", out var networks) || networks.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            names = networks.EnumerateObject().Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
            return true;
        }
        catch (JsonException)
        {
            // Unreadable output is reported as unknown usage by the caller, not as "no networks".
            return false;
        }
    }
}
