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
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class NetworksCombinedSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNamedNetworksRemainSeparateAndRemovalTargetsTheirOwners(bool bulk)
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var context = new WslcSessionContext();
        context.Select(first.Name);
        var existing = new Dictionary<string, bool> { ["first"] = true, ["second"] = true };
        var mutations = new List<string>();
        var dialogs = new DialogService();
        dialogs.DismissMessage.SetResult();
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = [first, second] },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IWslcService.ListNetworksAsync):
                    return Task.FromResult<IReadOnlyList<NetworkInfo>>(existing[session.Name]
                        ? [new NetworkInfo { Id = "network-id", Name = "app", Driver = "bridge" }] : []);
                case nameof(IWslcService.ListContainersAsync):
                    return Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
                case nameof(IWslcService.ListImagesAsync):
                    return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
                case nameof(IWslcService.ListVolumesAsync):
                    return Task.FromResult<IReadOnlyList<VolumeInfo>>([]);
                case nameof(IWslcService.RemoveNetworkAsync):
                    mutations.Add($"{session.Name}:{args[0]}");
                    existing[session.Name] = false;
                    return Task.FromResult(new CommandResult());
                default:
                    throw new InvalidOperationException(method.Name);
            }
        });
        var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session), () => "engine",
            NullLogger<WslcSessionInventoryReader>.Instance);
        using var inventory = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, context);
        var actions = new WslcSessionResourceActions(discovery, context, (session, _, _) => Client(session),
            () => "engine", () => "default", () => { });
        var viewModel = new NetworksViewModel(discovery, dialogs, context, inventory, actions, (session, _) => Client(session));

        await viewModel.RefreshAsync();

        var rows = viewModel.Networks.Where(network => network.Name == "app").ToArray();
        Assert.Equal(["first", "second"], rows.Select(network => network.SessionName));
        Assert.All(rows, network => Assert.True(network.UsageComplete));
        viewModel.Grid.SearchText = "second";
        Assert.All(viewModel.Grid.Items, network => Assert.Equal("second", network.SessionName));
        Assert.Single(viewModel.Grid.Items, network => network.Name == "app");
        Assert.Equal(2, viewModel.Networks.Count(network => network.Name == "app"));
        viewModel.Grid.SearchText = string.Empty;
        viewModel.Grid.SortBy("session");
        viewModel.Grid.SortBy("session");
        Assert.Equal(["second", "first"], viewModel.Grid.Items.Where(network => network.Name == "app")
            .Select(network => network.SessionName));
        if (bulk)
            await viewModel.BulkRemoveAsync(rows);
        else
            await viewModel.RemoveCommand.ExecuteAsync(rows.Single(network => network.SessionName == "second"));

        Assert.Equal(bulk ? ["first:app", "second:app"] : ["second:app"], mutations);
        Assert.Equal(!bulk, existing["first"]);
        Assert.False(existing["second"]);
        Assert.Equal("first", context.SelectedSessionName);
        Assert.Equal(bulk ? 0 : 1, viewModel.Networks.Count(network => network.Name == "app"));
    }
}
