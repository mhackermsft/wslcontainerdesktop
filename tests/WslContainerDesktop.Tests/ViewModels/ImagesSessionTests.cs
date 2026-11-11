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
public sealed class ImagesSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSessionOrEngineRejectsOldImageBeforeConfirmation(bool changeEngine)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var row = fixture.ViewModel.Images[0];
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
    public async Task PriorRefreshRowIsRejectedEvenWhenImageIdMatches()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var row = fixture.ViewModel.Images[0];
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
        var operation = fixture.ViewModel.BulkRemoveAsync(fixture.ViewModel.Images.ToArray());
        await fixture.RemovalStarted.Task;
        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
        fixture.ReleaseRemoval.SetResult();
        await operation;

        Assert.Equal(["id-one", "id-two"], fixture.Removed);
        Assert.True(fixture.Context.TrySelect("another"));
    }

    [Fact]
    public async Task OlderRefreshCannotReplaceNewerImageRows()
    {
        var fixture = new Fixture();
        var first = fixture.QueueInventory();
        var second = fixture.QueueInventory();
        var oldRefresh = fixture.ViewModel.RefreshAsync();
        var newRefresh = fixture.ViewModel.RefreshAsync();

        second.SetResult([new ImageInfo { Id = "new", Repository = "new", Tag = "latest" }]);
        await newRefresh;
        first.SetResult([new ImageInfo { Id = "old", Repository = "old", Tag = "latest" }]);
        await oldRefresh;

        Assert.Equal("new", Assert.Single(fixture.ViewModel.Images).Id);
    }

    [Fact]
    public async Task RegistryUpdateCheckReleasesSelectionAndDiscardsStaleResults()
    {
        var fixture = new Fixture { BlockUpdate = true };
        await fixture.ViewModel.RefreshAsync();
        var rows = fixture.ViewModel.Images.ToArray();
        var operation = fixture.ViewModel.CheckUpdatesCommand.ExecuteAsync(null);
        await fixture.UpdateStarted.Task;

        Assert.True(fixture.Context.TrySelect("another"));
        fixture.ReleaseUpdate.SetResult();
        await operation;

        Assert.All(rows, row => Assert.NotEqual(ImageUpdateState.UpdateAvailable, row.UpdateState));
    }

    private sealed class Fixture
    {
        private readonly Queue<TaskCompletionSource<IReadOnlyList<ImageInfo>>> _inventories = [];
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public List<string> Removed { get; } = [];
        public TaskCompletionSource RemovalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRemoval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource UpdateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockUpdate { get; set; }
        public ImagesViewModel ViewModel { get; }

        public Fixture()
        {
            Context.Select("first");
            Dialogs.DismissMessage.SetResult();
            var engine = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.ListImagesAsync) => ReadImagesAsync(),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
                nameof(IWslcService.RemoveImageAsync) => RemoveAsync((string)args[0]!),
                nameof(IWslcService.GetImageRepoDigestsAsync) => Task.FromResult<IReadOnlyList<string>>(["sha256:example"]),
                _ => throw new InvalidOperationException(method.Name),
            });
            var settings = Unused<ISettingsService>();
            var azure = Unused<IAzureCliService>();
            var auth = new RegistryAuthRefresher(engine, azure, settings, NullLogger<RegistryAuthRefresher>.Instance);
            var policy = NetworkTestProxy.Create<IWslPolicyService>((method, _) => method.Name switch
            {
                nameof(IWslPolicyService.GetPolicy) => new WslPolicySnapshot(true, true,
                    new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, [])),
                _ => throw new InvalidOperationException(method.Name),
            });
            var updates = NetworkTestProxy.Create<IImageUpdateService>((method, _) => method.Name switch
            {
                nameof(IImageUpdateService.CheckAsync) => CheckUpdateAsync(),
                _ => throw new InvalidOperationException(method.Name),
            });
            ViewModel = new ImagesViewModel(engine, new StatusMonitor(), Dialogs, settings, auth,
                Unused<INotificationService>(), Unused<IRunProfileStore>(), Unused<IActivityLog>(),
                updates, Unused<IRegistryCatalogService>(), policy,
                Unused<IRegistryCredentialStore>(), Unused<IWslcSettingsFileService>(), Context);
        }

        public TaskCompletionSource<IReadOnlyList<ImageInfo>> QueueInventory()
        {
            var result = new TaskCompletionSource<IReadOnlyList<ImageInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inventories.Enqueue(result);
            return result;
        }

        private Task<IReadOnlyList<ImageInfo>> ReadImagesAsync() =>
            _inventories.Count > 0 ? _inventories.Dequeue().Task :
                Task.FromResult<IReadOnlyList<ImageInfo>>(
                    [new ImageInfo { Id = "id-one", Repository = "one", Tag = "latest" },
                     new ImageInfo { Id = "id-two", Repository = "two", Tag = "latest" }]);

        private async Task<CommandResult> RemoveAsync(string id)
        {
            Removed.Add(id);
            if (Removed.Count == 1)
            {
                RemovalStarted.SetResult();
                await ReleaseRemoval.Task;
            }
            return new CommandResult();
        }

        private async Task<ImageUpdateState> CheckUpdateAsync()
        {
            if (BlockUpdate)
            {
                UpdateStarted.TrySetResult();
                await ReleaseUpdate.Task;
            }
            return ImageUpdateState.UpdateAvailable;
        }

        private static T Unused<T>() where T : class =>
            NetworkTestProxy.Create<T>((method, _) => throw new InvalidOperationException(method.Name));
    }
}
