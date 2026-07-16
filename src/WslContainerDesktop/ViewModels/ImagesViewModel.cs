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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class ImagesViewModel : ObservableObject
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly INotificationService _notifications;
    private readonly IRunProfileStore _profiles;
    private readonly IActivityLog _activity;
    private readonly IImageUpdateService _updates;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ImageInfo? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => $"{SelectedCount} selected";

    public ObservableCollection<ImageInfo> Images { get; } = new();

    public ImagesViewModel(IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings, RegistryAuthRefresher authRefresher, INotificationService notifications, IRunProfileStore profiles, IActivityLog activity, IImageUpdateService updates)
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
    }

    /// <summary>Saved run profiles that target the given image, for the one-click run submenu.</summary>
    public IReadOnlyList<RunProfile> ProfilesForImage(string image) => _profiles.GetForImage(image);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading images…";
        try
        {
            var images = await _wslc.ListImagesAsync();
            Images.Clear();
            foreach (var image in images.OrderBy(i => i.Repository).ThenBy(i => i.Tag))
            {
                Images.Add(image);
            }

            StatusMessage = $"{Images.Count} image{(Images.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Failed to load images", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        var dialog = new PullImageDialog(_settings.Registries);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var reference = dialog.Reference.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Pulling {reference}… (this can take a while)";
        try
        {
            // Refresh an Azure-backed registry's token just-in-time so the pull authenticates.
            await _authRefresher.EnsureFreshForReferenceAsync(reference);

            var result = await _wslc.PullImageAsync(reference);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Pull failed", result.ErrorText);
                StatusMessage = "Pull failed";
                _notifications.NotifyImagePull(reference, success: false, result.ErrorText);
                _activity.RecordImagePull(reference, success: false, result.ErrorText);
            }
            else
            {
                StatusMessage = $"Pulled {reference}";
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
                await _dialogs.ShowMessageAsync("Pull failed", result.ErrorText);
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
                await _dialogs.ShowMessageAsync("Run failed", result.ErrorText);
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

    [RelayCommand]
    private async Task RemoveAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove image",
            $"Remove image \"{image.Reference}\" ({image.ShortId})?",
            "Remove");
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
                await _dialogs.ShowMessageAsync("Remove failed", result.ErrorText);
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
            "Remove images",
            $"Remove {items.Count} image(s)?\n\n{BulkNames(items.Select(i => i.Reference))}",
            "Remove");
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
                "Some images were not removed",
                $"{failures.Count} of {items.Count} could not be removed (they may be in use by a container):\n\n{BulkNames(failures)}");
        }
        else
        {
            StatusMessage = $"Removed {items.Count} image(s)";
        }
    }

    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? $"{shown}\n… and {list.Count - max} more" : shown;
    }

    [RelayCommand]
    private async Task TagAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var dialog = new SimpleInputDialog(
            "Tag image",
            "New tag",
            "e.g. myrepo/myimage:v1")
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
                await _dialogs.ShowMessageAsync("Tag failed", result.ErrorText);
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

    [RelayCommand]
    private async Task PushAsync(ImageInfo? image)
    {
        image ??= Selected;
        if (image is null)
        {
            return;
        }

        var dialog = new PushImageDialog(_settings.Registries, image.Reference);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var reference = dialog.Reference.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Pushing {reference}… (this can take a while)";

        // Snapshot the names this image already carries so cleanup only removes an alias we
        // actually added — never a tag the user already had. Comparing the engine's own
        // normalized references catches collisions that raw string equality would miss
        // (e.g. Docker Hub where "nginx:1.0" resolves to "docker.io/library/nginx:1.0").
        var namesBefore = await ReferencesForImageAsync(image.Id);
        var transientAliases = new List<string>();
        try
        {
            await _authRefresher.EnsureFreshForReferenceAsync(reference);

            // A registry destination is encoded in the image name, and wslc can only push a
            // reference that exists locally. Add the fully-qualified name as an alias for the
            // push; it copies no data — just another pointer to the same image content.
            var tagResult = await _wslc.TagImageAsync(image.Id, reference);
            if (!tagResult.Success)
            {
                await _dialogs.ShowMessageAsync("Push failed",
                    $"Couldn't tag the image as {reference}.\n\n{tagResult.ErrorText}");
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

            var result = await _wslc.PushImageAsync(reference);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Push failed", result.ErrorText);
                StatusMessage = "Push failed";
            }
            else
            {
                StatusMessage = $"Pushed {reference}";
                await _dialogs.ShowMessageAsync("Push complete",
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? $"Pushed {reference}."
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
            await _dialogs.ShowMessageAsync($"Inspect · {image.Reference}",
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
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
                await _dialogs.ShowMessageAsync("Build failed", result.ErrorText);
                StatusMessage = "Build failed";
                _notifications.NotifyImageBuild(dialog.ImageTag, success: false, result.ErrorText);
                _activity.RecordImageBuild(dialog.ImageTag, success: false, result.ErrorText);
            }
            else
            {
                _notifications.NotifyImageBuild(dialog.ImageTag, success: true);
                _activity.RecordImageBuild(dialog.ImageTag, success: true);
                await _dialogs.ShowMessageAsync("Build complete", result.StandardOutput);
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Prune images",
            "Remove all dangling (unused) images?",
            "Prune");
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
                await _dialogs.ShowMessageAsync("Prune failed", result.ErrorText);
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
}
