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

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class NetworksSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSessionOrEngineRejectsOldNetworkBeforeConfirmation(bool changeEngine)
    {
        var fixture = new Fixture();
        await fixture.RefreshAndWaitAsync();
        var row = fixture.ViewModel.Networks.Single(network => network.Name == "one");
        if (changeEngine)
            Assert.True(fixture.Context.TryApplyIfUnlocked(() => { }));
        else
            Assert.True(fixture.Context.TrySelect("another"));

        await fixture.ViewModel.RemoveCommand.ExecuteAsync(row);

        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Contains("Refresh before taking", fixture.Dialogs.LastMessage);
        Assert.Empty(fixture.Removed);
    }

    [Fact]
    public async Task RowFromEarlierRefreshIsRejectedEvenWhenItsNameMatches()
    {
        var fixture = new Fixture();
        await fixture.RefreshAndWaitAsync();
        var row = fixture.ViewModel.Networks.Single(network => network.Name == "one");
        await fixture.RefreshAndWaitAsync();

        await fixture.ViewModel.RemoveCommand.ExecuteAsync(row);

        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Empty(fixture.Removed);
    }

    [Fact]
    public async Task BulkRemovalKeepsSessionAndEngineFixedUntilCompletion()
    {
        var fixture = new Fixture();
        await fixture.RefreshAndWaitAsync();
        var rows = fixture.ViewModel.Networks.Where(network => network.Name is "one" or "two").ToArray();
        var operation = fixture.ViewModel.BulkRemoveAsync(rows);
        await fixture.RemovalStarted.Task;
        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
        fixture.ReleaseRemoval.SetResult();
        await operation;
        await fixture.WaitForUnlockAsync();

        Assert.Equal(["one", "two"], fixture.Removed);
        Assert.True(fixture.Context.TrySelect("another"));
    }

    [Fact]
    public async Task LeavingPageCancelsUsageProbeAndReleasesSession()
    {
        var fixture = new Fixture { BlockUsage = true };
        await fixture.ViewModel.RefreshAsync();
        await fixture.UsageStarted.Task;
        Assert.False(fixture.Context.TrySelect("another"));
        var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Context.SelectionUnlocked += (_, _) => unlocked.TrySetResult();

        fixture.ViewModel.CancelRefresh();
        await unlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fixture.Context.TrySelect("another"));
    }

    private sealed class Fixture
    {
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public List<string> Removed { get; } = [];
        public TaskCompletionSource RemovalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRemoval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UsageStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockUsage { get; set; }
        public NetworksViewModel ViewModel { get; }

        public Fixture()
        {
            Context.Select("first");
            Dialogs.DismissMessage.SetResult();
            var engine = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>(
                    [new NetworkInfo { Id = "id-one", Name = "one" }, new NetworkInfo { Id = "id-two", Name = "two" }]),
                nameof(IWslcService.ListContainersAsync) => ReadContainersAsync((CancellationToken)args[1]!),
                nameof(IWslcService.RemoveNetworkAsync) => RemoveAsync((string)args[0]!),
                _ => throw new InvalidOperationException(method.Name),
            });
            ViewModel = new NetworksViewModel(engine, Dialogs, Context);
        }

        public async Task RefreshAndWaitAsync()
        {
            await ViewModel.RefreshAsync();
            await WaitForUnlockAsync();
        }

        public async Task WaitForUnlockAsync()
        {
            var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUnlocked(object? sender, EventArgs args) => unlocked.TrySetResult();
            Context.SelectionUnlocked += OnUnlocked;
            try
            {
                if (Context.IsSelectionLocked)
                    await unlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Context.SelectionUnlocked -= OnUnlocked;
            }
        }

        private async Task<IReadOnlyList<ContainerInfo>> ReadContainersAsync(CancellationToken token)
        {
            if (BlockUsage)
            {
                UsageStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return [];
        }

        private async Task<CommandResult> RemoveAsync(string name)
        {
            Removed.Add(name);
            if (Removed.Count == 1)
            {
                RemovalStarted.SetResult();
                await ReleaseRemoval.Task;
            }
            return new CommandResult();
        }
    }
}
