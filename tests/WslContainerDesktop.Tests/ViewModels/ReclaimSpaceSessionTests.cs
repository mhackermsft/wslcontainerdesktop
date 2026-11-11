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
public sealed class ReclaimSpaceSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSessionOrEngineRequiresFreshInventoryBeforePruning(bool changeEngine)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        if (changeEngine)
            Assert.True(fixture.Context.TryApplyIfUnlocked(() => { }));
        else
            Assert.True(fixture.Context.TrySelect("another"));

        await fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Contains("Refresh before reclaiming", fixture.Dialogs.LastMessage);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task FailedRescanCannotReuseAnOlderSnapshot()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        fixture.FailScan = true;
        await fixture.ViewModel.RefreshAsync();

        await fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Contains("Refresh before reclaiming", fixture.Dialogs.LastMessage);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task MultiStepPruneKeepsSelectionAndEngineFixedUntilRefreshFinishes()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();

        var prune = fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);
        await fixture.PruneStarted.Task;
        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
        Assert.False(fixture.ViewModel.RefreshChosenCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.PruneVolumesCommand.CanExecute(null));
        var imageReads = fixture.ImageReads;
        await fixture.ViewModel.RefreshAsync();
        Assert.Equal(imageReads, fixture.ImageReads);
        fixture.ReleasePrune.SetResult();
        await prune;

        Assert.Equal(["containers", "volumes", "images"], fixture.Mutations);
        Assert.True(fixture.ViewModel.PruneVolumesCommand.CanExecute(null));
        Assert.True(fixture.Context.TrySelect("another"));
    }

    [Fact]
    public async Task CleanupScansAndPrunesChosenSessionWithoutChangingManagementSelection()
    {
        var fixture = new Fixture(useSelector: true);
        fixture.ReleasePrune.SetResult();
        await fixture.ViewModel.RefreshAsync();
        var scannedAtConfirmation = 0;
        fixture.Dialogs.OnConfirm = () => scannedAtConfirmation = fixture.ChosenImageReadsBeforePrune;
        await fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);

        Assert.Equal("first", fixture.Context.SelectedSessionName);
        Assert.Equal(1, scannedAtConfirmation);
        Assert.Equal(["second:containers", "second:volumes", "second:images"], fixture.Mutations);
        Assert.Contains("second", fixture.Dialogs.LastConfirmation);
        Assert.Contains("second", fixture.ViewModel.InventorySessionDisplay);
    }

    [Fact]
    public async Task CancelledSessionChoiceDoesNotScanOrPrune()
    {
        var fixture = new Fixture(useSelector: true) { CancelChoice = true };
        await fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.ChosenImageReadsBeforePrune);
        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task ExplicitRefreshScansTheChosenSession()
    {
        var fixture = new Fixture(useSelector: true);
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.ChosenImageReadsBeforePrune);
        Assert.Contains("second", fixture.ViewModel.InventorySessionDisplay);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task ReplacedSessionAfterConfirmationDoesNotPrune()
    {
        var fixture = new Fixture(useSelector: true);
        fixture.Dialogs.OnConfirm = () => fixture.SessionChanged = true;
        await fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Mutations);
        Assert.Contains("Refresh before reclaiming", fixture.Dialogs.LastMessage);
    }

    [Fact]
    public async Task ConfirmationKeepsChosenInventoryPinnedUntilDecision()
    {
        var fixture = new Fixture(useSelector: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dialogs.ConfirmOverride = () => { started.TrySetResult(); return decision.Task; };
        var prune = fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(fixture.ViewModel.IsBusy);
        Assert.False(fixture.ViewModel.RefreshChosenCommand.CanExecute(null));
        var before = fixture.ImageReads;
        await fixture.ViewModel.RefreshAsync();
        Assert.Equal(before, fixture.ImageReads);
        decision.SetResult(false);
        await prune;
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task ChosenDefaultIsNotLabeledAsTheManagementSession()
    {
        var fixture = new Fixture(useSelector: true) { ChooseDefault = true, ConfirmResult = false };
        await fixture.ViewModel.PruneAllCommand.ExecuteAsync(null);
        Assert.Contains("Default CLI session", fixture.ViewModel.InventorySessionDisplay);
        Assert.DoesNotContain("first", fixture.ViewModel.InventorySessionDisplay);
    }

    private sealed class Fixture
    {
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public TaskCompletionSource PruneStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePrune { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Mutations { get; } = [];
        public int ImageReads { get; private set; }
        public bool FailScan { get; set; }
        public bool CancelChoice { get; set; }
        public bool ChooseDefault { get; set; }
        public bool ConfirmResult { set => Dialogs.ConfirmResult = value; }
        public bool SessionChanged { get; set; }
        public int ChosenImageReadsBeforePrune { get; private set; }
        public ReclaimSpaceViewModel ViewModel { get; }

        public Fixture(bool useSelector = false)
        {
            Context.Select("first");
            Dialogs.DismissMessage.SetResult();
            var engine = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.ListImagesAsync) => ReadImagesAsync(),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.PruneContainersAsync) => PruneContainersAsync(),
                nameof(IWslcService.PruneVolumesAsync) => PruneAsync("volumes"),
                nameof(IWslcService.PruneImagesAsync) => PruneAsync("images"),
                _ => throw new InvalidOperationException(method.Name),
            });
            var settings = NetworkTestProxy.Create<IWslcSettingsFileService>((method, _) => method.Name switch
            {
                nameof(IWslcSettingsFileService.ReadAsync) => Task.FromResult(new WslcSettingsFile()),
                _ => throw new InvalidOperationException(method.Name),
            });
            var chosenEngine = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.ListImagesAsync) => ReadChosenImagesAsync(),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.PruneContainersAsync) => PruneAsync("second:containers"),
                nameof(IWslcService.PruneVolumesAsync) => PruneAsync("second:volumes"),
                nameof(IWslcService.PruneImagesAsync) => PruneAsync("second:images"),
                _ => throw new InvalidOperationException(method.Name),
            });
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, _) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.ChooseAsync) or nameof(IWslcSessionSelectionService.ChooseForLaterAsync) or
                nameof(IWslcSessionSelectionService.ChooseActiveAsync) => Task.FromResult<WslcSessionSelection?>(CancelChoice
                    ? null : new(new(ChooseDefault ? null : "second"), "engine",
                        new WslcSessionInfo { Name = "second", ID = 2, CreatorPid = 200 },
                        ChooseDefault ? "Default CLI session" : "second")),
                nameof(IWslcSessionSelectionService.VerifyAsync) => SessionChanged
                    ? Task.FromException(new InvalidOperationException("Session changed")) : Task.CompletedTask,
                nameof(IWslcSessionSelectionService.CreateClient) => chosenEngine,
                _ => throw new InvalidOperationException(method.Name),
            });
            ViewModel = new ReclaimSpaceViewModel(engine, settings, Dialogs,
                NullLogger<ReclaimSpaceViewModel>.Instance, Context, useSelector ? selector : null);
        }

        private Task<IReadOnlyList<ImageInfo>> ReadChosenImagesAsync()
        {
            if (Mutations.Count == 0) ChosenImageReadsBeforePrune++;
            return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
        }

        private Task<IReadOnlyList<ImageInfo>> ReadImagesAsync()
        {
            ImageReads++;
            if (FailScan)
                return Task.FromException<IReadOnlyList<ImageInfo>>(new InvalidOperationException("scan failed"));
            return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
        }

        private async Task<CommandResult> PruneContainersAsync()
        {
            Mutations.Add("containers");
            PruneStarted.SetResult();
            await ReleasePrune.Task;
            return new CommandResult();
        }

        private Task<CommandResult> PruneAsync(string name)
        {
            Mutations.Add(name);
            return Task.FromResult(new CommandResult());
        }
    }
}
