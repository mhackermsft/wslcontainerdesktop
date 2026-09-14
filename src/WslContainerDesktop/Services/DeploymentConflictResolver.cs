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

using System.Globalization;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>What a deployment had to change to avoid disturbing what is already running.</summary>
public sealed record DeploymentAdjustment(
    string? OriginalName,
    string? Name,
    IReadOnlyList<string> Notes)
{
    public bool Adjusted => Notes.Count > 0;

    /// <summary>One sentence for the user or the model; empty when nothing had to change.</summary>
    public string Summary => Notes.Count == 0
        ? string.Empty
        : "Deployed alongside what was already running: " + string.Join("; ", Notes) + ".";
}

/// <summary>
/// Makes a new deployment independent of the containers already on the machine.
///
/// A deployment must never resolve a clash by stopping, removing, or reusing something the user is
/// already running — that trades their working state for ours. Instead the new deployment moves:
/// it takes the next free container name, the next free host ports, and its own copies of any named
/// volumes. The last part matters as much as the name: two database containers pointed at one
/// volume corrupt each other's data, so a renamed deployment that kept the original's volumes would
/// be worse than the clash it avoided.
///
/// Bind mounts are left exactly as written, because a host path is the user's explicit choice about
/// where data lives, not an implementation detail this can rewrite.
/// </summary>
public sealed class DeploymentConflictResolver(IWslcService wslc)
{
    private const int MaxAttempts = 200;

