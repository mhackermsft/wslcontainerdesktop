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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Resolves one all-container snapshot; no background polling or cross-refresh cache.</summary>
public static class VolumeUsageResolver
{
    /// <param name="volumes">Volumes to enrich after inspecting the complete snapshot.</param>
    /// <param name="containers">Normalized inventory including stopped containers.</param>
    /// <param name="inspect">Read-only inspect operation, called at most four times concurrently.</param>
    /// <param name="ct">Refresh cancellation/timeout token; cancellation publishes no usage results.</param>
    /// <param name="containerInventoryComplete">
    /// True only when the inventory was fully enumerated and parsed. A successful empty inventory
    /// is complete; a failed/partial listing is not. False prevents any unused classification.
    /// </param>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        IReadOnlyList<VolumeInfo> volumes,
        IReadOnlyList<ContainerInfo> containers,
        Func<string, CancellationToken, Task<CommandResult>> inspect,
        CancellationToken ct = default,
        bool containerInventoryComplete = true)
    {
        var snapshots = new ConcurrentDictionary<string, ContainerMounts>(StringComparer.Ordinal);
        var diagnostics = new ConcurrentQueue<string>();
        var validContainers = containers.Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .DistinctBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (validContainers.Length != containers.Count)
        {
            diagnostics.Enqueue("Container list contains missing or duplicate identities; volume usage is incomplete.");
        }

        await Parallel.ForEachAsync(validContainers,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (container, token) =>
            {
                var result = await inspect(container.Id, token);
                if (!result.Success)
                {
                    diagnostics.Enqueue($"Could not inspect container {container.Id}: {result.ErrorText}");
                    return;
                }

                var mounts = ContainerMounts.Parse(result.StandardOutput);
                snapshots[container.Id] = mounts;
                foreach (var warning in mounts.Warnings)
                {
                    diagnostics.Enqueue($"Container {container.Id}: {warning}");
                }
            });
        ct.ThrowIfCancellationRequested();

        var complete = containerInventoryComplete && diagnostics.IsEmpty && snapshots.Count == containers.Count;
        if (!containerInventoryComplete)
        {
            diagnostics.Enqueue("Container inventory is incomplete; volume usage cannot be established for every container.");
        }

        var usersByVolume = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var container in validContainers)
        {
            if (!snapshots.TryGetValue(container.Id, out var mounts))
            {
                continue;
            }

            foreach (var name in mounts.Items.Select(m => m.VolumeName).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!usersByVolume.TryGetValue(name, out var users))
                {
                    users = [];
                    usersByVolume.Add(name, users);
                }
                users.Add(DisplayName(container));
            }
        }

        foreach (var volume in volumes)
        {
            var users = usersByVolume.TryGetValue(volume.Name, out var matches)
                ? matches.Order(StringComparer.Ordinal).ToArray() : [];
            volume.ContainerUsers = users;
            volume.UsageState = users.Length > 0
                ? complete ? VolumeUsageState.Exact : VolumeUsageState.Partial
                : complete ? VolumeUsageState.Unused : VolumeUsageState.Unknown;

            if (volume.UsageState == VolumeUsageState.Unknown && volume.IsAnonymous && volume.CreatedAt is { } created)
            {
                // Only containers without complete metadata are candidates for the old heuristic.
                var estimates = validContainers.Where(c =>
                        (!snapshots.TryGetValue(c.Id, out var mounts) || !mounts.IsComplete) &&
                        Math.Abs((decimal)c.CreatedAt - created.ToUnixTimeSeconds()) <= 3)
                    .Select(DisplayName).Order(StringComparer.Ordinal).ToArray();
                if (estimates.Length > 0)
                {
                    volume.ContainerUsers = estimates;
                    volume.UsageState = VolumeUsageState.Estimated;
                }
            }
        }

        return diagnostics.ToArray();
    }

    private static string DisplayName(ContainerInfo container) =>
        string.IsNullOrWhiteSpace(container.Name) ? container.Id : container.Name;
}
