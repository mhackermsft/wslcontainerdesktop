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
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.ViewModels;
using Xunit;
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;

namespace WslContainerDesktop.Tests.ViewModels;

public sealed class ComposeViewModelBusyTests
{
    [Theory]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 1, 0 })]
    [InlineData(new[] { 0, 1, 2 })]
    [InlineData(new[] { 0, 2, 1 })]
    [InlineData(new[] { 1, 0, 2 })]
    [InlineData(new[] { 1, 2, 0 })]
    [InlineData(new[] { 2, 0, 1 })]
    [InlineData(new[] { 2, 1, 0 })]
    public async Task OverlappingRefreshes_StayBusyUntilEveryInventoryCompletes(int[] completionOrder)
    {
        var fixture = new Fixture();
        var inventories = completionOrder.Select(_ => fixture.AddInventory()).ToArray();
        var refreshes = inventories.Select(_ => fixture.ViewModel.RefreshAsync()).ToArray();

        AssertBusy(fixture.ViewModel, true);
        for (var i = 0; i < completionOrder.Length; i++)
        {
            var index = completionOrder[i];
            inventories[index].SetResult([]);
            await refreshes[index];
            AssertBusy(fixture.ViewModel, i < completionOrder.Length - 1);
        }

        Assert.Equal(new[] { true, false }, fixture.BusyChanges);
        Assert.Equal(new[] { false, true }, fixture.RefreshAvailability);
        Assert.Equal(new[] { false, true }, fixture.ManageAvailability);

        // Returning to the page again must still be usable after the overlapping calls.
        var nextInventory = fixture.AddInventory();
        var nextRefresh = fixture.ViewModel.RefreshAsync();
        AssertBusy(fixture.ViewModel, true);
        nextInventory.SetResult([]);
        await nextRefresh;
        AssertBusy(fixture.ViewModel, false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NestedLifecycleRefresh_ReleasesOnlyItsOwnBusyOwnership(
        bool lifecycleFinishesFirst, bool inventoryFails)
    {
        var fixture = new Fixture();
        var nestedInventory = fixture.AddInventory();
        var navigationInventory = fixture.AddInventory();

        // The real restart command enters lifecycle ownership and then its nested refresh.
        var lifecycle = fixture.ViewModel.RestartSessionCommand.ExecuteAsync(null);
        var navigation = fixture.ViewModel.RefreshAsync();
        AssertBusy(fixture.ViewModel, true);

        if (!lifecycleFinishesFirst)
        {
            navigationInventory.SetResult([]);
            await navigation;
            AssertBusy(fixture.ViewModel, true);
        }

        if (inventoryFails)
            nestedInventory.SetException(new InvalidOperationException("Controlled inventory failure."));
        else
            nestedInventory.SetResult([]);

        await fixture.Dialogs.MessageShown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Nested refresh finished, but the lifecycle still owns the outcome dialog.
        Assert.False(lifecycle.IsCompleted);
        AssertBusy(fixture.ViewModel, true);

        fixture.Dialogs.DismissMessage.SetResult();
        await lifecycle;
        AssertBusy(fixture.ViewModel, lifecycleFinishesFirst);

        if (lifecycleFinishesFirst)
        {
            navigationInventory.SetResult([]);
            await navigation;
            AssertBusy(fixture.ViewModel, false);
        }

        Assert.Equal(new[] { true, false }, fixture.BusyChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRefresh_ReleasesOwnershipWithoutClearingOtherRefresh(bool cancelled)
    {
        var fixture = new Fixture();
        var firstInventory = fixture.AddInventory();
        var secondInventory = fixture.AddInventory();
        var first = fixture.ViewModel.RefreshAsync();
        var second = fixture.ViewModel.RefreshAsync();

        if (cancelled)
            firstInventory.SetCanceled();
        else
            firstInventory.SetException(new InvalidOperationException("Controlled inventory failure."));
        await first;
        AssertBusy(fixture.ViewModel, true);
        secondInventory.SetResult([]);
        await second;
        AssertBusy(fixture.ViewModel, false);
    }

    [Fact]
    public async Task StoreFailure_ReleasesOwnershipAndAllowsSubsequentRefresh()
    {
        var fixture = new Fixture { StoreError = new InvalidOperationException("Controlled store failure.") };
        var inventory = fixture.AddInventory();
        var refresh = fixture.ViewModel.RefreshAsync();
        inventory.SetResult([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        AssertBusy(fixture.ViewModel, false);

        fixture.StoreError = null;
        inventory = fixture.AddInventory();
        refresh = fixture.ViewModel.RefreshAsync();
        inventory.SetResult([]);
        await refresh;
        AssertBusy(fixture.ViewModel, false);
    }

    private static void AssertBusy(ComposeViewModel viewModel, bool expected)
    {
        Assert.Equal(expected, viewModel.IsBusy);
        Assert.Equal(!expected, viewModel.RefreshCommand.CanExecute(null));
        Assert.Equal(!expected, viewModel.ManageServicesCommand.CanExecute(null));
    }

    private sealed class Fixture
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<ContainerInfo>>> _inventories = new();
        public ComposeViewModel ViewModel { get; }
        public DialogService Dialogs { get; } = new();
        public Exception? StoreError { get; set; }
        public List<bool> BusyChanges { get; } = [];
        public List<bool> RefreshAvailability { get; } = [];
        public List<bool> ManageAvailability { get; } = [];

        public Fixture()
        {
            var wslc = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => _inventories.Dequeue().Task,
                nameof(IWslcService.RestartSessionAsync) =>
                    Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "Controlled restart failure." }),
                _ => throw new NotSupportedException(method.Name),
            });
            var store = NetworkTestProxy.Create<IComposeProjectStore>((method, _) => method.Name switch
            {
                nameof(IComposeProjectStore.GetAll) => StoreError is { } error
                    ? throw error : Array.Empty<ComposeProject>(),
                _ => throw new NotSupportedException(method.Name),
            });
            var supervisor = new ComposeNetworkSupervisorTests.Fixture().Supervisor;
            ViewModel = new ComposeViewModel(store, supervisor, wslc, Dialogs);
            ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ComposeViewModel.IsBusy))
                    BusyChanges.Add(ViewModel.IsBusy);
            };
            ViewModel.RefreshCommand.CanExecuteChanged += (_, _) =>
                RefreshAvailability.Add(ViewModel.RefreshCommand.CanExecute(null));
            ViewModel.ManageServicesCommand.CanExecuteChanged += (_, _) =>
                ManageAvailability.Add(ViewModel.ManageServicesCommand.CanExecute(null));
        }

        public TaskCompletionSource<IReadOnlyList<ContainerInfo>> AddInventory()
        {
            var inventory = new TaskCompletionSource<IReadOnlyList<ContainerInfo>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inventories.Enqueue(inventory);
            return inventory;
        }
    }
}
