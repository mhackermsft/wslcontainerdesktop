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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class CombinedSessionsIntegrationTests
{
    [Fact]
    public async Task SameNamedResourcesRemainSeparateAndOnlyTheChosenSessionIsMutated()
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var sessions = new[] { first, second };
        var running = new Dictionary<string, bool> { ["first"] = true, ["second"] = true };
        var mutations = new List<string>();
        var managedCallbacks = 0;
        var context = new WslcSessionContext();
        context.Select("first");
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = sessions },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });

        IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IWslcService.ListContainersAsync):
                    return Task.FromResult<IReadOnlyList<ContainerInfo>>([new ContainerInfo
                    {
                        Id = "123456789abc", Name = "web", Image = "example", CreatedAt = 123,
                        CreatedAtKnown = true, StateValue = running[session.Name] ? 2 : 3, PortsKnown = true,
                    }]);
                case nameof(IWslcService.ListImagesAsync):
                    return Task.FromResult<IReadOnlyList<ImageInfo>>([new ImageInfo { Id = "same", Repository = "example", Tag = "latest" }]);
                case nameof(IWslcService.ListVolumesAsync):
                    return Task.FromResult<IReadOnlyList<VolumeInfo>>([new VolumeInfo { Name = "data" }]);
                case nameof(IWslcService.ListNetworksAsync):
                    return Task.FromResult<IReadOnlyList<NetworkInfo>>([new NetworkInfo { Id = "net-id", Name = "app" }]);
                case nameof(IWslcService.StopContainerAsync):
                    mutations.Add($"{session.Name}:{args[0]}");
                    running[session.Name] = false;
                    return Task.FromResult(new CommandResult());
                default:
                    throw new InvalidOperationException(method.Name);
            }
        });

        var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session), () => "engine",
            NullLogger<WslcSessionInventoryReader>.Instance);
        var snapshots = await reader.ReadAsync(sessions);
        var combined = WslcCombinedInventory.Create(snapshots);
        Assert.Equal(2, combined.Containers.Count);
        Assert.Equal(2, combined.Images.Count);
        Assert.Equal(2, combined.Volumes.Count);

        var selected = Assert.Single(combined.Containers, row => row.Session == second);
        var actions = new WslcSessionContainerActions(discovery, context,
            (session, _, managed) =>
            {
                Assert.True(managed);
                return Client(session);
            }, () => "engine", () => "wslc-cli-user",
            (_, _) => managedCallbacks++, () => managedCallbacks++);
        var outcome = await actions.ExecuteAsync(
            WslcContainerActionTarget.Capture(selected.Session, selected.Value), WslcContainerAction.Stop);

        Assert.True(outcome.Success);
        Assert.True(running["first"]);
        Assert.False(running["second"]);
        Assert.Equal(["second:123456789abc"], mutations);
        Assert.Equal(0, managedCallbacks);
        Assert.Equal("first", context.SelectedSessionName);

        var refreshed = WslcCombinedInventory.Create(await reader.ReadAsync([second]));
        Assert.Equal(ContainerState.Stopped, Assert.Single(refreshed.Containers).Value.State);
        Assert.True(running["first"]);
    }
}
