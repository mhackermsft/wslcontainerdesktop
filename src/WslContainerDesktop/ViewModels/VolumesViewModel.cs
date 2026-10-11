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
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>View model for the Volumes page, listing <c>wslc</c> volumes and running create, inspect, remove, prune, and bulk-remove commands.</summary>
public partial class VolumesViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => UiText.Translate(StatusMessage);

    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;
    private readonly ILogger<VolumesViewModel> _logger;
    private CancellationTokenSource? _refreshCancellation;

    /// <summary>Generated busy flag used to disable volume commands while an operation is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status text shown at the top of the Volumes page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    /// <summary>Currently selected volume row, or <c>null</c> when nothing is selected.</summary>
    [ObservableProperty]
    private VolumeInfo? _selected;

    /// <summary>True while the page is showing bulk-selection actions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    /// <summary>Number of rows currently selected for a bulk operation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => UiText.Get("Resource_Text_e5084bffcc17", "{0} selected", SelectedCount);

    /// <summary>Volume rows displayed by the page.</summary>
    public ObservableCollection<VolumeInfo> Volumes { get; } = new();

    /// <summary>Creates the volume page model with the services that talk to <c>wslc</c> and show dialogs.</summary>
    public VolumesViewModel(IWslcService wslc, DialogService dialogs, ILogger<VolumesViewModel> logger)
    {
        _wslc = wslc;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Reloads volumes, enriches them with inspect metadata, and resolves which containers use them.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _refreshCancellation?.Cancel();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refreshCancellation = refresh;
        refresh.CancelAfter(TimeSpan.FromSeconds(45));
        ct = refresh.Token;
        IsBusy = true;
        if (Volumes.Count == 0)
        {
            StatusMessage = "Loading volumes…";
        }
        try
        {
            var volumes = (await _wslc.ListVolumesAsync(ct)).ToList();

            // Enrich each volume with created-time + anonymous flag from inspect, a few at a time,
            // while the container listing for usage runs alongside. Awaiting both together means a
            // failure in either still fails the refresh and neither is left unobserved.
            using var gate = new SemaphoreSlim(4);
            var containersTask = _wslc.ListContainersAsync(all: true, ct: ct);
            var inspectsTask = Task.WhenAll(volumes.Select(async v =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var inspect = await _wslc.InspectVolumeAsync(v.Name, ct);
                    if (inspect.Success)
                    {
                        v.EnrichFromInspect(inspect.StandardOutput);
                    }
                    else
                    {
                        _logger.LogWarning("Could not inspect volume {Volume}: {Error}", v.Name, inspect.ErrorText);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            await Task.WhenAll(inspectsTask, containersTask);
            var containers = await containersTask;
            var warnings = await VolumeUsageResolver.ResolveAsync(volumes, containers,
                (id, token) => _wslc.InspectContainerAsync(id, token), ct);
            foreach (var warning in warnings)
            {
                _logger.LogWarning("Volume usage: {Warning}", warning);
            }

            ct.ThrowIfCancellationRequested();
            CollectionSync.ReplaceAll(Volumes, volumes
                .OrderBy(v => v.IsAnonymous)
                .ThenByDescending(v => v.CreatedAt ?? DateTimeOffset.MinValue)
                .ToList());

            StatusMessage = $"{Volumes.Count} volume{(Volumes.Count == 1 ? "" : "s")}";
            if (warnings.Count > 0)
            {
                StatusMessage += " - usage incomplete; unknown does not mean unused";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                StatusMessage = "Volume refresh cancelled or timed out; displayed data may be stale";
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_587588037564", "Failed to load volumes"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                _refreshCancellation = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>Cancels the current refresh when the page is leaving or being replaced.</summary>
    public void CancelRefresh() => _refreshCancellation?.Cancel();

    /// <summary>Prompts for a volume name and creates it with <c>wslc</c>.</summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        var dialog = new SimpleInputDialog(UiText.Get("Resource_Text_1c2351331a73", "Create volume"), UiText.Get("Resource_Text_9665af7c3b74", "Volume name"), UiText.Get("Resource_Text_389e0c4950f6", "e.g. my-data"));
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

    /// <summary>Confirms and removes one selected volume.</summary>
    [RelayCommand]
    private async Task RemoveAsync(VolumeInfo? volume)
    {
        volume ??= Selected;
        if (volume is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_7fa7f416b8c2", "Remove volume"),
            UiText.Get("Resource_Text_9cd3f346fe82", "Remove volume \"{0}\"? Data in the volume will be lost.", volume.Name),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.RemoveVolumeAsync(volume.Name), volume.Name);
    }

    /// <summary>Shows raw inspect output for the selected volume.</summary>
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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_f306c4f01fc3", "Inspect · {0}", volume.Name),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Confirms and asks the engine to remove every volume it considers unused.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(UiText.Get("Resource_Text_cb5d30ca0eaa", "Prune volumes"), UiText.Get("Resource_Text_075341e9d7a5", "Remove all unused volumes?"), UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"));
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
            UiText.Get("Resource_Text_97e7c38762f2", "Remove volumes"),
            UiText.Get("Resource_Text_895d5f585893", "Remove {0} volume(s)? Data in these volumes will be lost.\n\n{1}", items.Count, BulkNames(items.Select(v => v.Name))),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
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
                UiText.Get("Resource_Text_7e216826243b", "Some volumes were not removed"),
                UiText.Get("Resource_Text_2c7e82a6c879", "{0} of {1} could not be removed (they may still be attached to a container):\n\n{2}", failures.Count, items.Count, BulkNames(failures)));
        }
    }

    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? UiText.Get("Resource_Text_5b44479e9557", "{0}\n… and {1} more", shown, list.Count - max) : shown;
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
                    var name = string.IsNullOrEmpty(volumeName) ? UiText.Get("Resource_Text_815f075b8069", "This volume") : $"\"{volumeName}\"";
                    message =
                        UiText.Get("Resource_Text_01d84a089f65", "{0} is still attached to a container, so it can't be removed.\n\n", name) +
                        UiText.Get("Resource_Text_0d553567c19c", "Stop and remove the container that uses it first, then try again.\n\n") +
                        UiText.Get("Resource_Text_ea8c28b99ef3", "The Used by column includes stopped containers when inspect metadata is available. ") +
                        UiText.Get("Resource_Text_148f26a65911", "Unknown or partial usage is not proof that a volume is unused.");
                }

                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_69047d7b411a", "Can't remove volume"), message);
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
        foreach (var item in Volumes) item.RefreshLocalizedText();
    }
}
