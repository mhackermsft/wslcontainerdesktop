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

namespace WslContainerDesktop.Helpers;

/// <summary>
/// Rules for the Push dialog, where the chosen registry alone decides the destination and the
/// user types only the name inside that registry. Kept separate from the dialog so it is testable.
/// </summary>
public static class PushDestination
{
    private static readonly string[] DockerHubHosts = ["docker.io", "index.docker.io", "registry-1.docker.io"];

    /// <summary>
    /// Suggests a name inside <paramref name="registry"/> for a local image. The source registry
    /// is dropped, because pushing back to where an image was pulled from is almost never
    /// intended (and rarely allowed). Docker Hub names must start with the account name, so the
    /// signed-in username is used when known; otherwise only the image's own name is kept.
    /// </summary>
    public static string SuggestName(string? localReference, RegistryEntry registry, string? dockerHubUser)
    {
        var path = StripDigest(localReference ?? string.Empty).Trim();
        if (path.Length == 0 || path.StartsWith("<none>", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        path = StripHost(path);
        if (path.StartsWith("library/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["library/".Length..];
        }

        if (!registry.IsDefault)
        {
            return path;
        }

        var lastSlash = path.LastIndexOf('/');
        var leaf = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return string.IsNullOrWhiteSpace(dockerHubUser) ? leaf : $"{dockerHubUser.Trim()}/{leaf}";
    }

    /// <summary>
    /// If <paramref name="name"/> starts with a registry address, returns that host (Docker Hub
    /// aliases are reported as "docker.io"); otherwise null.
    /// </summary>
    public static string? HostIn(string name)
    {
        var value = name.Trim();
        if (!RegistryEntry.ReferenceHasRegistry(value))
        {
            return null;
        }

        var host = value[..value.IndexOf('/')];
        return IsDockerHubHost(host) ? "docker.io" : host;
    }

    /// <summary>Finds the configured registry an address refers to, or null.</summary>
    public static RegistryEntry? FindRegistry(IEnumerable<RegistryEntry> registries, string host) =>
        registries.FirstOrDefault(r => IsDockerHubHost(host)
            ? r.IsDefault
            : r.HasHost && string.Equals(r.Host.Trim().TrimEnd('/'), host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes a leading registry address, if any.</summary>
    public static string StripHost(string name)
    {
        var value = name.Trim();
        return RegistryEntry.ReferenceHasRegistry(value) ? value[(value.IndexOf('/') + 1)..] : value;
    }

    /// <summary>Returns a message explaining why the name can't be pushed, or null when it can.</summary>
    public static string? Validate(string name, RegistryEntry registry, bool allTags, string? dockerHubUser)
    {
        var value = name.Trim();
        if (value.Length == 0)
        {
            return "Enter the name the image should have in the registry.";
        }

        if (HostIn(value) is { } host)
        {
            return $"Leave out the registry address ({host}) and choose the registry above. " +
                "To push somewhere that isn't listed, add it on the Registries page first.";
        }

        if (registry.IsDefault && !value.Contains('/'))
        {
            var example = string.IsNullOrWhiteSpace(dockerHubUser) ? "yourname" : dockerHubUser.Trim();
            return $"Docker Hub names start with your Docker Hub username, for example {example}/{value}.";
        }

        if (!allTags && ExtractTag(value) is null)
        {
            return "Add a version tag (for example :1.0). Without one the push defaults to \"latest\", " +
                "which doesn't identify the actual version.";
        }

        return null;
    }

    /// <summary>The full reference that will be pushed.</summary>
    public static string Resolve(string name, RegistryEntry registry) => registry.Qualify(name.Trim());

    /// <summary>The destination as shown to the user, including Docker Hub's address.</summary>
    public static string Display(string name, RegistryEntry registry) =>
        registry.IsDefault ? $"docker.io/{name.Trim()}" : Resolve(name, registry);

    /// <summary>
    /// Returns the explicit tag of a reference, or null. A registry <c>host:port</c> and an
    /// <c>@sha256:…</c> digest are ignored.
    /// </summary>
    public static string? ExtractTag(string reference)
    {
        var value = StripDigest(reference).Trim();
        var lastSlash = value.LastIndexOf('/');
        var lastComponent = lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
        var colon = lastComponent.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var tag = lastComponent[(colon + 1)..].Trim();
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>Removes the tag (and any digest) from a reference.</summary>
    public static string StripTag(string reference)
    {
        var value = StripDigest(reference).Trim();
        var lastSlash = value.LastIndexOf('/');
        var lastColon = value.LastIndexOf(':');
        return lastColon > lastSlash ? value[..lastColon] : value;
    }

    private static string StripDigest(string reference)
    {
        var at = reference.IndexOf('@');
        return at >= 0 ? reference[..at] : reference;
    }

    private static bool IsDockerHubHost(string host) =>
        DockerHubHosts.Contains(host.Trim().TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
}
