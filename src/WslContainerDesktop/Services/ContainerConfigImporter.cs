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
    {
        JsonElement container;
        try
        {
            using var doc = JsonDocument.Parse(containerJson);
            container = Unwrap(doc.RootElement).Clone();
        }
        catch
        {
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

        var image = GetString(container, "Image");
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

        container.TryGetProperty("Config", out var config);

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

        return options;
    }

    private static string? ResolveNetwork(JsonElement container)
    {
        string? network = null;
        if (container.TryGetProperty("NetworkSettings", out var ns) &&
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
