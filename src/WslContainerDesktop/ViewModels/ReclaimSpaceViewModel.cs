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
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the "Reclaim space" page: a holistic disk-usage &amp; cleanup center that
/// summarizes how much space images, containers, and volumes consume and offers
/// one-click, confirmed pruning that reuses the existing <c>Prune*</c> service methods.
/// </summary>
/// <summary>View model for the Reclaim Space page, summarizing images, stopped containers, and volumes that may free disk space.</summary>
public partial class ReclaimSpaceViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => UiText.Translate(StatusMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StorageLocationDisplay => UiText.Translate(StorageLocation);

    /// <summary>How many of the largest images to surface in the "largest images" list.</summary>
    private const int TopImageCount = 5;

    private readonly IWslcService _wslc;
    private readonly IWslcSettingsFileService _wslcSettings;
    private readonly DialogService _dialogs;
    private readonly ILogger<ReclaimSpaceViewModel> _logger;

    private long _imagesTotalBytes;
    private long _imagesReclaimableBytes;

    /// <summary>Generated busy flag used while scanning or pruning disk usage.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status text shown at the top of the Reclaim Space page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    // ---- Images ----
    /// <summary>Total images reported by <c>wslc</c>.</summary>
    [ObservableProperty]
    private int _imageCount;

    /// <summary>Dangling images that are not currently held by a container and may be pruned.</summary>
    [ObservableProperty]
    private int _danglingImageCount;

    /// <summary>
    /// Dangling images a container still holds. They are listed, because they are real untagged
    /// images taking real space, but they are not counted as reclaimable: nothing can remove them
    /// until whatever is using them goes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRetainedDanglingImages))]
    [NotifyPropertyChangedFor(nameof(RetainedDanglingNote))]
    private int _retainedDanglingImageCount;

    /// <summary>True when some dangling images are still referenced by containers.</summary>
    public bool HasRetainedDanglingImages => RetainedDanglingImageCount > 0;

    /// <summary>Explanation shown when dangling images cannot yet be reclaimed.</summary>
    public string RetainedDanglingNote => RetainedDanglingImageCount == 1
        ? "1 dangling image is still used by a container and cannot be removed yet."
        : $"{RetainedDanglingImageCount} dangling images are still used by containers and cannot be removed yet.";

    /// <summary>Human-readable total image size.</summary>
    [ObservableProperty]
    private string _imagesTotalSize = "0 B";

    /// <summary>Human-readable upper bound for reclaimable dangling image space.</summary>
    [ObservableProperty]
    private string _imagesReclaimableSize = "0 B";

    // ---- Containers ----
    /// <summary>Total containers counted during the scan.</summary>
    [ObservableProperty]
    private int _containerCount;

    /// <summary>Stopped containers whose writable layers may be removed.</summary>
    [ObservableProperty]
    private int _stoppedContainerCount;

    /// <summary>Human-readable writable-layer space held by stopped containers.</summary>
    [ObservableProperty]
    private string _stoppedContainersReclaimableSize = "0 B";

    // ---- Volumes ----
    /// <summary>Total volumes counted during the scan.</summary>
    [ObservableProperty]
    private int _volumeCount;

    /// <summary>Volumes resolved as unused by the volume usage resolver.</summary>
    [ObservableProperty]
    private int _unusedVolumeCount;

    // ---- Totals ----
    /// <summary>Human-readable total of reclaimable image and stopped-container space.</summary>
    [ObservableProperty]
    private string _totalReclaimableSize = "0 B";

    /// <summary>Effective WSL container storage path read from the settings file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageLocationDisplay))]
    private string _storageLocation = "Unknown";

    /// <summary>The largest images by on-disk size (top <see cref="TopImageCount"/>).</summary>
    public ObservableCollection<ImageInfo> LargestImages { get; } = new();

    /// <summary>Dangling (untagged) images, which a plain image prune removes.</summary>
    public ObservableCollection<ImageInfo> DanglingImages { get; } = new();

    /// <summary>Volumes unused in a complete inspect snapshot; the engine decides prune eligibility.</summary>
    public ObservableCollection<VolumeInfo> UnusedVolumes { get; } = new();

    /// <summary>Creates the reclaim-space model with <c>wslc</c>, settings, dialog, and logging services.</summary>
    public ReclaimSpaceViewModel(IWslcService wslc, IWslcSettingsFileService wslcSettings, DialogService dialogs, ILogger<ReclaimSpaceViewModel> logger)
    {
        _wslc = wslc;
        _wslcSettings = wslcSettings;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>An image is "dangling" when it has no repository or tag (i.e. <c>&lt;none&gt;</c>).</summary>
    internal static bool IsDangling(ImageInfo image) =>
        IsNone(image.Repository) || IsNone(image.Tag);

    private static bool IsNone(string value) =>
        string.IsNullOrEmpty(value) || value.Equals("<none>", StringComparison.OrdinalIgnoreCase);

    /// <summary>Scans images, containers, and volumes to estimate safely reclaimable space.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        refresh.CancelAfter(TimeSpan.FromSeconds(45));
        ct = refresh.Token;
        IsBusy = true;
        StatusMessage = "Calculating disk usage…";
        try
        {
            var images = await _wslc.ListImagesAsync(ct);
            var settingsFile = await _wslcSettings.ReadAsync(ct);
            StorageLocation = settingsFile.UsesDefaultStorage
                ? $"Default ({settingsFile.DefaultStoragePath})"
                : settingsFile.EffectiveStoragePath;
            var containers = await _wslc.ListContainersAsync(all: true, ct: ct, includeSize: true);
            var volumes = (await _wslc.ListVolumesAsync(ct)).ToList();
            foreach (var volume in volumes)
            {
                var inspect = await _wslc.InspectVolumeAsync(volume.Name, ct);
                if (inspect.Success)
                {
                    volume.EnrichFromInspect(inspect.StandardOutput);
                }
                else
                {
                    _logger.LogWarning("Could not inspect volume {Volume}: {Error}", volume.Name, inspect.ErrorText);
                }
            }
            var warnings = await VolumeUsageResolver.ResolveAsync(volumes, containers,
                (id, token) => _wslc.InspectContainerAsync(id, token), ct);
            foreach (var warning in warnings)
            {
                _logger.LogWarning("Volume usage: {Warning}", warning);
            }
            ct.ThrowIfCancellationRequested();

            // Images. Sizes are per-image and may share layers, so the totals are an upper
            // bound (matching how `df`-style views typically present them).
            //
            // An image a container still references cannot be pruned, and a pull that moves a tag
            // leaves the previous image untagged, so an image can be dangling *and* in use at once.
            // Counting those as reclaimable promised space that no prune could free: the prune
            // reported nothing reclaimed and the row stayed, which reads as a broken refresh.
            ImageUsageResolver.Apply(images, containers);
            var dangling = images.Where(IsDangling).ToList();
            var reclaimable = dangling.Where(i => !i.IsInUse).ToList();
            _imagesTotalBytes = images.Sum(i => i.Size);
            _imagesReclaimableBytes = reclaimable.Sum(i => i.Size);

            ImageCount = images.Count;
            DanglingImageCount = reclaimable.Count;
            RetainedDanglingImageCount = dangling.Count - reclaimable.Count;
            ImagesTotalSize = FormatHelpers.HumanSize(_imagesTotalBytes);
            ImagesReclaimableSize = FormatHelpers.HumanSize(_imagesReclaimableBytes);

            LargestImages.Clear();
            foreach (var image in images.OrderByDescending(i => i.Size).Take(TopImageCount))
            {
                LargestImages.Add(image);
            }

            DanglingImages.Clear();
            foreach (var image in dangling.OrderByDescending(i => i.Size))
            {
                DanglingImages.Add(image);
            }

            ContainerCount = containers.Count;
            var stoppedContainers = containers.Where(c => c.State != ContainerState.Running).ToList();
            StoppedContainerCount = stoppedContainers.Count;
            var stoppedWritableBytes = stoppedContainers.Sum(c => c.SizeRwBytes ?? 0);
            StoppedContainersReclaimableSize = FormatHelpers.HumanSize(stoppedWritableBytes);

            // Never infer reclaimable storage from missing metadata or legacy estimates.
            var unused = volumes.Where(v => v.UsageState == VolumeUsageState.Unused).ToList();
            VolumeCount = volumes.Count;
            UnusedVolumeCount = unused.Count;

            UnusedVolumes.Clear();
            foreach (var volume in unused)
            {
                UnusedVolumes.Add(volume);
            }

            TotalReclaimableSize = FormatHelpers.HumanSize(_imagesReclaimableBytes + stoppedWritableBytes);
            StatusMessage = _imagesReclaimableBytes + stoppedWritableBytes > 0
                ? $"Up to {TotalReclaimableSize} reclaimable from dangling images and stopped containers"
                : "Nothing obvious to reclaim";
            if (warnings.Count > 0)
            {
                StatusMessage += " - volume usage incomplete; unknown volumes are not counted as unused";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = "Disk usage refresh cancelled or timed out; displayed data may be stale";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_272b9b240c75", "Failed to calculate disk usage"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Removes dangling images that are not currently used by containers.</summary>
    [RelayCommand]
    private async Task PruneImagesAsync()
    {
        if (DanglingImageCount == 0)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_6257dda9c3b7", "Remove dangling images"), UiText.Get("Resource_Text_fb107b55608b", "There are no dangling images to remove."));
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_6257dda9c3b7", "Remove dangling images"),
            UiText.Get("Resource_Text_bb93366308e4", "Remove {0}, reclaiming up to {1}?", Count(DanglingImageCount, "dangling image"), ImagesReclaimableSize),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = await SumImageBytesAsync();
            var result = await _wslc.PruneImagesAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
                return;
            }

            var freed = Math.Max(0, before - await SumImageBytesAsync());
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_e8b433d94d09", "Images pruned"), UiText.Get("Resource_Text_447d795e6433", "Reclaimed {0}.", FormatHelpers.HumanSize(freed)));
        });
    }

    /// <summary>Removes stopped containers after confirmation.</summary>
    [RelayCommand]
    private async Task PruneContainersAsync()
    {
        if (StoppedContainerCount == 0)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_5affb4df88db", "Remove stopped containers"), UiText.Get("Resource_Text_0cb1ba8ea3a2", "There are no stopped containers to remove."));
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_5affb4df88db", "Remove stopped containers"),
            UiText.Get("Resource_Text_df08e49b2d70", "Remove all stopped containers ({0} candidate(s)), reclaiming about {1} from writable layers?", StoppedContainerCount, StoppedContainersReclaimableSize),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = (await _wslc.ListContainersAsync(all: true, includeSize: true)).ToList();
            var beforeBytes = before.Where(c => c.State != ContainerState.Running).Sum(c => c.SizeRwBytes ?? 0);
            var result = await _wslc.PruneContainersAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
                return;
            }

            var after = await _wslc.ListContainersAsync(all: true, includeSize: true);
            var removed = Math.Max(0, before.Count - after.Count);
            var afterBytes = after.Where(c => c.State != ContainerState.Running).Sum(c => c.SizeRwBytes ?? 0);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_7b1133fe472d", "Containers pruned"),
                UiText.Get("Resource_Text_c1b7111021d0", "Removed {0}, reclaiming about {1}.", Count(removed, "container"), FormatHelpers.HumanSize(Math.Max(0, beforeBytes - afterBytes))));
        });
    }

    /// <summary>Asks the engine to remove volumes it considers unused.</summary>
    [RelayCommand]
    private async Task PruneVolumesAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_1b82a339cce8", "Remove unused volumes"),
            UiText.Get("Resource_Text_7a4d99920018", "Remove all unused volumes? Any data they hold will be lost.\n\n") +
            UiText.Get("Resource_Text_fbee6d8e80d2", "Usage is a snapshot, including stopped containers when metadata is available. ") +
            UiText.Get("Resource_Text_2eccf3bfe77f", "Unknown usage is not proof of eligibility; the engine determines which volumes can be pruned."),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = (await _wslc.ListVolumesAsync()).Count;
            var result = await _wslc.PruneVolumesAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
                return;
            }

            var removed = Math.Max(0, before - (await _wslc.ListVolumesAsync()).Count);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_1bdc17c5e2f9", "Volumes pruned"), UiText.Get("Resource_Text_5e34cda5a434", "Removed {0}.", Count(removed, "volume")));
        });
    }

    /// <summary>Runs the combined image, container, and volume cleanup flow.</summary>
    [RelayCommand]
    private async Task PruneAllAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_4f75bf557ab2", "Reclaim space"),
            UiText.Get("Resource_Text_51f0128d112d", "Remove all dangling images, stopped containers, and unused volumes?\n\n") +
            UiText.Get("Resource_Text_1a709936ef89", "Data in the affected volumes will be lost."),
            UiText.Get("Resource_Text_6600d6e1e67c", "Reclaim"));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var imageBytesBefore = await SumImageBytesAsync();
            var containersBefore = (await _wslc.ListContainersAsync(all: true, includeSize: true)).ToList();
            var containerBytesBefore = containersBefore.Where(c => c.State != ContainerState.Running).Sum(c => c.SizeRwBytes ?? 0);
            var volumesBefore = (await _wslc.ListVolumesAsync()).Count;

            // Containers first: removing a container releases any anonymous volumes it held,
            // so the subsequent volume prune can reclaim them too.
            var containerResult = await _wslc.PruneContainersAsync();
            var volumeResult = await _wslc.PruneVolumesAsync();
            var imageResult = await _wslc.PruneImagesAsync();

            var errors = new List<string>();
            AppendError(errors, UiText.Get("Resource_Text_642b5cd982d4", "containers"), containerResult);
            AppendError(errors, UiText.Get("Resource_Text_e44dfa6f6650", "volumes"), volumeResult);
            AppendError(errors, UiText.Get("Resource_Text_19f49d852660", "images"), imageResult);

            var freedBytes = Math.Max(0, imageBytesBefore - await SumImageBytesAsync());
            var containersAfter = await _wslc.ListContainersAsync(all: true, includeSize: true);
            var containersRemoved = Math.Max(0, containersBefore.Count - containersAfter.Count);
            var containerFreedBytes = Math.Max(0, containerBytesBefore - containersAfter.Where(c => c.State != ContainerState.Running).Sum(c => c.SizeRwBytes ?? 0));
            var volumesRemoved = Math.Max(0, volumesBefore - (await _wslc.ListVolumesAsync()).Count);

            var summary =
                UiText.Get("Resource_Text_2e0629ec10b6", "Reclaimed {0} from images.\n", FormatHelpers.HumanSize(freedBytes)) +
                UiText.Get("Resource_Text_2789780f8eaa", "Removed {0} (about {1} writable layer data) and {2}.", Count(containersRemoved, "container"), FormatHelpers.HumanSize(containerFreedBytes), Count(volumesRemoved, "volume"));
            if (errors.Count > 0)
            {
                summary += UiText.Get("Resource_Text_21208bca20bf", "\n\nSome steps reported errors:\n") + string.Join("\n", errors);
            }

            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4b16ccc50cc2", "Reclaim complete"), summary);
        });
    }

    private async Task<long> SumImageBytesAsync() =>
        (await _wslc.ListImagesAsync()).Sum(i => i.Size);

    private async Task ExecutePruneAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            var completed = await ContainerInventoryOperation.RunAsync(action, async error =>
            {
                StatusMessage = "Prune failed";
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"),
                    UiText.Get("Resource_Text_890767d3faf6", "Reclaim could not complete. Refresh the inventory before retrying; some cleanup may already have completed.\n\n{0}", error.Message));
            });
            if (!completed)
            {
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private static void AppendError(List<string> errors, string label, CommandResult result)
    {
        if (!result.Success)
        {
            errors.Add($"• {label}: {result.ErrorText}");
        }
    }

    private static string Count(int value, string noun) => noun switch
    {
        "image" => value == 1
            ? UiText.Get("Resource_Text_6d483483caef", "{0} image", value)
            : UiText.Get("Resource_Text_540058a14492", "{0} images", value),
        "container" => value == 1
            ? UiText.Get("Resource_Text_4554f5b256b4", "{0} container", value)
            : UiText.Get("Resource_Text_d89984bc3e37", "{0} containers", value),
        "volume" => value == 1
            ? UiText.Get("Resource_Text_7e34d9a960e3", "{0} volume", value)
            : UiText.Get("Resource_Text_765e0ff4d3a2", "{0} volumes", value),
        _ => $"{value} {noun}",
    };

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        foreach (var item in LargestImages) item.RefreshLocalizedText();
        foreach (var item in DanglingImages) item.RefreshLocalizedText();
        foreach (var item in UnusedVolumes) item.RefreshLocalizedText();
    }
}
