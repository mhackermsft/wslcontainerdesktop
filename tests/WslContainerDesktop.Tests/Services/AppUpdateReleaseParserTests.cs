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
using System.Text.Json.Nodes;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AppUpdateReleaseParserTests
{
    private const string Repo = "mhackermsft/wslcontainerdesktop";
    private const string Digest = "sha256:ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789";

    private static JsonObject Release(
        string tag = "v1.9.0",
        string assetName = "WSLContainerDesktop_1.9.0_x64.msix",
        string? url = null,
        long size = 123_456_789,
        string? digest = Digest,
        string? state = "uploaded",
        bool draft = false,
        bool prerelease = false)
    {
        var asset = new JsonObject
        {
            ["name"] = assetName,
            ["browser_download_url"] = url ?? $"https://github.com/{Repo}/releases/download/{tag}/{assetName}",
            ["size"] = size,
        };
        if (digest is not null) asset["digest"] = digest;
        if (state is not null) asset["state"] = state;

        return new JsonObject
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["html_url"] = $"https://github.com/{Repo}/releases/tag/{tag}",
            ["assets"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "WSLContainerDesktop-Signing.cer",
                    ["browser_download_url"] = $"https://github.com/{Repo}/releases/download/{tag}/WSLContainerDesktop-Signing.cer",
                    ["size"] = 800,
                },
                asset,
            },
        };
    }

    private static WslContainerDesktop.Models.AppUpdateRelease? Parse(JsonObject release, string arch = "x64") =>
        AppUpdateReleaseParser.Parse(release.ToJsonString(), Repo, arch);

    [Fact]
    public void ParsesThePublishedMsix()
    {
        var release = Parse(Release());

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 9, 0, 0), release.Version);
        Assert.Equal("v1.9.0", release.Tag);
        Assert.Equal("1.9.0", release.DisplayVersion);
        Assert.Equal("WSLContainerDesktop_1.9.0_x64.msix", release.AssetName);
        Assert.Equal(new Uri($"https://github.com/{Repo}/releases/download/v1.9.0/WSLContainerDesktop_1.9.0_x64.msix"), release.AssetDownloadUrl);
        Assert.Equal(123_456_789, release.AssetSize);
        Assert.Equal(Digest["sha256:".Length..].ToLowerInvariant(), release.AssetSha256);
        Assert.Equal(new Uri($"https://github.com/{Repo}/releases/tag/v1.9.0"), release.ReleasePage);
    }

    [Fact]
    public void IgnoresDraftsAndPrereleases()
    {
        Assert.Null(Parse(Release(draft: true)));
        Assert.Null(Parse(Release(prerelease: true)));
    }

    [Theory]
    [InlineData("1.9.0")]
    [InlineData("v1.9")]
    [InlineData("v1.9.0-beta")]
    [InlineData("v1.9.0.1")]
    [InlineData("v70000.0.0")]
    public void RejectsTagsTheReleaseWorkflowNeverCreates(string tag) =>
        Assert.Null(Parse(Release(tag: tag, assetName: "WSLContainerDesktop_1.9.0_x64.msix")));

    [Fact]
    public void RequiresAnAssetForThisArchitecture()
    {
        Assert.Null(Parse(Release(), arch: "arm64"));
        Assert.Null(Parse(Release(assetName: "WSLContainerDesktop_1.8.0_x64.msix")));
    }

    [Theory]
    [InlineData("https://evil.example.com/mhackermsft/wslcontainerdesktop/releases/download/v1.9.0/WSLContainerDesktop_1.9.0_x64.msix")]
    [InlineData("https://github.com/someone-else/wslcontainerdesktop/releases/download/v1.9.0/WSLContainerDesktop_1.9.0_x64.msix")]
    [InlineData("http://github.com/mhackermsft/wslcontainerdesktop/releases/download/v1.9.0/WSLContainerDesktop_1.9.0_x64.msix")]
    [InlineData("https://github.com/mhackermsft/wslcontainerdesktop/releases/download/v1.8.0/WSLContainerDesktop_1.9.0_x64.msix")]
    [InlineData("not a url")]
    public void RejectsDownloadsOutsideThisReleasesAssets(string url) =>
        Assert.Null(Parse(Release(url: url)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2L * 1024 * 1024 * 1024)]
    public void RejectsImplausibleSizes(long size) =>
        Assert.Null(Parse(Release(size: size)));

    [Fact]
    public void RejectsAnAssetThatIsStillUploading() =>
        Assert.Null(Parse(Release(state: "starter")));

    [Fact]
    public void ToleratesAMissingStateOrDigest()
    {
        var release = Parse(Release(state: null, digest: null));
        Assert.NotNull(release);
        Assert.Null(release.AssetSha256);
    }

    [Fact]
    public void IgnoresADigestThatIsNotSha256()
    {
        var release = Parse(Release(digest: "md5:0123456789abcdef0123456789abcdef"));
        Assert.NotNull(release);
        Assert.Null(release.AssetSha256);
    }

    [Fact]
    public void FallsBackToTheTagPageWhenHtmlUrlIsElsewhere()
    {
        var json = Release();
        json["html_url"] = "https://evil.example.com/release";
        Assert.Equal(new Uri($"https://github.com/{Repo}/releases/tag/v1.9.0"), Parse(json)!.ReleasePage);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"v1.9.0\"}")]
    [InlineData("{\"tag_name\":\"v1.9.0\",\"assets\":{}}")]
    public void ReturnsNullForIncompleteReleases(string json) =>
        Assert.Null(AppUpdateReleaseParser.Parse(json, Repo, "x64"));

    [Fact]
    public void ThrowsJsonExceptionForNonJson() =>
        Assert.ThrowsAny<JsonException>(() => AppUpdateReleaseParser.Parse("<html>", Repo, "x64"));

    [Theory]
    [InlineData("1.9.0.0", "1.8.0.0", true)]
    [InlineData("1.8.1.0", "1.8.0.0", true)]
    [InlineData("2.0.0.0", "1.99.99.0", true)]
    [InlineData("1.8.0.0", "1.8.0.0", false)]
    [InlineData("1.7.9.0", "1.8.0.0", false)]
    [InlineData("1.8.0", "1.8.0.0", false)]
    [InlineData("1.8.0.0", "1.8", false)]
    public void ComparesPackageVersions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, AppUpdateReleaseParser.IsNewer(Version.Parse(candidate), Version.Parse(current)));
}
