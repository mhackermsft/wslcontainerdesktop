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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>What a container's storage would lose if it were removed with volume deletion.</summary>
/// <param name="Anonymous">Volumes created for this container that <c>remove --volumes</c> deletes.</param>
/// <param name="Named">Named volumes this container mounts.</param>
/// <param name="SharedAnonymous">Anonymous volumes other containers also mount.</param>
/// <param name="IsComplete">False when inspect metadata was incomplete, so the list may be partial.</param>
public sealed record ContainerVolumeImpact(
    IReadOnlyList<ContainerVolumeUsage> Anonymous,
    IReadOnlyList<ContainerVolumeUsage> Named,
    IReadOnlyList<ContainerVolumeUsage> SharedAnonymous,
    bool IsComplete)
{
    /// <summary>Named volumes no other container uses. Removing the container strands these, so they
    /// are offered for deletion explicitly rather than silently left behind.</summary>
    public IReadOnlyList<ContainerVolumeUsage> OrphanedNamed =>
        Named.Where(v => v.OtherContainers.Count == 0).ToArray();

    /// <summary>Named volumes that stay in use elsewhere and must never be deleted here.</summary>
    public IReadOnlyList<ContainerVolumeUsage> SharedNamed =>
        Named.Where(v => v.OtherContainers.Count > 0).ToArray();

    public bool HasDeletableData => Anonymous.Count > 0 || OrphanedNamed.Count > 0;
}

/// <param name="Name">Volume name as the engine reports it.</param>
/// <param name="Destination">Mount path inside the container.</param>
/// <param name="OtherContainers">Other containers that mount the same volume.</param>
public sealed record ContainerVolumeUsage(string Name, string Destination, IReadOnlyList<string> OtherContainers);

/// <summary>
/// Describes which volumes a container removal would delete, so the user can confirm against real
/// names rather than a generic warning. Read-only: this never mutates containers or volumes.
/// </summary>
public sealed class ContainerVolumeInspector(IWslcService wslc)
{
    public async Task<ContainerVolumeImpact> InspectAsync(string containerId, CancellationToken ct = default)
    {
        var inspect = await wslc.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
        if (!inspect.Success)
            return new([], [], [], false);

        var mounts = ContainerMounts.Parse(inspect.StandardOutput);
        var volumes = mounts.Items
            .Where(m => m.Type.Equals("volume", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(m.VolumeName))
            .ToArray();
        if (volumes.Length == 0)
            return new([], [], [], mounts.IsComplete);

        // Map every other container's volume usage so shared data can be called out explicitly.
        var otherUsers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var complete = mounts.IsComplete;
        try
        {
            foreach (var container in await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false))
            {
                if (string.Equals(container.Id, containerId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var otherInspect = await wslc.InspectContainerAsync(container.Id, ct).ConfigureAwait(false);
                if (!otherInspect.Success)
                {
                    complete = false;
                    continue;
                }
                var otherMounts = ContainerMounts.Parse(otherInspect.StandardOutput);
                complete &= otherMounts.IsComplete;
                foreach (var mount in otherMounts.Items)
                {
                    if (mount.VolumeName is not { Length: > 0 } name)
                        continue;
                    if (!otherUsers.TryGetValue(name, out var users))
                        otherUsers[name] = users = [];
                    var label = string.IsNullOrWhiteSpace(container.Name) ? container.Id : container.Name;
                    if (!users.Contains(label, StringComparer.OrdinalIgnoreCase))
                        users.Add(label);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Usage detail is advisory; an unreadable inventory must not block the removal flow.
            complete = false;
        }

        var anonymous = new List<ContainerVolumeUsage>();
        var named = new List<ContainerVolumeUsage>();
        var shared = new List<ContainerVolumeUsage>();
        foreach (var mount in volumes)
        {
            var name = mount.VolumeName!;
            var users = otherUsers.TryGetValue(name, out var list) ? list : [];
            var usage = new ContainerVolumeUsage(name, mount.Destination ?? string.Empty, users);
            // Engines may not report anonymity; a 64-hex name is the conventional anonymous form.
            var isAnonymous = mount.IsAnonymous ?? (name.Length == 64 && name.All(char.IsAsciiHexDigit));
            if (!isAnonymous)
            {
                named.Add(usage);
                continue;
            }
            anonymous.Add(usage);
            if (users.Count > 0)
                shared.Add(usage);
        }

        return new(anonymous, named, shared, complete);
    }
}
