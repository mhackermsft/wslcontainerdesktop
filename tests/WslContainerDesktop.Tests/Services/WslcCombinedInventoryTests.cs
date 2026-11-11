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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcCombinedInventoryTests
{
    private static readonly WslcSessionInfo First = new() { ID = 1, CreatorPid = 100, Name = "first" };
    private static readonly WslcSessionInfo Second = new() { ID = 2, CreatorPid = 200, Name = "second" };

    [Fact]
    public void IdenticalResourceIdentitiesStaySeparateAcrossSessions()
    {
        var combined = WslcCombinedInventory.Create([Inventory(First), Inventory(Second)]);

        Assert.True(combined.IsComplete);
        Assert.Equal(["first", "second"], combined.Containers.Select(row => row.Session.Name));
        Assert.Equal(["same", "same"], combined.Containers.Select(row => row.Value.Id));
        Assert.Equal(["same", "same"], combined.Images.Select(row => row.Value.Id));
        Assert.Equal(["data", "data"], combined.Volumes.Select(row => row.Value.Name));
        Assert.Equal(["net", "net"], combined.Networks.Select(row => row.Value.Name));
        Assert.Equal(["first", "second"], combined.Endpoints.Select(row => row.Session.Name));
    }

    [Fact]
    public void FailedAndChangedSessionsAreNotCountedAsEmptySuccessfulInventories()
    {
        var failed = Inventory(First) with { Images = new(null, "unavailable") };
        var changed = Inventory(Second) with { SessionChanged = true };

        var combined = WslcCombinedInventory.Create([failed, changed]);

        Assert.False(combined.IsComplete);
        Assert.False(combined.EndpointsComplete);
        Assert.Single(combined.Containers);
        Assert.Empty(combined.Images);
        Assert.Equal(2, combined.Failures.Count);
        Assert.Contains(combined.Failures, f => f.Session == First && f.Kind == WslcCombinedInventory.FailureKind.Images);
        Assert.Contains(combined.Failures, f => f.Session == Second && f.Kind == WslcCombinedInventory.FailureKind.Session);
    }

    [Fact]
    public void UnknownPortsAreNotReportedAsAnEmptyCompleteEndpointList()
    {
        var inventory = Inventory(First);
        inventory.Containers.Items![0].PortsKnown = false;

        var combined = WslcCombinedInventory.Create([inventory]);

        Assert.True(combined.IsComplete);
        Assert.False(combined.EndpointsComplete);
        Assert.Empty(combined.Endpoints);
    }

    private static WslcSessionInventory Inventory(WslcSessionInfo session) => new(
        session,
        new([new ContainerInfo
        {
            Id = "same", Name = "web", StateValue = (int)ContainerState.Running, PortsKnown = true,
            Ports = [new PortMapping { HostPort = 8080, ContainerPort = 80, Protocol = 6 }],
        }]),
        new([new ImageInfo { Id = "same", Repository = "example", Tag = "latest" }]),
        new([new VolumeInfo { Name = "data" }]),
        new([new NetworkInfo { Name = "net" }]));
}
