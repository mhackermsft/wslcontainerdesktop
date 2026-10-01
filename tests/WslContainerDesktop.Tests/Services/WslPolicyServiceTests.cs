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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Covers Windows WSL policy parsing and registry allowlist checks that gate image references and builds.
/// </summary>
public sealed class WslPolicyServiceTests
{
    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData(1, 1, true, true)]
    [InlineData(2, 5, true, true)]
    [InlineData(0, 1, false, true)]
    [InlineData(1, 0, true, false)]
    public void PolicyDwords_DisableOnlyOnZero(int? allowWsl, int? allowContainer, bool expectedWsl, bool expectedContainer)
    {
        var policy = Service(allowWsl, allowContainer).GetPolicy();

        Assert.Equal(expectedWsl, policy.AllowWsl);
        Assert.Equal(expectedContainer, policy.AllowWslContainers);
    }

    [Fact]
    public void Allowlist_AbsentOrEmpty_IsUnrestricted()
    {
        Assert.Equal(WslRegistryAllowlistState.Unrestricted, Service(allowlist: null).GetPolicy().RegistryAllowlist.State);
        Assert.Equal(WslRegistryAllowlistState.Unrestricted, Service(allowlist: ["", "  "]).GetPolicy().RegistryAllowlist.State);
    }

    [Fact]
    public void Allowlist_Values_AreTrimmedDeduplicatedAndSorted()
    {
        var allowlist = Service(allowlist: [" ghcr.io ", "DOCKER.io", "ghcr.io"]).GetPolicy().RegistryAllowlist;

        Assert.Equal(WslRegistryAllowlistState.Configured, allowlist.State);
        Assert.Equal(["DOCKER.io", "ghcr.io"], allowlist.Registries);
    }

    [Fact]
    public void Allowlist_ReadFailure_IsInvalid()
    {
        var allowlist = Service(diagnostic: "access denied").GetPolicy().RegistryAllowlist;

        Assert.Equal(WslRegistryAllowlistState.Invalid, allowlist.State);
        Assert.Contains("access denied", allowlist.Diagnostic);
    }

    [Theory]
    [InlineData("nginx:latest", "docker.io")]
    [InlineData("library/nginx", "docker.io")]
    [InlineData("localhost:5000/app:1", "localhost:5000")]
    [InlineData("ghcr.io/owner/app:1", "ghcr.io")]
    [InlineData("example.com/team/app@sha256:abc", "example.com")]
    [InlineData("localhost/app", "localhost")]
    [InlineData("Evil/app", "evil")]
    [InlineData("MyRegistry/team/app:1", "myregistry")]
    public void ExtractRegistryHost_FollowsDockerRules(string reference, string expected) =>
        Assert.Equal(expected, WslRegistryPolicyGuard.ExtractRegistryHost(reference));

    [Theory]
    [InlineData("docker.io", "nginx:latest")]
    [InlineData("index.docker.io", "docker.io/library/nginx")]
    [InlineData("registry-1.docker.io", "index.docker.io/library/nginx")]
    [InlineData("GHCR.IO", "ghcr.io/owner/app:1")]
    public void Allowlist_MatchesCaseInsensitivelyWithDockerHubAliases(string allowed, string reference)
    {
        var snapshot = new WslPolicySnapshot(true, true,
            new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, [allowed]));

        Assert.Null(WslRegistryPolicyGuard.ValidateImageReference(snapshot, reference));
    }

    [Fact]
    public void Allowlist_BlockMessage_ListsApprovedRegistries()
    {
        var snapshot = new WslPolicySnapshot(true, true,
            new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, ["ghcr.io", "mcr.microsoft.com"]));

        var message = WslRegistryPolicyGuard.ValidateImageReference(snapshot, "docker.io/library/nginx");

        Assert.Contains("docker.io is not on your organization's approved registry list", message);
        Assert.Contains("ghcr.io, mcr.microsoft.com", message);
    }

    [Theory]
    [InlineData("4b275f7b1982", true)]
    [InlineData("4b275f7b1982d1e4c5f6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192", true)]
    [InlineData("sha256:4b275f7b1982d1e4c5f6a7b8c9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192", true)]
    [InlineData("sha256:4b275f7b1982", false)]
    [InlineData("4b275f7b198", false)]
    [InlineData("nginx", false)]
    [InlineData("deadbeefcafe:latest", false)]
    [InlineData("ghcr.io/owner/4b275f7b1982", false)]
    [InlineData("4B275F7B1982", false)]
    public void IsImageId_RecognizesOnlyBareImageIds(string reference, bool expected) =>
        Assert.Equal(expected, WslRegistryPolicyGuard.IsImageId(reference));

    [Fact]
    public void Build_IsRefusedWheneverAllowlistConfigured()
    {
        var snapshot = new WslPolicySnapshot(true, true,
            new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, ["ghcr.io"]));

        Assert.Equal(WslRegistryPolicyGuard.BuildBlockedMessage, WslRegistryPolicyGuard.ValidateBuild(snapshot));
    }

    [Theory]
    [InlineData("The container image registry 'docker.io' is blocked by the computer policy.", "approved registry list")]
    [InlineData("The container registry allowlist policy is invalid. Verify values.", "policy is invalid")]
    [InlineData("WSL container is disabled by the computer policy.", "disabled WSL containers")]
    public void CommandErrorText_MapsPolicyErrors(string raw, string expected)
    {
        var friendly = CommandErrorText.Friendly(raw);

        Assert.Contains(expected, friendly);
    }

    private static WslPolicyService Service(
        int? allowWsl = null,
        int? allowContainer = null,
        IReadOnlyList<string>? allowlist = null,
        string? diagnostic = null) =>
        new(new Reader(new(allowWsl, allowContainer, allowlist, diagnostic)));

    /// <summary>
    /// Test reader that returns one synthetic registry policy snapshot.
    /// </summary>
    private sealed class Reader(WslPolicyRegistryData data) : IWslPolicyRegistryReader
    {
        public WslPolicyRegistryData Read() => data;
    }
}
