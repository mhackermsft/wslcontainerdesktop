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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the "Reclaim space" page: a holistic disk-usage &amp; cleanup center that
/// summarizes how much space images, containers, and volumes consume and offers
/// one-click, confirmed pruning that reuses the existing <c>Prune*</c> service methods.
/// </summary>
public partial class ReclaimSpaceViewModel : ObservableObject
{
    /// <summary>How many of the largest images to surface in the "largest images" list.</summary>
    private const int TopImageCount = 5;

    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;

    private long _imagesTotalBytes;
    private long _imagesReclaimableBytes;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // ---- Images ----
    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private int _danglingImageCount;

    [ObservableProperty]
    private string _imagesTotalSize = "0 B";

    [ObservableProperty]
    private string _imagesReclaimableSize = "0 B";

    // ---- Containers ----
    [ObservableProperty]
    private int _containerCount;

    [ObservableProperty]
    private int _stoppedContainerCount;

    // ---- Volumes ----
    [ObservableProperty]
    private int _volumeCount;

    [ObservableProperty]
    private int _unusedVolumeCount;

    // ---- Totals ----
    [ObservableProperty]
    private string _totalReclaimableSize = "0 B";

    /// <summary>The largest images by on-disk size (top <see cref="TopImageCount"/>).</summary>
    public ObservableCollection<ImageInfo> LargestImages { get; } = new();

    /// <summary>Dangling (untagged) images, which a plain image prune removes.</summary>
    public ObservableCollection<ImageInfo> DanglingImages { get; } = new();

    /// <summary>Anonymous volumes with no correlated container (safe to prune).</summary>
    public ObservableCollection<VolumeInfo> UnusedVolumes { get; } = new();

    public ReclaimSpaceViewModel(IWslcService wslc, DialogService dialogs)
    {
        _wslc = wslc;
        _dialogs = dialogs;
    }

    /// <summary>An image is "dangling" when it has no repository or tag (i.e. <c>&lt;none&gt;</c>).</summary>
    internal static bool IsDangling(ImageInfo image) =>
        IsNone(image.Repository) || IsNone(image.Tag);

