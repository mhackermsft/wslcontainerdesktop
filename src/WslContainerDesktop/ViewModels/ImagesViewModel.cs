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
    private readonly IRunProfileStore _profiles;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ImageInfo? _selected;

    public ObservableCollection<ImageInfo> Images { get; } = new();

    public ImagesViewModel(IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings, RegistryAuthRefresher authRefresher, IRunProfileStore profiles)
    {
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _profiles = profiles;
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
            }
            else
            {
                StatusMessage = $"Pulled {reference}";
                await RefreshAsync();
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
    private async Task ImportComposeAsync()
    {
        var dialog = new ImportComposeDialog();
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        IReadOnlyList<RunProfile> parsed;
        try
        {
            parsed = ComposeImporter.Parse(dialog.Yaml);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Import failed", ex.Message);
            return;
        }

        if (parsed.Count == 0)
        {
            await _dialogs.ShowMessageAsync(
                "Nothing to import",
                "No services with an image were found in the compose file.");
            return;
        }

        foreach (var profile in parsed)
        {
            _profiles.Save(profile);
        }

        StatusMessage = $"Imported {parsed.Count} profile{(parsed.Count == 1 ? "" : "s")}";
        await _dialogs.ShowMessageAsync(
            "Compose imported",
            $"Saved {parsed.Count} run profile{(parsed.Count == 1 ? "" : "s")}: " +
            string.Join(", ", parsed.Select(p => p.Name)) +
            ".\n\nRun one from an image's ⋯ menu, or load it in the Run dialog.");
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
        try
        {
            await _authRefresher.EnsureFreshForReferenceAsync(reference);

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
            IsBusy = false;
        }
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
            }
            else
            {
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
