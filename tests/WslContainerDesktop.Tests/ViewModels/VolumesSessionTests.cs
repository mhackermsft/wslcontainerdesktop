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
public sealed class VolumesSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSessionOrEngineRejectsOldVolumeBeforeConfirmation(bool changeEngine)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var row = fixture.ViewModel.Volumes[0];
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
        await fixture.ViewModel.RefreshAsync();
        var row = fixture.ViewModel.Volumes[0];
        await fixture.ViewModel.RefreshAsync();

        await fixture.ViewModel.RemoveCommand.ExecuteAsync(row);

        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Empty(fixture.Removed);
    }

    [Fact]
    public async Task BulkRemovalKeepsSessionAndEngineFixedUntilCompletion()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var operation = fixture.ViewModel.BulkRemoveAsync(fixture.ViewModel.Volumes.ToArray());
        await fixture.RemovalStarted.Task;
        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
        fixture.ReleaseRemoval.SetResult();
        await operation;

        Assert.Equal(["one", "two"], fixture.Removed);
        Assert.True(fixture.Context.TrySelect("another"));
    }

    private sealed class Fixture
    {
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public List<string> Removed { get; } = [];
        public TaskCompletionSource RemovalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRemoval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VolumesViewModel ViewModel { get; }

        public Fixture()
        {
            Context.Select("first");
            Dialogs.DismissMessage.SetResult();
            var engine = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>(
                    [new VolumeInfo { Name = "one" }, new VolumeInfo { Name = "two" }]),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
                nameof(IWslcService.InspectVolumeAsync) => Task.FromResult(new CommandResult { StandardOutput = "{}" }),
                nameof(IWslcService.RemoveVolumeAsync) => RemoveAsync((string)args[0]!),
                _ => throw new InvalidOperationException(method.Name),
            });
            ViewModel = new VolumesViewModel(engine, Dialogs, NullLogger<VolumesViewModel>.Instance, Context);
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
