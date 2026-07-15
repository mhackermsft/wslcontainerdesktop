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

public partial class VolumesViewModel : ObservableObject
{
    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private VolumeInfo? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => $"{SelectedCount} selected";

    public ObservableCollection<VolumeInfo> Volumes { get; } = new();

    public VolumesViewModel(IWslcService wslc, DialogService dialogs)
    {
        _wslc = wslc;
        _dialogs = dialogs;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading volumes…";
        try
        {
            var volumes = (await _wslc.ListVolumesAsync()).ToList();

            // Enrich each volume with created-time + anonymous flag from inspect.
            foreach (var v in volumes)
            {
                var inspect = await _wslc.InspectVolumeAsync(v.Name);
                if (inspect.Success)
                {
                    v.EnrichFromInspect(inspect.StandardOutput);
                }
            }

            await CorrelateWithContainersAsync(volumes);

            Volumes.Clear();
            // Named first, then anonymous; each alphabetical/by-time.
            foreach (var v in volumes
                         .OrderBy(v => v.IsAnonymous)
                         .ThenByDescending(v => v.CreatedAt ?? DateTimeOffset.MinValue))
            {
                Volumes.Add(v);
            }

            StatusMessage = $"{Volumes.Count} volume{(Volumes.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Failed to load volumes", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Best-effort maps anonymous volumes to the container that created them. wslc does
    /// not report volume→container links, but an image-declared anonymous volume is created
    /// at the same instant as its container, so we match on creation time (to the second).
    /// </summary>
    private async Task CorrelateWithContainersAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        var anonymous = volumes.Where(v => v.IsAnonymous && v.CreatedAt is not null).ToList();
        if (anonymous.Count == 0)
        {
            return;
        }

        IReadOnlyList<ContainerInfo> containers;
        try
        {
            containers = await _wslc.ListContainersAsync(all: true);
        }
        catch
        {
            return;
        }

        foreach (var vol in anonymous)
        {
            var volSecond = vol.CreatedAt!.Value.ToUnixTimeSeconds();

            // Find the container whose creation time is closest within a 3-second window.
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
    private async Task CreateAsync()
    {
        var dialog = new SimpleInputDialog("Create volume", "Volume name", "e.g. my-data");
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        await ExecuteAsync(() => _wslc.CreateVolumeAsync(name));
    }

    [RelayCommand]
    private async Task RemoveAsync(VolumeInfo? volume)
    {
        volume ??= Selected;
        if (volume is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove volume",
            $"Remove volume \"{volume.Name}\"? Data in the volume will be lost.",
            "Remove");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.RemoveVolumeAsync(volume.Name), volume.Name);
    }

    [RelayCommand]
    private async Task InspectAsync(VolumeInfo? volume)
    {
        volume ??= Selected;
        if (volume is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.InspectVolumeAsync(volume.Name);
            await _dialogs.ShowMessageAsync($"Inspect · {volume.Name}",
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync("Prune volumes", "Remove all unused volumes?", "Prune");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.PruneVolumesAsync());
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            SelectedCount = 0;
        }
    }

    /// <summary>Removes every selected volume after a single confirmation, then exits selection mode.</summary>
    public async Task BulkRemoveAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        var items = volumes?.Where(v => v is not null).ToList() ?? new List<VolumeInfo>();
        if (items.Count == 0)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove volumes",
            $"Remove {items.Count} volume(s)? Data in these volumes will be lost.\n\n{BulkNames(items.Select(v => v.Name))}",
            "Remove");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var v in items)
            {
                var result = await _wslc.RemoveVolumeAsync(v.Name);
                if (!result.Success)
                {
                    failures.Add(v.Name);
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
                "Some volumes were not removed",
                $"{failures.Count} of {items.Count} could not be removed (they may still be attached to a container):\n\n{BulkNames(failures)}");
        }
    }

    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? $"{shown}\n… and {list.Count - max} more" : shown;
    }

    private async Task ExecuteAsync(Func<Task<CommandResult>> action, string? volumeName = null)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                var message = result.ErrorText;

                // Friendlier message for the common "volume is attached to a container" case.
                if (message.Contains("ERROR_SHARING_VIOLATION", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("is in use", StringComparison.OrdinalIgnoreCase))
                {
                    var name = string.IsNullOrEmpty(volumeName) ? "This volume" : $"\"{volumeName}\"";
                    message =
                        $"{name} is still attached to a container, so it can't be removed.\n\n" +
                        "Stop and remove the container that uses it first, then try again.\n\n" +
                        "(Note: the WSL container preview does not report which container uses a named volume.)";
                }

                await _dialogs.ShowMessageAsync("Can't remove volume", message);
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
