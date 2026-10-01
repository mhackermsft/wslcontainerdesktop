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

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// k3s runs in the pinned distribution, or WSL's default one. That distribution must exist, be
/// WSL 2, and not belong to another tool, or Kubernetes Install is unavailable with a reason.
/// </summary>
public sealed class KubernetesHostCheckTests
{
    private static readonly WslDistroRecord Ubuntu = new("Ubuntu", IsWsl2: true, IsDefault: true);
    private static readonly WslDistroRecord Debian = new("Debian", IsWsl2: true, IsDefault: false);

    [Fact]
    public void UsesTheDefaultDistributionWhenNothingIsPinned()
    {
        var host = KubernetesHostCheck.Resolve([Debian, Ubuntu], pinnedDistro: null);

        Assert.True(host.CanHost);
        Assert.Equal("Ubuntu", host.DistroName);
    }

    [Fact]
    public void UsesThePinnedDistributionEvenWhenItIsNotTheDefault()
    {
        var host = KubernetesHostCheck.Resolve([Debian, Ubuntu], pinnedDistro: " debian ");

        Assert.True(host.CanHost);
        Assert.Equal("Debian", host.DistroName);
    }

    [Fact]
    public void NoDistributions()
    {
        Assert.Equal(KubernetesHostProblem.NoDistributions, KubernetesHostCheck.Resolve([], null).Problem);
    }

    [Fact]
    public void PinnedDistributionMissing()
    {
        var host = KubernetesHostCheck.Resolve([Ubuntu], "Fedora");

        Assert.Equal(KubernetesHostProblem.PinnedMissing, host.Problem);
        Assert.Equal("Fedora", host.DistroName);
    }

    [Fact]
    public void DistributionsButNoDefault()
    {
        Assert.Equal(KubernetesHostProblem.NoDefault, KubernetesHostCheck.Resolve([Debian], null).Problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Legacy")]
    public void Wsl1CannotHostK3s(string? pinned)
    {
        var host = KubernetesHostCheck.Resolve([new WslDistroRecord("Legacy", IsWsl2: false, IsDefault: true)], pinned);

        Assert.Equal(KubernetesHostProblem.Wsl1, host.Problem);
        Assert.Equal("Legacy", host.DistroName);
    }

    [Theory]
    [InlineData("docker-desktop")]
    [InlineData("Docker-Desktop-Data")]
    [InlineData("rancher-desktop")]
    [InlineData("podman-machine-default")]
    public void ToolManagedDefaultDistributionCannotHostK3s(string name)
    {
        var host = KubernetesHostCheck.Resolve([new WslDistroRecord(name, IsWsl2: true, IsDefault: true), Debian], null);

        Assert.Equal(KubernetesHostProblem.ManagedByTool, host.Problem);
        Assert.Equal(name, host.DistroName);
    }

    [Fact]
    public void ExplanationOffersTheRightWayOut()
    {
        var unpinned = KubernetesHostCheck.Explain(KubernetesHostProblem.Wsl1, "Legacy", pinned: false);
        var pinned = KubernetesHostCheck.Explain(KubernetesHostProblem.Wsl1, "Legacy", pinned: true);

        Assert.Contains("wsl --set-version Legacy 2", unpinned);
        Assert.Contains("wsl --set-default", unpinned);
        Assert.Contains("default distribution instead", pinned);
        Assert.All(
            Enum.GetValues<KubernetesHostProblem>(),
            p => Assert.Contains("Containers aren't affected", KubernetesHostCheck.Explain(p, "X", pinned: false)));
    }
}
