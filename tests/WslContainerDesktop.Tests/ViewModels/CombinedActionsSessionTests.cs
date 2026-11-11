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
public sealed class CombinedActionsSessionTests
{
    [Fact]
    public async Task EngineChangeRejectsPreviouslyCapturedRowBeforeAnyAction()
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();
        var row = Assert.Single(fixture.Inventory.ContainerRows);
        fixture.Calls.Clear();
        Assert.True(fixture.Context.TryApplyIfUnlocked(() => fixture.Executable = "other-engine"));

        await fixture.Actions.ExecuteContainerAsync(row, WslcContainerAction.Start);

        Assert.Empty(fixture.Calls);
        Assert.Contains("Refresh before taking", fixture.Dialogs.LastMessage);
    }

    [Fact]
    public async Task FilteredOutRowCannotBeUsedAfterPickerReturns()
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();
        var row = Assert.Single(fixture.Inventory.ContainerRows);
        fixture.Calls.Clear();
        fixture.Inventory.ClearSelection();

        await fixture.Actions.LogsAsync(row);

        Assert.Empty(fixture.Calls);
        Assert.Contains("Refresh before taking", fixture.Dialogs.LastMessage);
    }

    [Fact]
    public async Task ExecutableMismatchRejectsRowEvenWithoutChangeNotification()
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();
        var row = Assert.Single(fixture.Inventory.ContainerRows);
        fixture.Calls.Clear();
        fixture.Executable = "other-engine";

        await fixture.Actions.LogsAsync(row);

        Assert.Empty(fixture.Calls);
        Assert.Contains("Refresh before taking", fixture.Dialogs.LastMessage);
    }

    [Fact]
    public async Task ChangingManagementSelectionDoesNotRetargetCombinedRow()
    {
        using var fixture = new Fixture();
        await fixture.LoadAsync();
        var row = Assert.Single(fixture.Inventory.ContainerRows);
        fixture.Calls.Clear();
        Assert.True(fixture.Context.TrySelect("another"));

        await fixture.Actions.LogsAsync(row);

        Assert.Contains("logs", fixture.Calls);
        Assert.Equal("target", row.Session.Name);
        Assert.Equal("another", fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task ActionAndFollowupRefreshKeepExecutableAndSelectionFixed()
    {
        using var fixture = new Fixture { BlockStart = true };
        await fixture.LoadAsync();
        var row = Assert.Single(fixture.Inventory.ContainerRows);
        var action = fixture.Actions.ExecuteContainerAsync(row, WslcContainerAction.Start);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => fixture.Executable = "other-engine"));
        fixture.ReleaseStart.SetResult(new CommandResult());
        await action;

        Assert.Contains("start", fixture.Calls);
        Assert.Equal("target", Assert.Single(fixture.Inventory.ContainerRows).Session.Name);
        Assert.True(fixture.Context.TrySelect("another"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WslcSessionInfo _session = new() { ID = 7, CreatorPid = 100, Name = "target" };
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public string Executable { get; set; } = "engine";
        public List<string> Calls { get; } = [];
        public bool BlockStart { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CommandResult> ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CombinedSessionViewModel Inventory { get; }
        public CombinedSessionActionsViewModel Actions { get; }

        public Fixture()
        {
            Context.Select("managed");
            Dialogs.DismissMessage.SetResult();
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => DiscoverAsync(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var client = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => ContainersAsync(),
                nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                nameof(IWslcService.StartContainerAsync) => StartAsync(),
                nameof(IWslcService.GetLogsAsync) => RecordLogs(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => Executable,
                NullLogger<WslcSessionInventoryReader>.Instance);
            Inventory = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, Context);
            var containers = new WslcSessionContainerActions(discovery, Context, (_, _, _) => client,
                () => Executable, () => "wslc-cli-user", (_, _) => { }, () => { });
            var resources = new WslcSessionResourceActions(discovery, Context, (_, _, _) => client,
                () => Executable, () => "wslc-cli-user", () => { });
            var ai = NetworkTestProxy.Create<IAiCapabilityService>((method, _) =>
                throw new InvalidOperationException(method.Name));
            Actions = new CombinedSessionActionsViewModel(containers, resources, Inventory, Dialogs, ai,
                NullLogger<CombinedSessionActionsViewModel>.Instance, Context);
        }

        public async Task LoadAsync()
        {
            await Inventory.DiscoverAsync();
            await Inventory.LoadAsync();
        }

        private Task<WslcSystemInfo> DiscoverAsync()
        {
            Calls.Add("discover");
            return Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [_session] } });
        }

        private Task<IReadOnlyList<ContainerInfo>> ContainersAsync()
        {
            Calls.Add("list");
            return Task.FromResult<IReadOnlyList<ContainerInfo>>([new ContainerInfo
            {
                Id = "123456789abc", Name = "web", Image = "example", CreatedAt = 123, CreatedAtKnown = true,
                StateValue = (int)ContainerState.Stopped,
            }]);
        }

        private Task<CommandResult> StartAsync()
        {
            Calls.Add("start");
            Started.TrySetResult();
            return BlockStart ? ReleaseStart.Task : Task.FromResult(new CommandResult());
        }

        private Task<CommandResult> RecordLogs()
        {
            Calls.Add("logs");
            return Task.FromResult(new CommandResult());
        }

        public void Dispose()
        {
            Actions.Dispose();
            Inventory.Dispose();
        }
    }
}
