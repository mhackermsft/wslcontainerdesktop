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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.Tray;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class PortsSessionTests
{
    [Fact]
    public async Task ExistingEndpointsPageShowsBothSessionsAndUpdatesOnlyManagedSession()
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var context = new WslcSessionContext();
        context.Select(first.Name);
        var monitor = new StatusMonitor();
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = [first, second] },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    [Container(session == first ? 8001 : 8002)]),
                nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                _ => throw new InvalidOperationException(method.Name),
            });
        var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session), () => "engine",
            NullLogger<WslcSessionInventoryReader>.Instance);
        using var inventory = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, context);
        var containerActions = new WslcSessionContainerActions(discovery, context, (session, _, _) => Client(session),
            () => "engine", () => "default", (_, _) => { }, () => { });
        var resources = new WslcSessionResourceActions(discovery, context, (session, _, _) => Client(session),
            () => "engine", () => "default", () => { });
        var ai = NetworkTestProxy.Create<IAiCapabilityService>((method, _) => throw new InvalidOperationException(method.Name));
        using var actions = new CombinedSessionActionsViewModel(containerActions, resources, inventory, new DialogService(), ai,
            NullLogger<CombinedSessionActionsViewModel>.Instance, context);
        var viewModel = new PortsViewModel(monitor, inventory, actions, context, NullLogger<PortsViewModel>.Instance);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["first", "second"], viewModel.Endpoints.Select(row => row.SessionName));
        Assert.All(viewModel.Endpoints, row => Assert.True(row.IsHttp));
        Assert.Equal("first", context.SelectedSessionName);
        viewModel.Grid.SearchText = "first";
        Assert.Equal("first", Assert.Single(viewModel.Grid.Items).SessionName);
        Assert.Equal(2, viewModel.Endpoints.Count);
        var originalSecond = viewModel.Endpoints.Single(row => row.SessionName == "second");
        monitor.Publish(new EngineStatusSnapshot
        {
            Health = EngineHealth.Healthy, Session = first, ExecutablePath = "engine",
            Containers = [Container(9001)],
        });
        Assert.Equal(9001, viewModel.Endpoints.Single(row => row.SessionName == "first").HostPort);
        Assert.Equal(8002, viewModel.Endpoints.Single(row => row.SessionName == "second").HostPort);
        Assert.True(inventory.IsCurrentRow(viewModel.Endpoints.Single(row => row.SessionName == "second").Source));
        Assert.Equal("second", originalSecond.SessionName);
        Assert.Equal(9001, Assert.Single(viewModel.Grid.Items).HostPort);

        monitor.Publish(new EngineStatusSnapshot
        {
            Health = EngineHealth.Healthy, Session = first with { ID = 3, CreatorPid = 300 },
            ExecutablePath = "engine", Containers = [Container(9002)],
        });

        Assert.Equal("second", Assert.Single(viewModel.Endpoints).SessionName);
        Assert.Contains("changed", viewModel.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    private static ContainerInfo Container(int port) => new()
    {
        Id = "123456789abc", Name = "web", Image = "example", CreatedAt = 123, CreatedAtKnown = true,
        StateValue = (int)ContainerState.Running, PortsKnown = true,
        Ports = [new PortMapping { HostPort = port, ContainerPort = 80, Protocol = 6 }],
    };
}
