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

namespace WslContainerDesktop.Services;

/// <summary>
/// Explains whether Windows registry policy allows WSL and WSL containers to run.
/// </summary>
public static class WslRegistryPolicyGuard
{
    public const string BuildBlockedMessage =
        "wslc build is blocked while your organization configures WSLContainerRegistryAllowlist. " +
        "Build locally outside the policy boundary or ask your administrator to remove the allowlist.";

    /// <summary>
    /// Extracts the registry host portion of a Docker-style image reference.
    /// </summary>
    public static string ExtractRegistryHost(string imageReference)
    {
        var text = (imageReference ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "docker.io";
        }

        var slash = text.IndexOf('/');
        if (slash <= 0)
        {
            return "docker.io";
        }

        // Docker reference rules: the first path segment is a registry host when it contains '.' or
        // ':', is "localhost", or has uppercase letters (repository paths must be lowercase).
        var first = text[..slash];
        return first.Contains('.') || first.Contains(':') || first.Equals("localhost", StringComparison.Ordinal) ||
               first.Any(char.IsUpper)
            ? first.ToLowerInvariant()
            : "docker.io";
    }

    /// <summary>
    /// Validates image reference before the app uses it for an external operation.
    /// </summary>
    public static string? ValidateImageReference(WslPolicySnapshot policy, string imageReference)
    {
        if (policy.RegistryAllowlist.State == WslRegistryAllowlistState.Invalid)
        {
            return "Your organization's WSLContainerRegistryAllowlist policy is invalid. " +
                "Contact your administrator to correct the registry allowlist policy.";
        }

        if (!policy.RegistryAllowlist.IsConfigured)
        {
            return null;
        }

        var registry = ExtractRegistryHost(imageReference);
        if (policy.RegistryAllowlist.Registries.Any(allowed => RegistryEquals(allowed, registry)))
        {
            return null;
        }

        return $"{registry} is not on your organization's approved registry list (WSLContainerRegistryAllowlist). " +
            $"Approved: {string.Join(", ", policy.RegistryAllowlist.Registries)}.";
    }

    /// <summary>
    /// True when <paramref name="imageReference"/> is an image ID (12 to 64 hex characters, optionally
    /// <c>sha256:</c>-prefixed) rather than a name. Compose runs services by the ID of an image it
    /// already pulled, and a local ID names no registry, so the allowlist (which governs fetching
    /// from registries) does not apply to it when the image is present.
    /// </summary>
    public static bool IsImageId(string? imageReference)
    {
        var text = (imageReference ?? string.Empty).Trim();
        var prefixed = text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase);
        if (prefixed)
            text = text[7..];
        return (prefixed ? text.Length == 64 : text.Length is >= 12 and <= 64) &&
               text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// Validates build before the app uses it for an external operation.
    /// </summary>
    public static string? ValidateBuild(WslPolicySnapshot policy) =>
        policy.RegistryAllowlist.IsConfigured || policy.RegistryAllowlist.State == WslRegistryAllowlistState.Invalid
            ? BuildBlockedMessage
            : null;

    private static bool RegistryEquals(string allowed, string actual)
    {
        var normalizedAllowed = allowed.Trim().TrimEnd('/').ToLowerInvariant();
        var normalizedActual = actual.Trim().TrimEnd('/').ToLowerInvariant();
        if (string.Equals(normalizedAllowed, normalizedActual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Docker Hub may be named differently by client, registry and engine paths. Treat the
        // known endpoints as equivalent so policy intent stays Docker-Hub scoped rather than
        // depending on which alias a command surfaced.
        return IsDockerHubHost(normalizedAllowed) && IsDockerHubHost(normalizedActual);
    }

    private static bool IsDockerHubHost(string host) =>
        host is "docker.io" or "index.docker.io" or "registry-1.docker.io";
}
