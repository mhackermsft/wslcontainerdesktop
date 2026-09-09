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
/// Builds a reusable <see cref="RunContainerOptions"/> from a live container's
/// <c>wslc inspect</c> JSON, so a running container can be captured as a run profile. When the
/// image's own <c>inspect</c> JSON is supplied, image-baked environment variables, the default
/// command/entrypoint, working directory and user are subtracted so the profile keeps only the
/// user-specified overrides. Never throws for malformed input.
/// </summary>
public static class ContainerConfigImporter
{
    /// <summary>
    /// Parses <paramref name="containerJson"/> (output of <c>wslc inspect</c>) into run options.
    /// <paramref name="imageJson"/> is the optional <c>wslc inspect --type image</c> output used to
    /// strip image defaults. Returns null when no image reference can be determined.
    /// </summary>
    public static RunContainerOptions? FromInspect(string containerJson, string? imageJson = null)
        => FromInspect(containerJson, out _, imageJson);

    /// <summary>
    /// Imports recoverable settings and reports omitted or uncertain storage settings for review
    /// before saving. The existing profile schema and already-saved profiles are not changed.
    /// </summary>
    public static RunContainerOptions? FromInspect(
        string containerJson, out IReadOnlyList<string> warnings, string? imageJson = null)
    {
        var limitations = new List<string>();
        warnings = limitations;
        JsonElement container;
        try
        {
            using var doc = JsonDocument.Parse(containerJson);
            container = Unwrap(doc.RootElement).Clone();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            limitations.Add("Container inspect data could not be read.");
            return null;
        }

        if (container.ValueKind != JsonValueKind.Object)
        {
            limitations.Add("Container inspect data is not an object.");
            return null;
        }

        JsonElement? imageConfig = null;
        if (!string.IsNullOrWhiteSpace(imageJson))
        {
            try
            {
                using var imgDoc = JsonDocument.Parse(imageJson);
                var imgRoot = Unwrap(imgDoc.RootElement);
                if (imgRoot.TryGetProperty("Config", out var cfg))
                {
                    imageConfig = cfg.Clone();
                }
            }
            catch
            {
                imageConfig = null;
            }
        }

        container.TryGetProperty("Config", out var config);

        // The runnable image reference (e.g. "nginx:alpine"). Docker-schema inspect puts it in
        // Config.Image (top-level Image is the digest/ID there); this wslc build puts the reference at
        // top-level Image and leaves Config.Image empty. Prefer Config.Image, fall back to top-level.
        var image = GetString(config, "Image");
        if (string.IsNullOrWhiteSpace(image))
        {
            image = GetString(container, "Image");
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var options = new RunContainerOptions { Image = image!.Trim() };

        var name = GetString(container, "Name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            options.Name = name!.TrimStart('/').Trim();
        }

        // Environment: keep only entries not baked into the image.
        var imageEnv = imageConfig is { } ic ? new HashSet<string>(GetStringArray(ic, "Env"), StringComparer.Ordinal) : null;
        foreach (var env in GetStringArray(config, "Env"))
        {
            if (imageEnv is null || !imageEnv.Contains(env))
            {
                options.EnvironmentVariables.Add(env);
            }
        }

        // Command / entrypoint: only when they differ from the image defaults.
        var containerCmd = GetStringArray(config, "Cmd");
        var imageCmd = imageConfig is { } ic2 ? GetStringArray(ic2, "Cmd") : Array.Empty<string>();
        if (containerCmd.Count > 0 && (imageConfig is null || !containerCmd.SequenceEqual(imageCmd)))
        {
            options.Command = string.Join(' ', containerCmd);
        }

        var containerEntry = GetStringArray(config, "Entrypoint");
        var imageEntry = imageConfig is { } ic3 ? GetStringArray(ic3, "Entrypoint") : Array.Empty<string>();
        if (containerEntry.Count > 0 && imageConfig is not null && !containerEntry.SequenceEqual(imageEntry))
        {
            options.Entrypoint = string.Join(' ', containerEntry);
        }

        // Working dir / user: only when overriding the image default.
        var workingDir = GetString(config, "WorkingDir");
        var imageWorkingDir = imageConfig is { } ic4 ? GetString(ic4, "WorkingDir") : null;
        if (!string.IsNullOrWhiteSpace(workingDir) && !string.Equals(workingDir, imageWorkingDir, StringComparison.Ordinal))
        {
            options.WorkingDir = workingDir;
        }

        var user = GetString(config, "User");
        var imageUser = imageConfig is { } ic5 ? GetString(ic5, "User") : null;
        if (!string.IsNullOrWhiteSpace(user) && !string.Equals(user, imageUser, StringComparison.Ordinal))
        {
            options.User = user;
        }

        // Port mappings: top-level "Ports" map ("80/tcp" -> [{HostIp,HostPort}]).
        if (container.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in ports.EnumerateObject())
            {
                var (containerPort, proto) = SplitPortProto(entry.Name);
                if (entry.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var binding in entry.Value.EnumerateArray())
                {
                    var hostPort = GetString(binding, "HostPort");
                    if (string.IsNullOrWhiteSpace(hostPort))
                    {
                        continue;
                    }

                    var mapping = $"{hostPort}:{containerPort}";
                    if (!string.IsNullOrEmpty(proto) && !proto.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                    {
                        mapping += $"/{proto}";
                    }

                    if (!options.PortMappings.Contains(mapping))
                    {
                        options.PortMappings.Add(mapping);
                    }
                }
            }
        }

        // Network: the first attached network, unless it's the engine default bridge.
        options.Network = ResolveNetwork(container);

        ImportMounts(container, options, limitations);
        return options;
    }

    private static void ImportMounts(JsonElement container, RunContainerOptions options, List<string> warnings)
    {
        var mounts = ContainerMounts.Parse(container);
        warnings.AddRange(mounts.Warnings);
        if (!mounts.IsComplete)
        {
            warnings.Add("Storage metadata is incomplete; not all mounts could be recovered. Review storage before running this profile.");
        }

        var targets = new Dictionary<string, List<ContainerMount>>(StringComparer.Ordinal);
        foreach (var mount in mounts.Items)
        {
            var target = NormalizeTarget(mount.Destination);
            if (target is null)
            {
                warnings.Add("A mount was omitted because its container destination is missing or cannot be represented safely.");
                continue;
            }

            if (!targets.TryGetValue(target, out var entries))
            {
                entries = new List<ContainerMount>();
                targets.Add(target, entries);
            }

            entries.Add(mount);
        }

        foreach (var (target, entries) in targets)
        {
            var specs = entries.Select(mount => BuildMountSpec(mount, target, warnings)).ToList();
            if (specs.Any(spec => spec is null) || specs.Distinct(StringComparer.Ordinal).Count() != 1)
            {
                if (entries.Count > 1)
                {
                    warnings.Add($"Mounts at '{target}' were omitted because duplicate destinations have conflicting or uncertain settings.");
                }

                continue;
            }

            options.Volumes.Add(specs[0]!);
            if (entries.Count > 1)
            {
                warnings.Add($"Repeated identical mounts at '{target}' were captured only once.");
            }
        }
    }

    private static string? BuildMountSpec(ContainerMount mount, string target, List<string> warnings)
    {
        string? source;
        if (mount.Type.Equals("volume", StringComparison.OrdinalIgnoreCase))
        {
            source = mount.VolumeName;
            if (source is null ||
                (!string.IsNullOrEmpty(mount.Name) && !ContainerMount.IsVolumeIdentifier(mount.Name)) ||
                (ContainerMount.IsVolumeIdentifier(mount.Name) && ContainerMount.IsVolumeIdentifier(mount.Source) &&
                 !string.Equals(mount.Name, mount.Source, StringComparison.Ordinal)))
            {
                warnings.Add($"Volume at '{target}' was omitted because its reusable name is missing or ambiguous.");
                return null;
            }

            if (mount.IsAnonymous == true || (source.Length == 64 && source.All(Uri.IsHexDigit)))
            {
                warnings.Add($"Volume at '{target}' was omitted because it is anonymous or has a likely anonymous generated name. Select storage explicitly if needed.");
                return null;
            }
        }
        else if (mount.Type.Equals("bind", StringComparison.OrdinalIgnoreCase))
        {
            source = mount.Source;
            if (!IsReusableHostPath(source))
            {
                warnings.Add($"Bind at '{target}' was omitted because its source is not a reusable absolute Windows or UNC host path (it may be an engine-internal Linux path). No path conversion was attempted.");
                return null;
            }
        }
        else
        {
            warnings.Add($"Mount at '{target}' was omitted because mount type '{mount.Type}' is unsupported in saved profiles.");
            return null;
        }

        if (mount.ReadOnly is null)
        {
            warnings.Add($"Mount at '{target}' was omitted because read-only/read-write metadata is missing or conflicting; it was not assumed writable.");
            return null;
        }

        if (mount.ReadOnly == true)
        {
            warnings.Add($"Mount at '{target}' is captured read-only (:ro). Review this access mode before running the profile.");
        }

        return $"{source}:{target}{(mount.ReadOnly == true ? ":ro" : string.Empty)}";
    }

    private static string? NormalizeTarget(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || destination != destination.Trim() || !destination.StartsWith('/') ||
            destination.Any(c => char.IsControl(c) || c is ':' or '\\'))
        {
            return null;
        }

        var parts = destination.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            return null;
        }

