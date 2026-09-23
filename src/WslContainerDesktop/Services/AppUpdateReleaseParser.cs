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
using System.Text.Json;
using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads the GitHub "latest release" API response and extracts the MSIX the updater may install.
/// Everything here is deliberately strict: a release that does not look exactly like one the
/// release workflow publishes is ignored rather than guessed at, because the result is fed to the
/// package installer.
/// </summary>
public static partial class AppUpdateReleaseParser
{
    [GeneratedRegex(@"^v(?<major>\d{1,5})\.(?<minor>\d{1,5})\.(?<patch>\d{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^sha256:(?<hex>[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    /// <summary>The asset name the release workflow gives the MSIX for a version and architecture.</summary>
    public static string ExpectedAssetName(Version version, string architecture) =>
        string.Create(CultureInfo.InvariantCulture, $"WSLContainerDesktop_{version.Major}.{version.Minor}.{version.Build}_{architecture}.msix");

    /// <summary>
    /// Parses a <c>GET /repos/{owner}/{repo}/releases/latest</c> body. Returns null when the release
    /// is a draft or pre-release, its tag is not <c>vX.Y.Z</c>, or it has no MSIX for
    /// <paramref name="architecture"/> hosted on this repository's release downloads.
    /// </summary>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    public static AppUpdateRelease? Parse(string json, string repository, string architecture)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (GetBool(root, "draft") || GetBool(root, "prerelease"))
        {
            return null;
        }

        var tag = GetString(root, "tag_name");
        var version = tag is null ? null : ParseTag(tag);
        if (tag is null || version is null)
        {
            return null;
        }

        var releasesBase = $"https://github.com/{repository}/releases/";
        var expectedName = ExpectedAssetName(version, architecture);
        var expectedUrl = new Uri($"{releasesBase}download/{tag}/{expectedName}");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(GetString(asset, "name"), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Assets still uploading report a different state; only a finished upload is installable.
            var state = GetString(asset, "state");
            if (state is not null && !string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!Uri.TryCreate(GetString(asset, "browser_download_url"), UriKind.Absolute, out var download)
                || !string.Equals(download.AbsoluteUri, expectedUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
            if (size <= 0 || size > AppConstants.UpdateMaxPackageBytes)
            {
                return null;
            }

            var sha256 = GetString(asset, "digest") is { } digest && DigestPattern().Match(digest) is { Success: true } m
                ? m.Groups["hex"].Value.ToLowerInvariant()
                : null;

            var page = Uri.TryCreate(GetString(root, "html_url"), UriKind.Absolute, out var html)
                && html.AbsoluteUri.StartsWith(releasesBase, StringComparison.OrdinalIgnoreCase)
                    ? html
                    : new Uri($"{releasesBase}tag/{tag}");

            return new AppUpdateRelease
            {
                Version = version,
                Tag = tag,
                ReleasePage = page,
                AssetName = expectedName,
                AssetDownloadUrl = download,
                AssetSize = size,
                AssetSha256 = sha256,
            };
        }

        return null;
    }

    /// <summary>Parses a <c>vX.Y.Z</c> tag into the four-part package version <c>X.Y.Z.0</c>.</summary>
    public static Version? ParseTag(string tag)
    {
        var match = TagPattern().Match(tag);
        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
        var patch = int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture);

        // Package version parts are 16-bit.
        return major > ushort.MaxValue || minor > ushort.MaxValue || patch > ushort.MaxValue
            ? null
            : new Version(major, minor, patch, 0);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
