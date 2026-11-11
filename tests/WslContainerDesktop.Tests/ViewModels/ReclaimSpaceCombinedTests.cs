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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class ReclaimSpaceCombinedTests
{
    [Fact]
    public async Task PageRefreshScansBothSessionsWithoutAskingAndKeepsSameNamesSeparate()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.Choices);
        Assert.Equal(2, fixture.ViewModel.ImageCount);
        Assert.Equal(2, fixture.ViewModel.DanglingImageCount);
        Assert.Equal(2, fixture.ViewModel.UnusedVolumeCount);
        Assert.Equal(3072, fixture.ViewModel.DanglingImages.Sum(item => item.Size));
        Assert.Equal(["Alpha", "alpha"], fixture.ViewModel.DanglingImages.Select(item => item.SessionName).Order(StringComparer.Ordinal));
        Assert.Equal(["Alpha", "alpha"], fixture.ViewModel.UnusedVolumes.Select(item => item.SessionName).Order(StringComparer.Ordinal));
        Assert.Contains("2 of 2", fixture.ViewModel.InventorySessionDisplay);
        Assert.True(fixture.ViewModel.HasInventory);
        Assert.Empty(fixture.ViewModel.InventoryWarning);
    }

    [Fact]
    public async Task FailureShowsPartialResultsRatherThanACompleteTotal()
    {
        var fixture = new Fixture { FailedSession = "Alpha" };
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.ViewModel.ImageCount);
        Assert.Contains("1 of 2", fixture.ViewModel.InventorySessionDisplay);
        Assert.Contains("Alpha", fixture.ViewModel.InventoryWarning);
        Assert.Contains("synthetic read failed", fixture.ViewModel.InventoryWarning);
        Assert.True(fixture.ViewModel.HasInventory);
    }

    [Fact]
    public async Task MissingStoppedContainerSizesAreUnknownAndRelocalize()
    {
        UiText.Configure((key, _) => key == "Common_Text0003" && UiText.LanguageTag == "zh-Hans"
            ? "未知" : key);
        try
        {
            UiText.SetLanguage("en-US");
            var fixture = new Fixture { MissingContainerSizes = true };
            await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

            Assert.Equal(2, fixture.ViewModel.StoppedContainerCount);
            Assert.Equal("Unknown", fixture.ViewModel.TotalReclaimableSizeDisplay);
            Assert.Equal("Unknown", fixture.ViewModel.StoppedContainersReclaimableSizeDisplay);
            Assert.Contains("writable-layer size", fixture.ViewModel.StatusMessageDisplay, StringComparison.OrdinalIgnoreCase);
            UiText.SetLanguage("zh-Hans");
            Assert.Equal("未知", fixture.ViewModel.TotalReclaimableSizeDisplay);
            Assert.Equal("未知", fixture.ViewModel.StoppedContainersReclaimableSizeDisplay);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task UnknownImageUsageAndContainerStatesAreNotReportedAsReclaimable()
    {
        var fixture = new Fixture { UnknownMetadata = true };
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.ViewModel.DanglingImageCount);
        Assert.Equal(0, fixture.ViewModel.StoppedContainerCount);
        Assert.Equal("Unknown", fixture.ViewModel.ImagesReclaimableSizeDisplay);
        Assert.Equal("Unknown", fixture.ViewModel.StoppedContainersReclaimableSizeDisplay);
        Assert.Equal("Unknown", fixture.ViewModel.TotalReclaimableSizeDisplay);
        Assert.All(fixture.ViewModel.DanglingImages, image => Assert.True(image.UsageUnknown));
    }

    [Fact]
    public async Task ContainersReportingTagsLeaveDanglingImageUsageUnknown()
    {
        var fixture = new Fixture { TagOnlyImage = true };
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.Equal(0, fixture.ViewModel.DanglingImageCount);
        Assert.Equal("Unknown", fixture.ViewModel.ImagesReclaimableSizeDisplay);
        Assert.All(fixture.ViewModel.DanglingImages, image => Assert.True(image.UsageUnknown));
    }

    [Fact]
    public async Task LeavingPageCancelsQueuedScansAndReturningStartsAFreshScan()
    {
        var fixture = new Fixture { HoldScans = true, ExtraSession = true };
        using var lifetime = new CancellationTokenSource();
        fixture.ViewModel.SetPageLifetime(lifetime.Token);
        var scan = fixture.ViewModel.RefreshForPageAsync(lifetime.Token);
        await fixture.TwoScansStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        fixture.HoldScans = false;
        using var nextLifetime = new CancellationTokenSource();
        fixture.ViewModel.SetPageLifetime(nextLifetime.Token);
        var next = fixture.ViewModel.RefreshForPageAsync(nextLifetime.Token);
        await Task.WhenAll(scan, next).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, fixture.Reads.Count);
        Assert.Contains("3 of 3", fixture.ViewModel.InventorySessionDisplay);
        Assert.True(fixture.ViewModel.HasInventory);
    }

    [Fact]
    public async Task AllFailuresHideStaleMetrics()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);
        fixture.FailAll = true;
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.HasInventory);
        Assert.Contains("0 of 2", fixture.ViewModel.InventorySessionDisplay);
        Assert.Contains("alpha", fixture.ViewModel.InventoryWarning);
        Assert.Contains("Alpha", fixture.ViewModel.InventoryWarning);
    }

    [Fact]
    public async Task SessionReplacedBeforeDisplayIsExcludedFromTotals()
    {
        var fixture = new Fixture { ReplacedBeforeDisplay = "alpha" };
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.ViewModel.ImageCount);
        Assert.Equal("Alpha", Assert.Single(fixture.ViewModel.DanglingImages).SessionName);
        Assert.Contains("alpha: changed", fixture.ViewModel.InventoryWarning);
        Assert.Contains("1 of 2", fixture.ViewModel.InventorySessionDisplay);
    }

    [Fact]
    public async Task EmptyDiscoveryDoesNotStartASessionOrPresentAnEmptyDisk()
    {
        var fixture = new Fixture { NoSessions = true };
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.HasInventory);
        Assert.Contains("No active sessions", fixture.ViewModel.InventorySessionDisplay);
        Assert.Empty(fixture.Reads);
        Assert.Equal(0, fixture.Choices);
        Assert.False(fixture.ViewModel.PruneAllCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RefreshChosenCommand.CanExecute(null));
    }

    [Fact]
    public async Task CleanupUsesFreshChosenSessionInventoryAndReturnsToCombinedView()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);
        fixture.Dialogs.OnConfirm = () =>
        {
            Assert.Contains("Alpha", fixture.Dialogs.LastConfirmation);
            Assert.Equal(1, fixture.ViewModel.DanglingImageCount);
        };
        await fixture.ViewModel.PruneImagesCommand.ExecuteAsync(null);

        Assert.Equal(["Alpha:images"], fixture.Mutations);
        Assert.Equal(1, fixture.Choices);
        Assert.Contains("2 of 2", fixture.ViewModel.InventorySessionDisplay);
        Assert.Equal(2, fixture.ViewModel.ImageCount);
    }

    [Fact]
    public async Task ChangedTargetAfterConfirmationCannotPrune()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);
        fixture.Dialogs.OnConfirm = () => fixture.Changed = true;
        await fixture.ViewModel.PruneImagesCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Mutations);
        Assert.Contains("Refresh before reclaiming", fixture.Dialogs.LastMessage);
    }

    [Fact]
    public async Task CancelledCleanupDoesNotChangeTheCombinedInventory()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshChosenCommand.ExecuteAsync(null);
        var reads = fixture.Reads.Count;
        fixture.CancelChoice = true;
        await fixture.ViewModel.PruneImagesCommand.ExecuteAsync(null);
        Assert.Equal(reads, fixture.Reads.Count);
        Assert.Empty(fixture.Mutations);
        Assert.Contains("2 of 2", fixture.ViewModel.InventorySessionDisplay);
    }

    private sealed class Fixture
    {
        private readonly WslcSessionInfo[] _sessions =
        [
            new() { ID = 1, CreatorPid = 11, Name = "alpha" },
            new() { ID = 2, CreatorPid = 22, Name = "Alpha" },
        ];
        public readonly DialogService Dialogs = new();
        public readonly ConcurrentBag<string> Reads = [];
        public readonly List<string> Mutations = [];
        public readonly ReclaimSpaceViewModel ViewModel;
        public bool NoSessions, FailAll, CancelChoice, Changed, MissingContainerSizes, UnknownMetadata, HoldScans, ExtraSession, TagOnlyImage;
        public TaskCompletionSource TwoScansStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailedSession;
        public int Choices;

        public Fixture()
        {
            Dialogs.DismissMessage.SetResult();
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
                method.Name == nameof(IWslcService.GetSystemInfoAsync)
                    ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo
                        { Sessions = NoSessions ? [] : ExtraSession
                            ? [.. _sessions, new WslcSessionInfo { ID = 3, CreatorPid = 33, Name = "third" }]
                            : _sessions } })
                    : throw new InvalidOperationException(method.Name));
            IWslcService Client(string name) => NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.ListImagesAsync) => Images(name, (CancellationToken)args![0]!),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    UnknownMetadata
                        ? [new ContainerInfo { Id = "missing-" + name, Name = "missing", StateValue = (int)ContainerState.Unknown },
                           new ContainerInfo { Id = "paused-" + name, Name = "paused", Image = "other", StateValue = (int)ContainerState.Paused }]
                        : TagOnlyImage
                        ? [new ContainerInfo { Id = "container-" + name, Name = "web", Image = "nginx:latest", StateValue = (int)ContainerState.Running }]
                        : MissingContainerSizes
                        ? [new ContainerInfo { Id = "container-" + name, Name = "web", Image = "abcdef012345", StateValue = (int)ContainerState.Stopped }]
                        : []),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>(
                    [new VolumeInfo { Name = "data", Driver = "local" }]),
                nameof(IWslcService.InspectVolumeAsync) => Task.FromResult(new CommandResult { StandardOutput = "{}" }),
                nameof(IWslcService.InspectContainerAsync) => Task.FromResult(new CommandResult { StandardOutput = "{\"Mounts\":[]}" }),
                nameof(IWslcService.PruneImagesAsync) => Prune(name),
                _ => throw new InvalidOperationException(method.Name),
            });
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, args) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.ChooseActiveAsync) => Choose(),
                nameof(IWslcSessionSelectionService.VerifyAsync) => Verify((WslcSessionSelection)args![0]!),
                nameof(IWslcSessionSelectionService.CreateClient) => Client(((WslcSessionSelection)args![0]!).Generation!.Name),
                _ => throw new InvalidOperationException(method.Name),
            });
            var settings = NetworkTestProxy.Create<IWslcSettingsFileService>((method, _) =>
                method.Name == nameof(IWslcSettingsFileService.ReadAsync)
                    ? Task.FromResult(new WslcSettingsFile { DefaultStoragePath = "synthetic" })
                    : throw new InvalidOperationException(method.Name));
            var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session.Name),
                () => "engine", NullLogger<WslcSessionInventoryReader>.Instance);
            ViewModel = new ReclaimSpaceViewModel(discovery, settings, Dialogs,
                NullLogger<ReclaimSpaceViewModel>.Instance, new WslcSessionContext(), selector, reader);
        }

        private async Task<IReadOnlyList<ImageInfo>> Images(string name, CancellationToken ct)
        {
            Reads.Add(name);
            if (HoldScans)
            {
                if (Reads.Count >= 2) TwoScansStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (FailAll || name == FailedSession)
                throw new IOException("synthetic read failed");
            return [new ImageInfo { Id = "same", Repository = "<none>", Tag = "<none>", Size = name == "alpha" ? 1024 : 2048 }];
        }

        private Task<WslcSessionSelection?> Choose()
        {
            Choices++;
            return Task.FromResult<WslcSessionSelection?>(CancelChoice ? null :
                new(new("Alpha"), "engine", _sessions[1], "Alpha"));
        }

        private readonly ConcurrentDictionary<string, int> _verifications = new(StringComparer.Ordinal);
        public string? ReplacedBeforeDisplay { get; set; }
        private Task Verify(WslcSessionSelection selection)
        {
            var count = _verifications.AddOrUpdate(selection.Generation!.Name, 1, (_, value) => value + 1);
            return Changed || selection.Generation.Name == ReplacedBeforeDisplay && count >= 3
                ? Task.FromException(new InvalidOperationException("changed")) : Task.CompletedTask;
        }

        private Task<CommandResult> Prune(string name)
        {
            Mutations.Add(name + ":images");
            return Task.FromResult(new CommandResult());
        }
    }
}