    /// <summary>
    /// Picks a project name, networks and ports that do not collide with what is already deployed.
    ///
    /// Call before <c>ApplyProjectNamespacing</c>. That method prefixes volumes and generated
    /// networks with the project name, so a free project name carries most of the isolation — but
    /// it deliberately preserves an explicitly named network (<c>name: openwebui-net</c>), and
    /// published host ports come straight from the YAML. Both would collide with the first
    /// deployment, so both are adjusted here when the project has to move.
    /// </summary>
    public async Task<DeploymentAdjustment> ResolveProjectAsync(ComposeProject project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        IReadOnlyList<ContainerInfo> containers;
        IReadOnlyList<NetworkInfo> networks;
        try
        {
            containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            networks = await wslc.ListNetworksAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(project.Name, project.Name, []);
        }

        var originalName = project.Name;
        var notes = new List<string>();

        // Ask "does anything already belong to this project name" rather than reverse-engineering a
        // project out of each container name. Splitting on the last underscore mis-reads a service
        // whose own name contains one ("proj_web_ui" -> "proj_web"), and sees nothing at all for a
        // service pinned with container_name, so an occupied project would look free and the repeat
        // launch would adopt and recreate the deployment already running.
        var names = containers.Select(c => Normalize(c.Name)).Where(n => n.Length > 0).ToArray();
        var pinned = new HashSet<string>(
            project.Services.Select(s => (s.Options.Name ?? string.Empty).Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        bool ProjectTaken(string candidate) =>
            names.Any(n => n.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase));

        var suffix = 0;
        // A container_name is an exact name the project will claim wherever it runs, so it occupies
        // the project just as much as a prefixed container does.
        if (!string.IsNullOrWhiteSpace(project.Name)
            && (ProjectTaken(project.Name) || pinned.Any(p => names.Contains(p, StringComparer.OrdinalIgnoreCase))))
        {
            for (var i = 2; i < MaxAttempts; i++)
            {
                if (!ProjectTaken($"{project.Name}-{i}"))
                {
                    suffix = i;
                    break;
                }
            }

            suffix = suffix == 0 ? MaxAttempts : suffix;
            var renamed = $"{project.Name}-{suffix}";
            notes.Add($"named \"{renamed}\" because \"{project.Name}\" is already deployed");
            project.Name = renamed;
        }

        if (suffix > 0)
        {
            // A pinned container_name does not move with the project, so it would still collide.
            // Suffix it too, otherwise the repeat launch fights the running deployment for the name.
            foreach (var service in project.Services)
            {
                var pinnedName = (service.Options.Name ?? string.Empty).Trim();
                if (pinnedName.Length == 0)
                    continue;
                var renamed = $"{pinnedName}-{suffix}";
                service.Options.Name = renamed;
                notes.Add($"container \"{renamed}\"");
            }

            var takenNetworks = new HashSet<string>(
                networks.Select(n => n.Name).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            foreach (var network in project.Networks.Where(n => !n.External && !string.IsNullOrWhiteSpace(n.ExplicitName)))
            {
                if (!takenNetworks.Contains(network.ExplicitName!))
                    continue;
                var renamed = $"{network.ExplicitName}-{suffix}";
                notes.Add($"using network \"{renamed}\"");
                network.ExplicitName = renamed;
            }
        }

        var takenPorts = new HashSet<int>(containers.SelectMany(PublishedHostPorts));
        foreach (var service in project.Services)
        {
            RemapPorts(service.Options, takenPorts, notes);
        }

        return new(originalName, project.Name, notes);
    }

    public async Task<DeploymentAdjustment> ResolveAsync(RunContainerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<ContainerInfo> inventory;
        try
        {
            inventory = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without inventory we cannot tell what would clash. Deploy exactly as asked and let the
            // engine reject a genuine conflict, rather than inventing names from a guess.
            return new(options.Name, options.Name, []);
        }

        var takenNames = new HashSet<string>(
            inventory.Select(c => Normalize(c.Name)).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        var takenPorts = new HashSet<int>(inventory.SelectMany(PublishedHostPorts));

        var notes = new List<string>();
        var originalName = options.Name;
        var suffix = 0;

        if (!string.IsNullOrWhiteSpace(options.Name) && takenNames.Contains(Normalize(options.Name)))
        {
            suffix = NextFreeSuffix(options.Name!, takenNames);
            var renamed = $"{options.Name}-{suffix}";
            notes.Add($"named \"{renamed}\" because \"{options.Name}\" is in use");
            options.Name = renamed;
        }

        RemapPorts(options, takenPorts, notes);

        // Only a renamed deployment gets its own volumes. Deploying under the original name means
        // nothing clashed, and reusing that name's data is the expected behaviour.
        if (suffix > 0)
        {
            RenameNamedVolumes(options, suffix, notes);
        }

        return new(originalName, options.Name, notes);
    }

    private static void RemapPorts(RunContainerOptions options, HashSet<int> takenPorts, List<string> notes)
    {
        for (var i = 0; i < options.PortMappings.Count; i++)
        {
            if (!TrySplitPort(options.PortMappings[i], out var prefix, out var hostPort, out var rest))
                continue;
            if (!takenPorts.Contains(hostPort))
            {
                // Claim it so two mappings in the same deployment cannot land on one port.
                takenPorts.Add(hostPort);
                continue;
            }

            var free = NextFreePort(hostPort, takenPorts);
            if (free is null)
                continue;

            takenPorts.Add(free.Value);
            options.PortMappings[i] = $"{prefix}{free.Value.ToString(CultureInfo.InvariantCulture)}{rest}";
            notes.Add($"published on host port {free.Value} instead of {hostPort}");
        }
    }

    private static void RenameNamedVolumes(RunContainerOptions options, int suffix, List<string> notes)
    {
        var renamed = new List<string>();
        for (var i = 0; i < options.Volumes.Count; i++)
        {
            var spec = options.Volumes[i];
            var separator = SourceSeparator(spec);
            if (separator < 0)
                continue;

            var source = spec[..separator];
            if (!IsNamedVolume(source))
                continue;

            var newSource = $"{source}-{suffix}";
            options.Volumes[i] = newSource + spec[separator..];
            renamed.Add(newSource);
        }

        if (renamed.Count > 0)
        {
            notes.Add($"using its own {(renamed.Count == 1 ? "volume" : "volumes")} {string.Join(", ", renamed)} so it cannot write over existing data");
        }
    }

    private static int NextFreeSuffix(string name, HashSet<string> taken)
    {
        for (var i = 2; i < MaxAttempts; i++)
        {
            if (!taken.Contains(Normalize($"{name}-{i}")))
                return i;
        }

        return MaxAttempts;
    }

    private static int? NextFreePort(int start, HashSet<int> taken)
    {
        for (var port = start + 1; port < 65536 && port < start + MaxAttempts; port++)
        {
            if (!taken.Contains(port))
                return port;
        }

        return null;
    }

    private static IEnumerable<int> PublishedHostPorts(ContainerInfo container)
    {
        // PortsKnown false means inspect could not resolve them; treat unknown as "not proven free"
        // by simply contributing nothing, since inventing a clash would move ports needlessly.
        foreach (var mapping in container.Ports)
        {
            if (mapping.HostPort > 0)
                yield return mapping.HostPort;
        }
    }

    /// <summary>Engine inventory reports names with and without Docker's leading slash.</summary>
    private static string Normalize(string? name) => (name ?? string.Empty).Trim().TrimStart('/');

    /// <summary>
    /// Splits <c>[ip:]host:container[/proto]</c> into the text before the host port, the host port,
    /// and everything after it, so a remap preserves any bind address and protocol exactly.
    /// </summary>
    private static bool TrySplitPort(string mapping, out string prefix, out int hostPort, out string rest)
    {
        prefix = string.Empty;
        rest = string.Empty;
        hostPort = 0;
        if (string.IsNullOrWhiteSpace(mapping))
            return false;

        var parts = mapping.Split(':');
        if (parts.Length < 2)
            return false;

        // The host port is the second-to-last colon-separated field in every supported form.
        var hostIndex = parts.Length - 2;
        if (!int.TryParse(parts[hostIndex], NumberStyles.None, CultureInfo.InvariantCulture, out hostPort))
            return false;

        prefix = hostIndex == 0 ? string.Empty : string.Join(':', parts[..hostIndex]) + ":";
        rest = ":" + string.Join(':', parts[(hostIndex + 1)..]);
        return true;
    }

    /// <summary>Index of the colon separating a mount's source from its target, or -1.</summary>
    private static int SourceSeparator(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return -1;
        // Skip a Windows drive letter so "C:\data:/data" is not split at the drive colon.
        var start = spec.Length > 1 && spec[1] == ':' && char.IsLetter(spec[0]) ? 2 : 0;
        var index = spec.IndexOf(':', start);
        return index <= 0 ? -1 : index;
    }

    /// <summary>
    /// A named volume is a bare identifier. Anything rooted, drive-qualified or dot-relative is a
    /// host path the user chose deliberately.
    /// </summary>
    private static bool IsNamedVolume(string source) =>
        !string.IsNullOrWhiteSpace(source)
        && !source.StartsWith('/')
        && !source.StartsWith('.')
        && !source.StartsWith('~')
        && !source.Contains('\\', StringComparison.Ordinal)
        && !(source.Length > 1 && source[1] == ':');
}