    private static bool IsNone(string value) =>
        string.IsNullOrEmpty(value) || value.Equals("<none>", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Calculating disk usage…";
        try
        {
            var images = await _wslc.ListImagesAsync();
            var containers = await _wslc.ListContainersAsync(all: true);
            var volumes = (await _wslc.ListVolumesAsync()).ToList();
            await EnrichVolumesAsync(volumes, containers);

            // Images. Sizes are per-image and may share layers, so the totals are an upper
            // bound (matching how `df`-style views typically present them).
            var dangling = images.Where(IsDangling).ToList();
            _imagesTotalBytes = images.Sum(i => i.Size);
            _imagesReclaimableBytes = dangling.Sum(i => i.Size);

            ImageCount = images.Count;
            DanglingImageCount = dangling.Count;
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

            // Containers. wslc does not report per-container writable-layer sizes, so we
            // surface counts: anything not running can be reclaimed by a container prune.
            ContainerCount = containers.Count;
            StoppedContainerCount = containers.Count(c => c.State != ContainerState.Running);

            // Volumes. wslc does not report volume sizes, and named-volume usage is unknown,
            // so "unused" is limited to anonymous volumes with no correlated container.
            var unused = volumes.Where(v => v.IsAnonymous && string.IsNullOrEmpty(v.UsedBy)).ToList();
            VolumeCount = volumes.Count;
            UnusedVolumeCount = unused.Count;

            UnusedVolumes.Clear();
            foreach (var volume in unused)
            {
                UnusedVolumes.Add(volume);
            }

            TotalReclaimableSize = FormatHelpers.HumanSize(_imagesReclaimableBytes);
            StatusMessage = _imagesReclaimableBytes > 0
                ? $"Up to {TotalReclaimableSize} reclaimable from dangling images"
                : "Nothing obvious to reclaim";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Failed to calculate disk usage", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fills IsAnonymous/CreatedAt from <c>volume inspect</c> and best-effort correlates
    /// anonymous volumes to a container by creation time (mirrors VolumesViewModel).
    /// </summary>
    private async Task EnrichVolumesAsync(IReadOnlyList<VolumeInfo> volumes, IReadOnlyList<ContainerInfo> containers)
    {
        foreach (var v in volumes)
        {
            var inspect = await _wslc.InspectVolumeAsync(v.Name);
            if (inspect.Success)
            {
                v.EnrichFromInspect(inspect.StandardOutput);
            }
        }

        foreach (var vol in volumes.Where(v => v.IsAnonymous && v.CreatedAt is not null))
        {
            var volSecond = vol.CreatedAt!.Value.ToUnixTimeSeconds();

            ContainerInfo? best = null;
            long bestDelta = long.MaxValue;
            foreach (var c in containers)
            {
                var delta = Math.Abs(c.CreatedAt - volSecond);
                if (delta <= 3 && delta < bestDelta)
                {
                    bestDelta = delta;
                    best = c;
                }
            }

            if (best is not null)
            {
                vol.UsedBy = best.Name;
            }
        }
    }

    [RelayCommand]
    private async Task PruneImagesAsync()
    {
        if (DanglingImageCount == 0)
        {
            await _dialogs.ShowMessageAsync("Remove dangling images", "There are no dangling images to remove.");
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove dangling images",
            $"Remove {Count(DanglingImageCount, "dangling image")}, reclaiming up to {ImagesReclaimableSize}?",
            "Remove");
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
                await _dialogs.ShowMessageAsync("Prune failed", result.ErrorText);
                return;
            }

            var freed = Math.Max(0, before - await SumImageBytesAsync());
            await _dialogs.ShowMessageAsync("Images pruned", $"Reclaimed {FormatHelpers.HumanSize(freed)}.");
        });
    }

    [RelayCommand]
    private async Task PruneContainersAsync()
    {
        if (StoppedContainerCount == 0)
        {
            await _dialogs.ShowMessageAsync("Remove stopped containers", "There are no stopped containers to remove.");
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove stopped containers",
            $"Remove all stopped containers ({StoppedContainerCount} candidate{(StoppedContainerCount == 1 ? "" : "s")})?",
            "Remove");
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = (await _wslc.ListContainersAsync(all: true)).Count;
            var result = await _wslc.PruneContainersAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Prune failed", result.ErrorText);
                return;
            }

            var removed = Math.Max(0, before - (await _wslc.ListContainersAsync(all: true)).Count);
            await _dialogs.ShowMessageAsync("Containers pruned", $"Removed {Count(removed, "container")}.");
        });
    }

    [RelayCommand]
    private async Task PruneVolumesAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Remove unused volumes",
            "Remove all unused volumes? Any data they hold will be lost.\n\n" +
            "(wslc does not report which container uses a named volume, so only volumes " +
            "not attached to a running container are affected.)",
            "Remove");
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
                await _dialogs.ShowMessageAsync("Prune failed", result.ErrorText);
                return;
            }

            var removed = Math.Max(0, before - (await _wslc.ListVolumesAsync()).Count);
            await _dialogs.ShowMessageAsync("Volumes pruned", $"Removed {Count(removed, "volume")}.");
        });
    }

    [RelayCommand]
    private async Task PruneAllAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Reclaim space",
            "Remove all dangling images, stopped containers, and unused volumes?\n\n" +
            "Data in the affected volumes will be lost.",
            "Reclaim");
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var imageBytesBefore = await SumImageBytesAsync();
            var containersBefore = (await _wslc.ListContainersAsync(all: true)).Count;
            var volumesBefore = (await _wslc.ListVolumesAsync()).Count;

            // Containers first: removing a container releases any anonymous volumes it held,
            // so the subsequent volume prune can reclaim them too.
            var containerResult = await _wslc.PruneContainersAsync();
            var volumeResult = await _wslc.PruneVolumesAsync();
            var imageResult = await _wslc.PruneImagesAsync();

            var errors = new List<string>();
            AppendError(errors, "containers", containerResult);
            AppendError(errors, "volumes", volumeResult);
            AppendError(errors, "images", imageResult);

            var freedBytes = Math.Max(0, imageBytesBefore - await SumImageBytesAsync());
            var containersRemoved = Math.Max(0, containersBefore - (await _wslc.ListContainersAsync(all: true)).Count);
            var volumesRemoved = Math.Max(0, volumesBefore - (await _wslc.ListVolumesAsync()).Count);

            var summary =
                $"Reclaimed {FormatHelpers.HumanSize(freedBytes)} from images.\n" +
                $"Removed {Count(containersRemoved, "container")} and {Count(volumesRemoved, "volume")}.";
            if (errors.Count > 0)
            {
                summary += "\n\nSome steps reported errors:\n" + string.Join("\n", errors);
            }

            await _dialogs.ShowMessageAsync("Reclaim complete", summary);
        });
    }

    private async Task<long> SumImageBytesAsync() =>
        (await _wslc.ListImagesAsync()).Sum(i => i.Size);

    private async Task ExecutePruneAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
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

    private static string Count(int value, string noun) =>
        $"{value} {noun}{(value == 1 ? "" : "s")}";
}
