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

/// <summary>Covers the Networks page's "Used by" column, which reads each container's own inspect output.</summary>
public sealed class NetworkUsageResolverTests
{
    private static string Inspect(params string[] networks) =>
        "[{\"Id\":\"x\",\"NetworkSettings\":{\"Networks\":{" +
        string.Join(",", networks.Select(n => $"\"{n}\":{{}}")) + "}}}]";

    private static CommandResult Ok(string output) => new() { ExitCode = 0, StandardOutput = output };

    private static CommandResult Failed() => new() { ExitCode = 1, StandardError = "Object not found" };

    [Fact]
    public async Task ListsRunningAndStoppedContainersPerNetwork()
    {
        var networks = new[] { new NetworkInfo { Name = "demo-net" }, new NetworkInfo { Name = "bridge" }, new NetworkInfo { Name = "empty" } };
        var containers = new[]
        {
            new ContainerInfo { Id = "a", Name = "web" },
            new ContainerInfo { Id = "b", Name = "db" },
            new ContainerInfo { Id = "c", Name = "migrate" },
        };
        var outputs = new Dictionary<string, string>
        {
            ["a"] = Inspect("demo-net"),
            ["b"] = Inspect("demo-net", "bridge"),
            ["c"] = Inspect("bridge"),
        };

        var warnings = await NetworkUsageResolver.ResolveAsync(networks, containers,
            (id, _) => Task.FromResult(Ok(outputs[id])));

        Assert.Empty(warnings);
        Assert.Equal("db, web", networks[0].UsedByDisplay);
        Assert.Equal("db, migrate", networks[1].UsedByDisplay);
        Assert.Equal("Not in use", networks[2].UsedByDisplay);
    }

    [Fact]
    public async Task FailedInspectMakesUsageUnknownRatherThanUnused()
    {
        var networks = new[] { new NetworkInfo { Name = "demo-net" }, new NetworkInfo { Name = "empty" } };
        var containers = new[]
        {
            new ContainerInfo { Id = "a", Name = "web" },
            new ContainerInfo { Id = "b", Name = "gone" },
        };

        var warnings = await NetworkUsageResolver.ResolveAsync(networks, containers,
            (id, _) => Task.FromResult(id == "a" ? Ok(Inspect("demo-net")) : Failed()));

        Assert.Single(warnings);
        Assert.Equal("web (others unknown)", networks[0].UsedByDisplay);
        Assert.Equal("Unknown", networks[1].UsedByDisplay);
    }

    [Fact]
    public void UnresolvedNetworkShowsUnknown()
    {
        Assert.Equal("Unknown", new NetworkInfo { Name = "demo-net" }.UsedByDisplay);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[{\"Id\":\"x\"}]")]
    [InlineData("[{\"Id\":\"x\",\"NetworkSettings\":{\"Networks\":null}}]")]
    public void TryParseNetworkNames_RejectsOutputWithoutANetworkMap(string json)
    {
        Assert.False(NetworkUsageResolver.TryParseNetworkNames(json, out var names));
        Assert.Empty(names);
    }
}
