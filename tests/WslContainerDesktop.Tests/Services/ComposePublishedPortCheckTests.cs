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
/// The Compose review blocks an apply when a published host port is already in use. A service's own
/// running container must not count: <c>wslc list</c> reports 12-character short IDs while the plan
/// carries the full ID from <c>inspect</c>, and comparing them exactly blocked every re-apply of a
/// project that publishes a port (seen with WordPress on 8082).
/// </summary>
public sealed class ComposePublishedPortCheckTests
{
    private const string FullId = "97ddb1f9e1067f607427b1625b680083c585138a10aa2cb78b79763b0dc97cc3";

    private static ContainerInfo Running(string id, int hostPort) => new()
    {
        Id = id,
        Name = "c" + id[..4],
        StateValue = (int)ContainerState.Running,
        PortsKnown = true,
        Ports = [new PortMapping { HostPort = hostPort, ContainerPort = 80, Protocol = 6 }],
    };

    private static ComposeReconciliationPlan Plan(string? containerId)
    {
        var service = new ComposeService { Name = "wordpress", Options = new RunContainerOptions { Image = "wordpress", PortMappings = { "8082:80" } } };
        return new([new ComposeServicePlan(service, "wordpress_wordpress", "hash", ComposeServiceChange.Unchanged,
            ComposeServiceAction.Keep, "unchanged", containerId)]);
    }

    [Fact]
    public void OwnContainerListedWithShortId_IsNotAConflict() =>
        Assert.Null(ComposeProjectSupervisor.ValidatePublishedPorts(Plan(FullId), [Running(FullId[..12], 8082)]));

    [Fact]
    public void OwnContainerWithIdenticalId_IsNotAConflict() =>
        Assert.Null(ComposeProjectSupervisor.ValidatePublishedPorts(Plan(FullId), [Running(FullId, 8082)]));

    [Fact]
    public void AnotherContainerOnTheSamePort_IsStillAConflict() =>
        Assert.NotNull(ComposeProjectSupervisor.ValidatePublishedPorts(Plan(FullId), [Running("0123456789abcdef", 8082)]));

    [Fact]
    public void NewInstanceWithoutAContainer_ConflictsWithAnyUserOfThePort() =>
        Assert.NotNull(ComposeProjectSupervisor.ValidatePublishedPorts(Plan(null), [Running(FullId[..12], 8082)]));
}
