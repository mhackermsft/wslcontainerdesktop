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
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class VolumesCombinedSessionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CreateAndPruneUseTheChosenSessionAndRejectChangedTargets(bool network, bool prune, bool changed)
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var context = new WslcSessionContext();
        context.Select(first.Name);
        var calls = new List<string>();
        var verification = 0;
        var dialogs = new DialogService();
        dialogs.DismissMessage.SetResult();
        dialogs.OnShowDialog = dialog =>
        {
            if (dialog is SimpleInputDialog input) input.Value = "data";
            if (dialog is CreateNetworkDialog create) create.NetworkName = "net";
            return ContentDialogResult.Primary;
        };
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = [first, second] },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            switch (method.Name)
            {
                case nameof(IWslcService.ListVolumesAsync): return Task.FromResult<IReadOnlyList<VolumeInfo>>([]);
                case nameof(IWslcService.ListNetworksAsync): return Task.FromResult<IReadOnlyList<NetworkInfo>>([]);
                case nameof(IWslcService.ListContainersAsync): return Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
                case nameof(IWslcService.ListImagesAsync): return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
                case nameof(IWslcService.CreateVolumeAsync):
                case nameof(IWslcService.CreateNetworkAsync):
                case nameof(IWslcService.PruneVolumesAsync):
                case nameof(IWslcService.PruneNetworksAsync):
                    calls.Add($"{session.Name}:{method.Name}");
                    return Task.FromResult(new CommandResult());
                default: throw new InvalidOperationException(method.Name);
            }
        });
        var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session), () => "engine",
            NullLogger<WslcSessionInventoryReader>.Instance);
        using var inventory = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, context);
        var actions = new WslcSessionResourceActions(discovery, context, (session, _, _) => Client(session),
            () => "engine", () => "default", () => { });
        var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, _) => method.Name switch
        {
            nameof(IWslcSessionSelectionService.ChooseAsync) or nameof(IWslcSessionSelectionService.ChooseForLaterAsync) or
            nameof(IWslcSessionSelectionService.ChooseActiveAsync) =>
                Task.FromResult<WslcSessionSelection?>(new(new("second"), "engine", second, "second")),
            nameof(IWslcSessionSelectionService.VerifyAsync) => changed && ++verification > 1
                ? Task.FromException(new InvalidOperationException("Session changed"))
                : Task.CompletedTask,
            nameof(IWslcSessionSelectionService.CreateClient) => Client(second),
            _ => throw new InvalidOperationException(method.Name),
        });
        var volumes = new VolumesViewModel(discovery, dialogs, NullLogger<VolumesViewModel>.Instance,
            context, inventory, actions, (session, _) => Client(session), selector);
        var networks = new NetworksViewModel(discovery, dialogs, context, inventory, actions,
            (session, _) => Client(session), sessionSelection: selector);

        if (network && prune) await networks.PruneCommand.ExecuteAsync(null);
        else if (network) await networks.CreateCommand.ExecuteAsync(null);
        else if (prune) await volumes.PruneCommand.ExecuteAsync(null);
        else await volumes.CreateCommand.ExecuteAsync(null);

        if (changed) Assert.Empty(calls);
        else Assert.Equal($"second:{(network ? prune ? nameof(IWslcService.PruneNetworksAsync) : nameof(IWslcService.CreateNetworkAsync)
            : prune ? nameof(IWslcService.PruneVolumesAsync) : nameof(IWslcService.CreateVolumeAsync))}", Assert.Single(calls));
        if (prune) Assert.Contains("second", dialogs.LastConfirmation);
        Assert.Equal("first", context.SelectedSessionName);
    }

    [Fact]
    public async Task FailedContainerInventoryNeverMakesVolumeLookUnused()
    {
        var session = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var context = new WslcSessionContext();
        context.Select(session.Name);
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = [session] },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        var client = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>(
                [new VolumeInfo { Name = "data", Driver = "local" }]),
            nameof(IWslcService.ListContainersAsync) => throw new IOException("Container list failed."),
            nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
            nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
            nameof(IWslcService.InspectVolumeAsync) => Task.FromResult(new CommandResult
            {
                StandardOutput = """[{"Name":"data","CreatedAt":"2026-10-25T10:00:00Z"}]""",
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => "engine",
            NullLogger<WslcSessionInventoryReader>.Instance);
        using var inventory = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, context);
        var actions = new WslcSessionResourceActions(discovery, context, (_, _, _) => client,
            () => "engine", () => "default", () => { });
        var viewModel = new VolumesViewModel(discovery, new DialogService(), NullLogger<VolumesViewModel>.Instance,
            context, inventory, actions, (_, _) => client);

        await viewModel.RefreshAsync();

        Assert.Equal(VolumeUsageState.Unknown, Assert.Single(viewModel.Volumes).UsageState);
        Assert.Contains("incomplete", viewModel.CombinedWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNamedVolumesRemainSeparateAndRemovalTargetsTheirOwners(bool bulk)
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
                case nameof(IWslcService.ListVolumesAsync):
                    return Task.FromResult<IReadOnlyList<VolumeInfo>>(existing[session.Name]
                        ? [new VolumeInfo { Name = "data", Driver = "local" }] : []);
                case nameof(IWslcService.ListContainersAsync):
                    return Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
                case nameof(IWslcService.ListImagesAsync):
                    return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
                case nameof(IWslcService.ListNetworksAsync):
                    return Task.FromResult<IReadOnlyList<NetworkInfo>>([]);
                case nameof(IWslcService.InspectVolumeAsync):
                    return Task.FromResult(new CommandResult
                    {
                        StandardOutput = """[{"Name":"data","CreatedAt":"2026-10-25T10:00:00Z"}]""",
                    });
                case nameof(IWslcService.RemoveVolumeAsync):
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
        var viewModel = new VolumesViewModel(discovery, dialogs, NullLogger<VolumesViewModel>.Instance,
            context, inventory, actions, (session, _) => Client(session));

        await viewModel.RefreshAsync();

        Assert.Equal(["first", "second"], viewModel.Volumes.Select(volume => volume.SessionName));
        Assert.All(viewModel.Volumes, volume => Assert.Equal("data", volume.Name));
        Assert.All(viewModel.Volumes, volume => Assert.NotNull(volume.CreatedAt));
        viewModel.Grid.SearchText = "second";
        Assert.Equal("second", Assert.Single(viewModel.Grid.Items).SessionName);
        Assert.Equal(2, viewModel.Volumes.Count);
        viewModel.Grid.SearchText = string.Empty;
        viewModel.Grid.SortBy("session");
        viewModel.Grid.SortBy("session");
        Assert.Equal(["second", "first"], viewModel.Grid.Items.Select(volume => volume.SessionName));
        if (bulk)
            await viewModel.BulkRemoveAsync(viewModel.Volumes.ToArray());
        else
            await viewModel.RemoveCommand.ExecuteAsync(viewModel.Volumes.Single(volume => volume.SessionName == "second"));

        Assert.Equal(bulk ? ["first:data", "second:data"] : ["second:data"], mutations);
        Assert.Equal(!bulk, existing["first"]);
        Assert.False(existing["second"]);
        Assert.Equal("first", context.SelectedSessionName);
        if (bulk)
            Assert.Empty(viewModel.Volumes);
        else
            Assert.Equal("first", Assert.Single(viewModel.Volumes).SessionName);
    }
}