        return "/" + string.Join('/', parts);
    }

    private static bool IsReusableHostPath(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source != source.Trim() ||
            source.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '*' or '?'))
        {
            return false;
        }

        var path = source.Replace('/', '\\');
        var drivePath = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var uncPath = path.StartsWith(@"\\", StringComparison.Ordinal);
        if (!drivePath && !uncPath)
        {
            return false;
        }

        var remainder = drivePath ? path[3..] : path[2..];
        var parts = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (remainder.Contains(':') || parts.Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
        {
            return false;
        }

        return drivePath || (parts.Length >= 2 &&
            !parts[0].Equals("wsl$", StringComparison.OrdinalIgnoreCase) &&
            !parts[0].Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveNetwork(JsonElement container)
    {
        string? network = null;
        if (container.TryGetProperty("NetworkSettings", out var ns) &&
            ns.ValueKind == JsonValueKind.Object &&
            ns.TryGetProperty("Networks", out var nets) &&
            nets.ValueKind == JsonValueKind.Object)
        {
            foreach (var net in nets.EnumerateObject())
            {
                network = net.Name;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(network) &&
            container.TryGetProperty("HostConfig", out var hc))
        {
            network = GetString(hc, "NetworkMode");
        }

        if (string.IsNullOrWhiteSpace(network) ||
            network.Equals("bridge", StringComparison.OrdinalIgnoreCase) ||
            network.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return network;
    }

    private static (string Port, string Proto) SplitPortProto(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? (key, string.Empty) : (key[..slash], key[(slash + 1)..]);
    }

    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }
}
