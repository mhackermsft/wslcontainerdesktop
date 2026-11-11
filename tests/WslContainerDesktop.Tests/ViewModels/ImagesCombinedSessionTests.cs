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
public sealed class ImagesCombinedSessionTests
{
    [Fact]
    public async Task ContainerInventoryFailureLeavesImageUsageUnknownForThatSession()
    {
        using var fixture = new Fixture { FailSecondContainerInventory = true };
        await fixture.ViewModel.RefreshAsync();

        Assert.False(fixture.ViewModel.Images.Single(image => image.SessionName == "first").UsageUnknown);
        Assert.True(fixture.ViewModel.Images.Single(image => image.SessionName == "second").UsageUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullAndBuildUseTheChosenSessionWithoutChangingManagementSelection(bool build)
    {
        using var fixture = new Fixture();
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is PullImageDialog pull)
            {
                Assert.Contains("second", pull.Title.ToString() ?? string.Empty);
                pull.Reference = "example/app:latest";
            }
            if (dialog is BuildImageDialog image)
            {
                Assert.Contains("second", image.Title.ToString() ?? string.Empty);
                image.ContextPath = "context";
                image.ImageTag = "example/app:latest";
            }
            return ContentDialogResult.Primary;
        };

        if (build)
            await fixture.ViewModel.BuildCommand.ExecuteAsync(null);
        else
            await fixture.ViewModel.PullCommand.ExecuteAsync(null);

        Assert.Contains(build ? "second:build:example/app:latest" : "second:pull:example/app:latest", fixture.Calls);
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("first:", StringComparison.Ordinal));
        Assert.Equal("first", fixture.Context.SelectedSessionName);
        Assert.True(fixture.VerifyCalls >= 2);
        Assert.Equal("second", Assert.Single(fixture.ActivityOwners)!.SessionName);
    }

    [Fact]
    public async Task ImageUpdateActivityUsesTheImageOwner()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var image = fixture.ViewModel.Images.Single(item => item.SessionName == "second");

        await fixture.ViewModel.PullUpdateCommand.ExecuteAsync(image);

        Assert.Contains("second:pull:example:latest", fixture.Calls);
        Assert.Equal("second", Assert.Single(fixture.ActivityOwners)!.SessionName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrChangedDestinationNeverPullsOrBuilds(bool changed)
    {
        using var fixture = new Fixture { CancelDestination = !changed, FailAfterDialog = changed };
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is PullImageDialog pull) pull.Reference = "example/app:latest";
            if (dialog is BuildImageDialog image) { image.ContextPath = "context"; image.ImageTag = "example/app:latest"; }
            return ContentDialogResult.Primary;
        };
        await fixture.ViewModel.PullCommand.ExecuteAsync(null);
        fixture.VerifyCalls = 0;
        await fixture.ViewModel.BuildCommand.ExecuteAsync(null);

        Assert.DoesNotContain(fixture.Calls, call => call.Contains(":pull:", StringComparison.Ordinal) ||
            call.Contains(":build:", StringComparison.Ordinal));
        Assert.Equal("first", fixture.Context.SelectedSessionName);
        if (changed) Assert.Contains("changed", fixture.Dialogs.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("import")]
    [InlineData("prune")]
    public async Task OtherImageActionsUseChosenSession(string action)
    {
        using var fixture = new Fixture();
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is SimpleInputDialog input) input.Value = "snapshot:latest";
            return ContentDialogResult.Primary;
        };
        if (action == "load") await fixture.ViewModel.LoadImageAsync("archive.tar");
        else if (action == "import") await fixture.ViewModel.ImportImageAsync("archive.tar");
        else await fixture.ViewModel.PruneCommand.ExecuteAsync(null);

        Assert.Contains($"second:{action}:archive", fixture.Calls);
        Assert.Equal("first", fixture.Context.SelectedSessionName);
        if (action == "prune") Assert.Contains("second", fixture.Dialogs.LastConfirmation);
    }

    [Theory]
    [InlineData("load", false)]
    [InlineData("load", true)]
    [InlineData("import", false)]
    [InlineData("import", true)]
    [InlineData("prune", false)]
    [InlineData("prune", true)]
    public async Task OtherImageActionsDoNotMutateAfterCancelOrSessionChange(string action, bool changed)
    {
        using var fixture = new Fixture { CancelDestination = !changed, FailAfterDialog = changed };
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is SimpleInputDialog input) input.Value = "snapshot:latest";
            return ContentDialogResult.Primary;
        };
        if (action == "load") await fixture.ViewModel.LoadImageAsync("archive.tar");
        else if (action == "import") await fixture.ViewModel.ImportImageAsync("archive.tar");
        else await fixture.ViewModel.PruneCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fixture.Calls, call => call.Contains($":{action}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PruneDoesNotStartADormantSessionBeforeConfirmation()
    {
        using var fixture = new Fixture { DormantDestination = true };
        await fixture.ViewModel.PruneCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.StartingChoices);
        Assert.Equal(1, fixture.NonStartingChoices);
        Assert.Equal(0, fixture.Dialogs.ConfirmCount);
        Assert.Empty(fixture.Calls);
        Assert.Contains("not running", fixture.Dialogs.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GridFiltersSameNamedImagesByOwnerWithoutChangingInventory()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var original = fixture.ViewModel.Images.ToArray();

        fixture.ViewModel.Grid.SearchText = "SECOND";
        Assert.Same(original[1], Assert.Single(fixture.ViewModel.Grid.Items));
        Assert.Equal(original, fixture.ViewModel.Images);
        fixture.ViewModel.Grid.SearchText = string.Empty;
        fixture.ViewModel.Grid.SortBy("session");
        fixture.ViewModel.Grid.SortBy("session");
        Assert.Equal(["second", "first"], fixture.ViewModel.Grid.Items.Select(image => image.SessionName));
        Assert.Equal(original, fixture.ViewModel.Images);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalOfSameNamedImagesUsesTheirOwningSessions(bool bulk)
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var images = fixture.ViewModel.Images.ToArray();
        Assert.Equal(["first", "second"], images.Select(image => image.SessionName));

        if (bulk)
            await fixture.ViewModel.BulkRemoveAsync(images);
        else
            await fixture.ViewModel.RemoveCommand.ExecuteAsync(images[1]);

        Assert.Equal(bulk ? ["first:remove:image-id", "second:remove:image-id"] : ["second:remove:image-id"],
            fixture.Calls.Where(call => call.Contains(":remove:", StringComparison.Ordinal)));
        Assert.Equal("first", fixture.Context.SelectedSessionName);
        Assert.Equal(bulk ? 0 : 1, fixture.ViewModel.Images.Count);
    }

    [Fact]
    public async Task TagPushAndRunProfileUseTheSelectedRowsSession()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();
        var image = fixture.ViewModel.Images.Single(row => row.SessionName == "second");
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is SimpleInputDialog input)
                input.Value = "example:new";
            if (dialog is PushImageDialog push)
                push.Reference = "example:push";
            return ContentDialogResult.Primary;
        };

        await fixture.ViewModel.TagCommand.ExecuteAsync(image);
        image = fixture.ViewModel.Images.Single(row => row.SessionName == "second" && row.Reference == "example:latest");
        await fixture.ViewModel.PushCommand.ExecuteAsync(image);
        await fixture.ViewModel.RunProfileForImageAsync(new RunProfile
        {
            Options = new RunContainerOptions { Image = "example:latest", Detached = true },
        }, image);

        Assert.Contains("second:tag:example:new", fixture.Calls);
        Assert.Contains("second:tag:example:push", fixture.Calls);
        Assert.Contains("second:push:example:push", fixture.Calls);
        Assert.Contains("second:remove:example:push", fixture.Calls);
        Assert.Contains("second:run:example:latest", fixture.Calls);
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("first:", StringComparison.Ordinal));
        Assert.Equal("first", fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task SavingImagesFromDifferentSessionsIsRefusedBeforeWriting()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.RefreshAsync();

        await fixture.ViewModel.SaveImagesAsync(fixture.ViewModel.Images.ToArray(), "archive.tar");

        Assert.DoesNotContain(fixture.Calls, call => call.Contains(":save:", StringComparison.Ordinal));
        Assert.Contains("one session", fixture.Dialogs.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushStopsWhenTheDestinationTagNoLongerPointsToTheSelectedImage()
    {
        using var fixture = new Fixture { WrongInspectedImage = true };
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is PushImageDialog push)
                push.Reference = "example:push";
            return ContentDialogResult.Primary;
        };

        await fixture.ViewModel.PushCommand.ExecuteAsync(
            fixture.ViewModel.Images.Single(row => row.SessionName == "second"));

        Assert.DoesNotContain(fixture.Calls, call => call.Contains(":push:", StringComparison.Ordinal));
        Assert.Contains("resource changed", fixture.Dialogs.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushDoesNotOverwriteAnEquivalentTagOwnedByAnotherImage()
    {
        using var fixture = new Fixture { ConflictingDestination = true };
        await fixture.ViewModel.RefreshAsync();
        fixture.Dialogs.OnShowDialog = dialog =>
        {
            if (dialog is PushImageDialog push)
                push.Reference = "example:push";
            return ContentDialogResult.Primary;
        };

        await fixture.ViewModel.PushCommand.ExecuteAsync(
            fixture.ViewModel.Images.Single(row => row.SessionName == "second" && row.Id == "image-id"));

        Assert.DoesNotContain(fixture.Calls, call => call.Contains(":tag:", StringComparison.Ordinal) ||
            call.Contains(":push:", StringComparison.Ordinal) || call.Contains(":remove:", StringComparison.Ordinal));
        Assert.Contains("different local image", fixture.Dialogs.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WslcSessionInfo _first = new() { ID = 1, CreatorPid = 100, Name = "first" };
        private readonly WslcSessionInfo _second = new() { ID = 2, CreatorPid = 200, Name = "second" };
        private readonly Dictionary<string, List<string>> _references = new()
        {
            ["first"] = ["example:latest"],
            ["second"] = ["example:latest"],
        };
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public List<string> Calls { get; } = [];
        public List<AutomationSessionOwner?> ActivityOwners { get; } = [];
        public bool WrongInspectedImage { get; set; }
        public bool ConflictingDestination { get; set; }
        public bool CancelDestination { get; set; }
        public bool DormantDestination { get; set; }
        public bool FailAfterDialog { get; set; }
        public bool FailSecondContainerInventory { get; set; }
        public int VerifyCalls { get; set; }
        public int StartingChoices { get; private set; }
        public int NonStartingChoices { get; private set; }
        public CombinedSessionViewModel Inventory { get; }
        public ImagesViewModel ViewModel { get; }

        public Fixture()
        {
            Context.Select("first");
            Dialogs.DismissMessage.SetResult();
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = [_first, _second] },
                }),
                _ => throw new InvalidOperationException(method.Name),
            });
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_Registries" => new List<RegistryEntry>(),
                "get_WslcPath" => "engine",
                _ => throw new InvalidOperationException(method.Name),
            });
            var auth = new RegistryAuthRefresher(discovery, Unused<IAzureCliService>(), settings,
                NullLogger<RegistryAuthRefresher>.Instance);
            var policy = NetworkTestProxy.Create<IWslPolicyService>((method, _) => method.Name switch
            {
                nameof(IWslPolicyService.GetPolicy) => new WslPolicySnapshot(true, true,
                    new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, [])),
                _ => throw new InvalidOperationException(method.Name),
            });
            var reader = new WslcSessionInventoryReader(discovery, (session, _) => Client(session), () => "engine",
                NullLogger<WslcSessionInventoryReader>.Instance);
            Inventory = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, Context);
            var actions = new WslcSessionResourceActions(discovery, Context, (session, _, _) => Client(session),
                () => "engine", () => "default", () => { });
            var selector = NetworkTestProxy.Create<IWslcSessionSelectionService>((method, args) => method.Name switch
            {
                nameof(IWslcSessionSelectionService.ChooseAsync) => ChooseDestination(starting: true),
                nameof(IWslcSessionSelectionService.ChooseForLaterAsync) => ChooseDestination(starting: false),
                nameof(IWslcSessionSelectionService.ChooseActiveAsync) => ChooseDestination(starting: false),
                nameof(IWslcSessionSelectionService.VerifyAsync) => VerifyDestinationAsync(),
                nameof(IWslcSessionSelectionService.CreateClient) => Client(((WslcSessionSelection)args[0]!).Generation!),
                _ => throw new InvalidOperationException(method.Name),
            });
            var notifications = NetworkTestProxy.Create<INotificationService>((method, _) =>
                method.Name is nameof(INotificationService.NotifyImagePull) or nameof(INotificationService.NotifyImageBuild)
                    ? null : throw new InvalidOperationException(method.Name));
            var activity = NetworkTestProxy.Create<IActivityLog>((method, args) =>
            {
                if (method.Name is not (nameof(IActivityLog.RecordImagePull) or nameof(IActivityLog.RecordImageBuild)))
                    throw new InvalidOperationException(method.Name);
                ActivityOwners.Add((AutomationSessionOwner?)args![3]);
                return null;
            });
            ViewModel = new ImagesViewModel(discovery, new StatusMonitor(), Dialogs, settings, auth,
                notifications, Unused<IRunProfileStore>(), activity,
                Unused<IImageUpdateService>(), Unused<IRegistryCatalogService>(), policy,
                Unused<IRegistryCredentialStore>(), Unused<IWslcSettingsFileService>(), Context,
                Inventory, actions, (session, _, _) => Client(session), sessionSelection: selector);
        }

        private Task VerifyDestinationAsync()
        {
            VerifyCalls++;
            return FailAfterDialog && VerifyCalls > 1
                ? Task.FromException(new InvalidOperationException("The selected session changed."))
                : Task.CompletedTask;
        }

        private Task<WslcSessionSelection?> ChooseDestination(bool starting)
        {
            if (starting) StartingChoices++; else NonStartingChoices++;
            return Task.FromResult<WslcSessionSelection?>(CancelDestination ? null :
                new(new("second"), "engine", DormantDestination ? null : _second, "second"));
        }

        private IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, args) =>
        {
            var names = _references[session.Name];
            switch (method.Name)
            {
                case nameof(IWslcService.ListImagesAsync):
                    var images = names.Select(reference =>
                    {
                        var parts = reference.Split(':', 2);
                        return new ImageInfo { Id = "image-id", Repository = parts[0], Tag = parts[1] };
                    }).ToList();
                    if (ConflictingDestination && session.Name == "second")
                        images.Add(new ImageInfo { Id = "another-image", Repository = "docker.io/library/example", Tag = "push" });
                    return Task.FromResult<IReadOnlyList<ImageInfo>>(images);
                case nameof(IWslcService.ListContainersAsync):
                    if (FailSecondContainerInventory && session.Name == "second")
                        return Task.FromException<IReadOnlyList<ContainerInfo>>(new IOException("container inventory unavailable"));
                    return Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
                case nameof(IWslcService.ListVolumesAsync):
                    return Task.FromResult<IReadOnlyList<VolumeInfo>>([]);
                case nameof(IWslcService.ListNetworksAsync):
                    return Task.FromResult<IReadOnlyList<NetworkInfo>>([]);
                case nameof(IWslcService.InspectImageAsync):
                    return Task.FromResult(new CommandResult
                    {
                        StandardOutput = WrongInspectedImage ? """[{"Id":"different-id"}]""" : """[{"Id":"image-id"}]""",
                    });
                case nameof(IWslcService.TagImageAsync):
                    Calls.Add($"{session.Name}:tag:{args[1]}");
                    if (!names.Contains((string)args[1]!))
                        names.Add((string)args[1]!);
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.RemoveImageAsync):
                    Calls.Add($"{session.Name}:remove:{args[0]}");
                    if ((string)args[0]! == "image-id")
                        names.Clear();
                    else
                        names.Remove((string)args[0]!);
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.PushImageAsync):
                    Calls.Add($"{session.Name}:push:{args[0]}");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.PullImageAsync):
                    Calls.Add($"{session.Name}:pull:{args[0]}");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.BuildImageAsync):
                    Calls.Add($"{session.Name}:build:{args[1]}");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.LoadImageAsync):
                    Calls.Add($"{session.Name}:load:archive");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.ImportImageAsync):
                    Calls.Add($"{session.Name}:import:archive");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.PruneImagesAsync):
                    Calls.Add($"{session.Name}:prune:archive");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.RunContainerAsync):
                    Calls.Add($"{session.Name}:run:{((RunContainerOptions)args[0]!).Image}");
                    return Task.FromResult(new CommandResult());
                case nameof(IWslcService.SaveImagesAsync):
                    Calls.Add($"{session.Name}:save:{args[1]}");
                    return Task.FromResult(new CommandResult());
                default:
                    throw new InvalidOperationException(method.Name);
            }
        });

        private static T Unused<T>() where T : class =>
            NetworkTestProxy.Create<T>((method, _) => throw new InvalidOperationException(method.Name));

        public void Dispose() => Inventory.Dispose();
    }
}
