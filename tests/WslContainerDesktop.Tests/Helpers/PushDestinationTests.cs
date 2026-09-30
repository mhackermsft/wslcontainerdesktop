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

using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

/// <summary>Covers image push destination parsing and suggestions so registry names, Docker Hub aliases, tags, and validation messages stay predictable.</summary>
public sealed class PushDestinationTests
{
    private static readonly RegistryEntry DockerHub = RegistryEntry.DockerHub();
    private static readonly RegistryEntry Acr = new() { Name = "Company ACR", Host = "contoso.azurecr.io" };

    [Theory]
    [InlineData("ghcr.io/open-webui/open-webui:main", null, "open-webui:main")]
    [InlineData("ghcr.io/open-webui/open-webui:main", "mike", "mike/open-webui:main")]
    [InlineData("nginx:alpine", "mike", "mike/nginx:alpine")]
    [InlineData("docker.io/library/nginx:alpine", null, "nginx:alpine")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:10.0@sha256:abc", "mike", "mike/sdk:10.0")]
    [InlineData("<none>:<none>", "mike", "")]
    public void DockerHubSuggestionUsesTheAccountNotTheSource(string local, string? user, string expected) =>
        Assert.Equal(expected, PushDestination.SuggestName(local, DockerHub, user));

    [Theory]
    [InlineData("ghcr.io/open-webui/open-webui:main", "open-webui/open-webui:main")]
    [InlineData("contoso.azurecr.io/team/app:1.0", "team/app:1.0")]
    [InlineData("library/nginx:alpine", "nginx:alpine")]
    public void OtherRegistrySuggestionKeepsThePathWithoutTheSourceHost(string local, string expected) =>
        Assert.Equal(expected, PushDestination.SuggestName(local, Acr, "ignored"));

    [Fact]
    public void RegistryChoiceDecidesTheDestination()
    {
        Assert.Equal("contoso.azurecr.io/team/app:1.0", PushDestination.Resolve("team/app:1.0", Acr));
        Assert.Equal("mike/app:1.0", PushDestination.Resolve("mike/app:1.0", DockerHub));
        Assert.Equal("docker.io/mike/app:1.0", PushDestination.Display("mike/app:1.0", DockerHub));
    }

    [Theory]
    [InlineData("ghcr.io/open-webui/open-webui:main", "ghcr.io")]
    [InlineData("index.docker.io/mike/app:1", "docker.io")]
    [InlineData("localhost:5000/app:1", "localhost:5000")]
    [InlineData("team/app:1", null)]
    public void DetectsARegistryAddressInTheName(string name, string? expected) =>
        Assert.Equal(expected, PushDestination.HostIn(name));

    [Fact]
    public void FindsConfiguredRegistriesIncludingDockerHubAliases()
    {
        var all = new[] { DockerHub, Acr };
        Assert.Same(Acr, PushDestination.FindRegistry(all, "CONTOSO.azurecr.io"));
        Assert.Same(DockerHub, PushDestination.FindRegistry(all, "docker.io"));
        Assert.Null(PushDestination.FindRegistry(all, "ghcr.io"));
    }

    [Fact]
    public void ValidationExplainsEachProblem()
    {
        Assert.Contains("Enter the name", PushDestination.Validate(" ", Acr, false, null));
        Assert.Contains("ghcr.io", PushDestination.Validate("ghcr.io/x/y:1", Acr, false, null));
        Assert.Contains("mike/app:1.0", PushDestination.Validate("app:1.0", DockerHub, false, "mike"));
        Assert.Contains("yourname/app:1.0", PushDestination.Validate("app:1.0", DockerHub, false, null));
        Assert.Contains("version tag", PushDestination.Validate("team/app", Acr, false, null));
        Assert.Null(PushDestination.Validate("team/app", Acr, true, null));
        Assert.Null(PushDestination.Validate("mike/app:1.0", DockerHub, false, "mike"));
        Assert.Null(PushDestination.Validate("app:1.0", Acr, false, null));
    }

    [Theory]
    [InlineData("localhost:5000/app", null)]
    [InlineData("localhost:5000/app:1.2", "1.2")]
    [InlineData("app:1@sha256:abc", "1")]
    public void ExtractTagIgnoresPortsAndDigests(string reference, string? expected) =>
        Assert.Equal(expected, PushDestination.ExtractTag(reference));
}
