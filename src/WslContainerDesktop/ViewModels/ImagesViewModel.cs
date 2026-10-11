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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Backs the Images page, where users pull, build, tag, push, save, restore and remove container images through the WSL container engine.</summary>
public partial class ImagesViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => UiText.Translate(StatusMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BuildPolicyMessageDisplay => UiText.Translate(BuildPolicyMessage);

    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly INotificationService _notifications;
    private readonly IRunProfileStore _profiles;
    private readonly IActivityLog _activity;
    private readonly IImageUpdateService _updates;
    private readonly IRegistryCatalogService _catalog;
    private readonly IWslPolicyService _policy;
    private readonly IRegistryCredentialStore _credentials;
    private readonly IWslcSettingsFileService _wslcSettings;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    /// <summary>Value for selected shown or edited by the view.</summary>
    [ObservableProperty]
    private ImageInfo? _selected;

    /// <summary>Whether selection mode for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    /// <summary>Bindable state for selected count used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Whether the user can build from the view.</summary>
    [ObservableProperty]
    private bool _canBuild = true;

    /// <summary>Bindable state for build policy message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildPolicyMessageDisplay))]
    private string _buildPolicyMessage = "Build an image from a Dockerfile";

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => UiText.Get("Resource_Text_e5084bffcc17", "{0} selected", SelectedCount);

    /// <summary>Value for images shown or edited by the view.</summary>
    public ObservableCollection<ImageInfo> Images { get; } = new();

    /// <summary>Creates the Images view model and stores its injected services.</summary>
    public ImagesViewModel(IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings, RegistryAuthRefresher authRefresher, INotificationService notifications, IRunProfileStore profiles, IActivityLog activity, IImageUpdateService updates, IRegistryCatalogService catalog, IWslPolicyService policy, IRegistryCredentialStore credentials, IWslcSettingsFileService wslcSettings)
    {
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _notifications = notifications;
        _profiles = profiles;
        _activity = activity;
        _updates = updates;
        _catalog = catalog;
        _policy = policy;
        _credentials = credentials;
        _wslcSettings = wslcSettings;
    }

    /// <summary>Saved run profiles that target the given image, for the one-click run submenu.</summary>
    public IReadOnlyList<RunProfile> ProfilesForImage(string image) => _profiles.GetForImage(image);

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        RefreshPolicyState();
        IsBusy = true;
        if (Images.Count == 0)
        {
            StatusMessage = "Loading images…";
        }

        try
        {
            // Both calls are independent engine round trips, so run them together. The usage lookup
            // never faults, so a failed image listing doesn't leave it unobserved.
            var imagesTask = _wslc.ListImagesAsync();
            var containersTask = ListContainersForUsageAsync();
            var images = await imagesTask;

            // Which container holds each image. A dangling row otherwise reads "<none> <none>",
            // which says an image is untagged but not why it is still on disk or why removing it
            // fails — and the answer is almost always a container still referencing it.
            if (await containersTask is { } containers)
            {
                try
                {
                    ImageUsageResolver.Apply(images, containers);
                }
                catch (Exception)
                {
                    // Same reason as ListContainersForUsageAsync: usage is optional annotation.
                }
            }

            CollectionSync.ReplaceAll(Images, images.OrderBy(i => i.Repository).ThenBy(i => i.Tag).ToList());
            StatusMessage = $"{Images.Count} image{(Images.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_68d903613f94", "Failed to load images"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<ContainerInfo>?> ListContainersForUsageAsync()
    {
        try
        {
            return await _wslc.ListContainersAsync(all: true);
        }
        catch (Exception)
        {
            // Deliberately silent: usage is an annotation on the listing, not the listing
            // itself, and the rows are still correct and actionable without it.
            return null;
        }
    }

    /// <summary>Command handler for pull actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PullAsync()
    {
        var dialog = new PullImageDialog(_settings.Registries, _catalog);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var reference = dialog.Reference.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        if (dialog.AllTags)
        {
            reference = WslcService.StripTag(reference);
        }

        IsBusy = true;
        StatusMessage = dialog.AllTags
            ? $"Pulling all tags of {reference}… (this can take a while)"
            : $"Pulling {reference}… (this can take a while)";
        try
        {
            // Refresh an Azure-backed registry's token just-in-time so the pull authenticates.
            await _authRefresher.EnsureFreshForReferenceAsync(reference);

            var result = await _wslc.PullImageAsync(reference, allTags: dialog.AllTags);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_8ee0e62563d4", "Pull failed"), result.ErrorText);
                StatusMessage = "Pull failed";
                _notifications.NotifyImagePull(reference, success: false, result.ErrorText);
                _activity.RecordImagePull(reference, success: false, result.ErrorText);
            }
            else
            {
                StatusMessage = dialog.AllTags ? $"Pulled all tags for {reference}" : $"Pulled {reference}";
                _notifications.NotifyImagePull(reference, success: true);
                _activity.RecordImagePull(reference, success: true);
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Checks every tagged image against its upstream registry digest and updates each row's
    /// <see cref="ImageInfo.UpdateState"/> so an "update available" badge can show. Runs the checks
    /// concurrently; images with no tag or no registry digest are skipped.
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        var candidates = Images
            .Where(i => !string.IsNullOrEmpty(i.Tag) && i.Tag != "<none>")
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        StatusMessage = "Checking for image updates…";
        foreach (var image in candidates)
        {
            image.UpdateState = ImageUpdateState.Checking;
        }

        var tasks = candidates.Select(async image =>
        {
            try
            {
                var digests = await _wslc.GetImageRepoDigestsAsync(image.Id);
                image.UpdateState = await _updates.CheckAsync(image.Reference, digests);
            }
            catch
            {
                image.UpdateState = ImageUpdateState.CheckFailed;
            }
        });

        await Task.WhenAll(tasks);

        var available = candidates.Count(i => i.UpdateState == ImageUpdateState.UpdateAvailable);
        StatusMessage = available == 0
            ? "All images are up to date."
            : $"{available} image{(available == 1 ? "" : "s")} can be updated.";
    }

    /// <summary>Pulls the latest image for a row that has an update available, then re-checks it.</summary>
    [RelayCommand]
    private async Task PullUpdateAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null || string.IsNullOrWhiteSpace(image.Reference))
        {
            return;
        }

        var reference = image.Reference;
        IsBusy = true;
        StatusMessage = $"Pulling {reference}…";
        try
        {
            await _authRefresher.EnsureFreshForReferenceAsync(reference);

            var result = await _wslc.PullImageAsync(reference);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_8ee0e62563d4", "Pull failed"), result.ErrorText);
                StatusMessage = "Pull failed";
                _notifications.NotifyImagePull(reference, success: false, result.ErrorText);
                _activity.RecordImagePull(reference, success: false, result.ErrorText);
                return;
            }

            StatusMessage = $"Updated {reference}";
            _notifications.NotifyImagePull(reference, success: true);
            _activity.RecordImagePull(reference, success: true);
            await RefreshAsync();

            // Re-check the freshly pulled tag so the badge clears.
            var updated = Images.FirstOrDefault(i =>
                string.Equals(i.Reference, reference, StringComparison.Ordinal));
            if (updated is not null)
            {
                updated.UpdateState = ImageUpdateState.Checking;
                try
                {
                    var digests = await _wslc.GetImageRepoDigestsAsync(updated.Id);
                    updated.UpdateState = await _updates.CheckAsync(updated.Reference, digests);
                }
                catch
                {
                    updated.UpdateState = ImageUpdateState.CheckFailed;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for run actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RunAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var dialog = new RunContainerDialog(_wslc, _settings.Registries, _profiles, image.Reference);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Options is null)
        {
            return;
        }

        await ExecuteRunAsync(dialog.Options);
    }

    /// <summary>Command handler for run profile actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RunProfileAsync(RunProfile? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Options.Image))
        {
            return;
        }

        await ExecuteRunAsync(profile.Options);
    }

    /// <summary>Launches a container from resolved run options and refreshes the monitor.</summary>
    private async Task ExecuteRunAsync(RunContainerOptions options)
    {
        IsBusy = true;
        StatusMessage = $"Running {options.Image}…";
        try
        {
            // `wslc run` auto-pulls if the image is absent, so refresh Azure auth first.
            await _authRefresher.EnsureFreshForReferenceAsync(options.Image);

            var result = await _wslc.RunContainerAsync(options);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_9a731f3d6dec", "Run failed"), result.ErrorText);
                StatusMessage = "Run failed";
            }
            else
            {
                StatusMessage = "Container started";
                _monitor.RequestRefresh();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for remove actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RemoveAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_5f94b03c2e6a", "Remove image"),
            UiText.Get("Resource_Text_92bffa1208b8", "Remove image \"{0}\" ({1})?", image.Reference, image.ShortId),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Removing {image.Reference}…";
        try
        {
            var result = await _wslc.RemoveImageAsync(image.Id);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_debe01de259b", "Remove failed"), result.ErrorText);
                StatusMessage = "Remove failed";
            }
            else
            {
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Handles is selection mode changed changes and updates related view-model state.</summary>
    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            SelectedCount = 0;
        }
    }

    /// <summary>Removes every selected image after one confirmation, then exits selection mode.</summary>
    public async Task BulkRemoveAsync(IReadOnlyList<ImageInfo> images)
    {
        var items = images?.Where(i => i is not null).ToList() ?? new List<ImageInfo>();
        if (items.Count == 0)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_aea8cc2d34a5", "Remove images"),
            UiText.Get("Resource_Text_eb37f372523b", "Remove {0} image(s)?\n\n{1}", items.Count, BulkNames(items.Select(i => i.Reference))),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Removing {items.Count} image(s)…";
        var failures = new List<string>();
        try
        {
            foreach (var image in items)
            {
                var result = await _wslc.RemoveImageAsync(image.Id);
                if (!result.Success)
                {
                    failures.Add(image.Reference);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        IsSelectionMode = false;
        await RefreshAsync();

        if (failures.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Resource_Text_7800478a75f7", "Some images were not removed"),
                UiText.Get("Resource_Text_ca3ab35903c6", "{0} of {1} could not be removed (they may be in use by a container):\n\n{2}", failures.Count, items.Count, BulkNames(failures)));
        }
        else
        {
            StatusMessage = $"Removed {items.Count} image(s)";
        }
    }

    /// <summary>Helper for the bulk names workflow in this view model.</summary>
    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? UiText.Get("Resource_Text_5b44479e9557", "{0}\n… and {1} more", shown, list.Count - max) : shown;
    }

    /// <summary>Command handler for tag actions triggered from the view.</summary>
    [RelayCommand]
    private async Task TagAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var dialog = new SimpleInputDialog(
            UiText.Get("Resource_Text_95562ee52bdb", "Tag image"),
            UiText.Get("Resource_Text_0a6181aadd01", "New tag"),
            UiText.Get("Resource_Text_3ad374153994", "e.g. myrepo/myimage:v1"))
        {
            Value = image.Reference,
        };
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var target = dialog.Value.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.TagImageAsync(image.Id, target);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_b5d06eb9a6ed", "Tag failed"), result.ErrorText);
            }
            else
            {
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for push actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PushAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var dialog = new PushImageDialog(_settings.Registries, image.Reference, CheckPushSignInAsync);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            if (dialog.OpenRegistriesRequested)
            {
                App.Current.MainWindow?.NavigateTo("registries");
            }

            return;
        }

        var reference = dialog.Reference.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        IsBusy = true;
        var allTags = dialog.AllTags;
        var pushReference = allTags ? WslcService.StripTag(reference) : reference;
        StatusMessage = allTags
            ? $"Pushing all tags for {pushReference}… (this can take a while)"
            : $"Pushing {reference}… (this can take a while)";

        // Snapshot the names this image already carries so cleanup only removes an alias we
        // actually added — never a tag the user already had. Comparing the engine's own
        // normalized references catches collisions that raw string equality would miss
        // (e.g. Docker Hub where "nginx:1.0" resolves to "docker.io/library/nginx:1.0").
        var namesBefore = await ReferencesForImageAsync(image.Id);
        var transientAliases = new List<string>();
        try
        {
            await _authRefresher.EnsureFreshForReferenceAsync(pushReference);

            // A registry destination is encoded in the image name, and wslc can only push a
            // reference that exists locally. Add the fully-qualified name as an alias for the
            // push; it copies no data — just another pointer to the same image content.
            var tagResult = await _wslc.TagImageAsync(image.Id, reference);
            if (!tagResult.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0ef1f1bf6b99", "Push failed"),
                    UiText.Get("Resource_Text_b9f7af9f92c3", "Couldn't tag the image as {0}.\n\n{1}", reference, tagResult.ErrorText));
                StatusMessage = "Push failed";
                return;
            }

            // Whatever new reference the tag introduced is the transient alias to undo later.
            // If the reference already existed, the tag was a no-op and nothing is removed.
            // The Count guard ensures we only trust a valid pre-push snapshot before diffing.
            if (namesBefore.Count > 0)
            {
                var namesAfter = await ReferencesForImageAsync(image.Id);
                transientAliases = namesAfter.Except(namesBefore, StringComparer.Ordinal).ToList();
            }

            var result = await _wslc.PushImageAsync(reference, allTags: allTags);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0ef1f1bf6b99", "Push failed"), result.ErrorText);
                StatusMessage = "Push failed";
            }
            else
            {
                StatusMessage = allTags ? $"Pushed all tags for {pushReference}" : $"Pushed {reference}";
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_3c60d06bc185", "Push complete"),
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? (allTags ? UiText.Get("Resource_Text_d443bd63ddee", "Pushed all tags for {0}.", pushReference) : UiText.Get("Resource_Text_6bbef811d085", "Pushed {0}.", reference))
                        : result.StandardOutput.Trim());
            }

        }
        finally
        {
            // Remove only the aliases the tag step introduced, so the local image keeps the
            // exact names it had before the push. Untagging a still-multi-referenced image
            // never deletes its content or the user's original tags.
            foreach (var alias in transientAliases)
            {
                await _wslc.RemoveImageAsync(alias, force: true);
            }

            IsBusy = false;
        }
    }

    /// <summary>Provides the save images operation to views or collaborating view models.</summary>
    public async Task SaveImagesAsync(IReadOnlyList<ImageInfo> images, string outputPath)
    {
        var items = images.Where(i => i is not null).ToList();
        if (items.Count == 0)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Saving {items.Count} image{(items.Count == 1 ? "" : "s")}…";
        try
        {
            // Tagged images save by reference so the archive keeps their names; untagged and
            // intermediate images ("<none>") can only be addressed by ID.
            var targets = items
                .Select(i => i.Repository is "" or "<none>" || i.Tag is "" or "<none>" ? i.Id : i.Reference)
                .ToList();
            var result = await _wslc.SaveImagesAsync(targets, outputPath);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0a4444676244", "Save failed"), result.ErrorText);
                StatusMessage = "Save failed";
                return;
            }

            StatusMessage = $"Saved {items.Count} image{(items.Count == 1 ? "" : "s")} to {Path.GetFileName(outputPath)}. To bring {(items.Count == 1 ? "it" : "them")} back, use Import → Restore saved images….";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refreshes load image state for the view model.</summary>
    public async Task LoadImageAsync(string inputPath)
    {
        IsBusy = true;
        StatusMessage = "Restoring saved images…";
        try
        {
            var result = await _wslc.LoadImageAsync(inputPath);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_7c7a8c1ac2c8", "Couldn't restore images"), result.ErrorText);
                StatusMessage = "Load failed";
                return;
            }

            StatusMessage = "Saved images restored";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Provides the import image operation to views or collaborating view models.</summary>
    public async Task ImportImageAsync(string inputPath)
    {
        var dialog = new SimpleInputDialog(
            UiText.Get("Resource_Text_043660976a99", "Create image from exported files"),
            UiText.Get("Resource_Text_47e73c701cb3", "Name for the new image (optional; without one it is listed as <none>)"),
            UiText.Get("Resource_Text_73b3566a1003", "e.g. my-snapshot:latest"))
        {
            Value = string.Empty,
        };
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var image = dialog.Value.Trim();
        IsBusy = true;
        StatusMessage = string.IsNullOrEmpty(image) ? "Creating image from exported files…" : $"Creating {image}…";
        try
        {
            var result = await _wslc.ImportImageAsync(inputPath, string.IsNullOrEmpty(image) ? null : image);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_6d490981d8f3", "Couldn't create the image"), result.ErrorText);
                StatusMessage = "Import failed";
                return;
            }

            StatusMessage = "Image created from exported files";
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The set of image references (names) currently pointing at the given image id, as the
    /// engine reports them (already normalized). Used to detect which alias a tag step adds so
    /// only that alias is later removed. Failures degrade to an empty set (cleanup is skipped).
    /// </summary>
    private async Task<HashSet<string>> ReferencesForImageAsync(string imageId)
    {
        var all = await _wslc.ListImagesAsync();
        return all
            .Where(i => string.Equals(i.Id, imageId, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Reference)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Command handler for inspect actions triggered from the view.</summary>
    [RelayCommand]
    private async Task InspectAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.InspectImageAsync(image.Id);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_f306c4f01fc3", "Inspect · {0}", image.Reference),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for build actions triggered from the view.</summary>
    [RelayCommand]
    private async Task BuildAsync()
    {
        RefreshPolicyState();
        if (!CanBuild)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_be5db383a1cb", "Build blocked by policy"), BuildPolicyMessage);
            return;
        }

        var dialog = new BuildImageDialog(_settings.Registries);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Building {dialog.ImageTag}…";
        try
        {
            var result = await _wslc.BuildImageAsync(dialog.ContextPath, dialog.ImageTag, dialog.Dockerfile);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_2f8e461f4b9f", "Build failed"), result.ErrorText);
                StatusMessage = "Build failed";
                _notifications.NotifyImageBuild(dialog.ImageTag, success: false, result.ErrorText);
                _activity.RecordImageBuild(dialog.ImageTag, success: false, result.ErrorText);
            }
            else
            {
                _notifications.NotifyImageBuild(dialog.ImageTag, success: true);
                _activity.RecordImageBuild(dialog.ImageTag, success: true);
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_9e1ba089f927", "Build complete"), result.StandardOutput);
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sign-in check for the Push dialog. Docker Hub is read from the engine's stored credential
    /// (an anonymous probe can't tell the difference); other registries are probed, and Azure
    /// registries are silently re-authenticated first so an expired token doesn't read as signed out.
    /// </summary>
    private async Task<(RegistryLoginState State, string? User)> CheckPushSignInAsync(RegistryEntry registry)
    {
        if (registry.IsDefault)
        {
            if (_credentials.IsLoggedIn(registry.LoginServer, out var user))
                return (RegistryLoginState.LoggedIn, user);

            // Only the "wincred" store (wslc's default) can be read here. With "file", a missing
            // Windows credential proves nothing, so report "couldn't confirm" rather than block the push.
            return await UsesWindowsCredentialStoreAsync()
                ? (RegistryLoginState.Anonymous, null)
                : (RegistryLoginState.Unknown, null);
        }

        var state = await _wslc.ProbeRegistryLoginAsync(registry.Host, "wslcd-login-probe");
        if (state != RegistryLoginState.LoggedIn && registry.IsAzure && await _authRefresher.RefreshAsync(registry))
        {
            state = RegistryLoginState.LoggedIn;
        }

        registry.LoginState = state;
        return (state, registry.Username);
    }

    /// <summary>
    /// True when wslc keeps registry logins in Windows Credential Manager, which is the only store
    /// the app can read. An unreadable settings file counts as the default (wincred).
    /// </summary>
    private async Task<bool> UsesWindowsCredentialStoreAsync()
    {
        try
        {
            var store = (await _wslcSettings.ReadAsync()).CredentialStore;
            return string.IsNullOrWhiteSpace(store) || store.Trim().Equals("wincred", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // The default is wincred, so an unreadable settings file keeps the normal sign-in check.
            System.Diagnostics.Debug.WriteLine($"Could not read the wslc credential store setting: {ex.Message}");
            return true;
        }
    }

    /// <summary>Refreshes policy state for the view model.</summary>
    private void RefreshPolicyState()
    {
        var message = WslRegistryPolicyGuard.ValidateBuild(_policy.GetPolicy());
        CanBuild = message is null;
        BuildPolicyMessage = message ?? "Build an image from a Dockerfile";
    }

    /// <summary>Command handler for prune actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_58d3b07833b2", "Prune images"),
            UiText.Get("Resource_Text_c2370bb291a5", "Remove all dangling (unused) images?"),
            UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.PruneImagesAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
            }
            else
            {
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        foreach (var item in Images) item.RefreshLocalizedText();
    }
}
