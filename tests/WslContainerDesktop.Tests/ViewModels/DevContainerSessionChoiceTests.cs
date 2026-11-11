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
public sealed class DevContainerSessionChoiceTests
{
    [Fact]
    public async Task StartingUnassignedWorkspacePersistsAndUsesChosenOwner()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var row = Assert.Single(fixture.ViewModel.DevContainers);
        Assert.Contains("not assigned", row.SessionDisplay, StringComparison.OrdinalIgnoreCase);
        await fixture.ViewModel.UpCommand.ExecuteAsync(row);

        Assert.Equal("second", fixture.Store.Get("test")!.SessionOwner!.SessionName);
        Assert.Equal(["second:pull", "second:run"], fixture.Mutations);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
        Assert.Equal(1, fixture.Choices);
        Assert.Contains("second", Assert.Single(fixture.ViewModel.DevContainers).SessionDisplay);
    }

    [Fact]
    public async Task CancelledChoiceDoesNotAssignOrRun()
    {
        using var fixture = new Fixture { CancelChoice = true };
        await fixture.ViewModel.RefreshAsync();
        await fixture.ViewModel.UpCommand.ExecuteAsync(Assert.Single(fixture.ViewModel.DevContainers));
        Assert.Null(fixture.Store.Get("test")!.SessionOwner);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task ExistingOwnerDoesNotPromptOrUseManagementSession()
    {
        using var fixture = new Fixture();
        fixture.Store.Save(new DevContainerConfig
        {
            Id = "test", Name = "dev", Image = "fixture",
            RunOptions = new() { Image = "fixture" },
            SessionOwner = AutomationSessionOwner.Capture("engine", new("second")),
        });
        await fixture.ViewModel.RefreshAsync();
        await fixture.ViewModel.UpCommand.ExecuteAsync(Assert.Single(fixture.ViewModel.DevContainers));
        Assert.Equal(0, fixture.Choices);
        Assert.Equal(["second:pull", "second:run"], fixture.Mutations);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task StoppingUnassignedWorkspaceWithNoMatchingContainerDoesNotAssignIt()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        await fixture.ViewModel.StopCommand.ExecuteAsync(Assert.Single(fixture.ViewModel.DevContainers));
        Assert.Equal(1, fixture.Choices);
        Assert.Null(fixture.Store.Get("test")!.SessionOwner);
        Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task OverlappingRefreshDoesNotReleaseAnActiveWorkspaceOperation()
    {
        using var fixture = new Fixture { HoldPull = true };
        await fixture.ViewModel.RefreshAsync();
        var up = fixture.ViewModel.UpCommand.ExecuteAsync(Assert.Single(fixture.ViewModel.DevContainers));
        await fixture.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ViewModel.RefreshAsync();
        Assert.True(fixture.ViewModel.IsBusy);
        fixture.ReleasePull.SetResult(new CommandResult());
        await up;
        Assert.False(fixture.ViewModel.IsBusy);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wcd-dev-choice-" + Guid.NewGuid().ToString("N"));
        private readonly WslcSessionInfo _second = new() { Name = "second", ID = 2, CreatorPid = 200 };
        private bool _exists;
        public WslcSessionContext Context { get; } = new();
        public DevContainerStore Store { get; }
        public List<string> Mutations { get; } = [];
        public bool CancelChoice { get; set; }
        public bool HoldPull { get; set; }
        public TaskCompletionSource PullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CommandResult> ReleasePull { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Choices { get; private set; }
        public DevContainersViewModel ViewModel { get; }

        public Fixture()
        {
            Context.Select("first");
            Store = new DevContainerStore(NullLogger<DevContainerStore>.Instance, Path.Combine(_root, "store.json"));
            Store.Save(new DevContainerConfig { Id = "test", Name = "dev", Image = "fixture",
                RunOptions = new() { Image = "fixture" } });
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) =>
                method.Name == "get_WslcPath" ? "engine" : throw new InvalidOperationException(method.Name));
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = [new() { Name = "first", ID = 1, CreatorPid = 100 }, _second] },
                }),
                _ => throw new InvalidOperationException(method.Name),
            });
            var client = NetworkTestProxy.Create<IWslcService>((method, _) =>
            {
                switch (method.Name)
                {
                    case nameof(IWslcService.PullImageAsync):
                        Mutations.Add("second:pull");
                        if (HoldPull) { PullStarted.TrySetResult(); return ReleasePull.Task; }
                        return Task.FromResult(new CommandResult());
                    case nameof(IWslcService.RunContainerAsync):
                        Mutations.Add("second:run"); _exists = true; return Task.FromResult(new CommandResult());
                    case nameof(IWslcService.ListContainersAsync):
                        return Task.FromResult<IReadOnlyList<ContainerInfo>>(_exists
                            ? [new() { Id = "123456789abc", Name = "devcontainer-test", StateValue = 2 }] : []);
                    case nameof(IWslcService.InspectContainerAsync):
                        return Task.FromResult(new CommandResult
                        {
                            StandardOutput = """{"Id":"123456789abc","Config":{"Labels":{"com.wslcontainerdesktop.devcontainer.id":"test"}},"NetworkSettings":{"Networks":{}}}""",
                        });
                    default: throw new InvalidOperationException(method.Name);
                }
            });
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => "engine",
                NullLogger<WslcSessionInventoryReader>.Instance);
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, _) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.ChooseAsync) or nameof(IWslcSessionSelectionService.ChooseForLaterAsync) or
                nameof(IWslcSessionSelectionService.ChooseActiveAsync) => Choose(),
                nameof(IWslcSessionSelectionService.VerifyAsync) => Task.CompletedTask,
                nameof(IWslcSessionSelectionService.CreateClient) => client,
                _ => throw new InvalidOperationException(method.Name),
            });
            var dialogs = new DialogService();
            dialogs.DismissMessage.SetResult();
            var sessions = new DevContainerSessionCoordinator(selector, reader, settings, Store,
                Unused<IDevContainerFeatureResolver>(), null!, Unused<IComposeProjectStore>(), null!,
                Context, dialogs, NullLogger<DevContainerSupervisor>.Instance,
                NullLogger<DevContainerSessionCoordinator>.Instance);
            var presenter = NetworkTestProxy.Create<IDevContainerHostCommandPresenter>((method, _) =>
                method.Name == nameof(IDevContainerHostCommandPresenter.ConfirmAsync)
                    ? Task.FromResult(true) : throw new InvalidOperationException(method.Name));
            ViewModel = new DevContainersViewModel(Unused<IDevContainerImporter>(), Store, presenter,
                sessions, dialogs, NullLogger<DevContainersViewModel>.Instance);
        }

        private Task<WslcSessionSelection?> Choose()
        {
            Choices++;
            return Task.FromResult<WslcSessionSelection?>(CancelChoice ? null : new(new("second"), "engine", _second, "second"));
        }

        private static T Unused<T>() where T : class =>
            NetworkTestProxy.Create<T>((method, _) => throw new InvalidOperationException(method.Name));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
