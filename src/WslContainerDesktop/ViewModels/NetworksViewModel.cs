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

public partial class NetworksViewModel : ObservableObject
{
    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private NetworkInfo? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => $"{SelectedCount} selected";

    public ObservableCollection<NetworkInfo> Networks { get; } = new();

    public NetworksViewModel(IWslcService wslc, DialogService dialogs)
    {
        _wslc = wslc;
        _dialogs = dialogs;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading networks…";
        try
        {
            var networks = await _wslc.ListNetworksAsync();
            Networks.Clear();

            // Always show the built-in default bridge network first (read-only).
            Networks.Add(NetworkInfo.DefaultBridge());

            foreach (var n in networks.OrderBy(n => n.Name))
            {
                Networks.Add(n);
            }

            var userCount = Networks.Count - 1;
            StatusMessage = userCount == 0
                ? "1 default network"
                : $"{userCount} user network{(userCount == 1 ? "" : "s")} + 1 default";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Failed to load networks", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var dialog = new SimpleInputDialog("Create network", "Network name", "e.g. app-net");
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        await ExecuteAsync(() => _wslc.CreateNetworkAsync(name));
    }

    [RelayCommand]
    private async Task RemoveAsync(NetworkInfo? network)
    {
        network ??= Selected;
        if (network is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove network",
            $"Remove network \"{network.Name}\"?",
            "Remove");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.RemoveNetworkAsync(network.Name));
    }

    [RelayCommand]
    private async Task InspectAsync(NetworkInfo? network)
    {
        network ??= Selected;
        if (network is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.InspectNetworkAsync(network.Name);
            await _dialogs.ShowMessageAsync($"Inspect · {network.Name}",
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
        var ok = await _dialogs.ShowConfirmAsync("Prune networks", "Remove all unused networks?", "Prune");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.PruneNetworksAsync());
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            SelectedCount = 0;
        }
    }

    /// <summary>Removes every selected (removable) network after one confirmation, then exits selection mode.</summary>
    public async Task BulkRemoveAsync(IReadOnlyList<NetworkInfo> networks)
    {
        var items = networks?.Where(n => n is not null && n.CanModify).ToList() ?? new List<NetworkInfo>();
        if (items.Count == 0)
        {
            IsSelectionMode = false;
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove networks",
            $"Remove {items.Count} network(s)? (Built-in networks are skipped.)\n\n{BulkNames(items.Select(n => n.Name))}",
            "Remove");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var n in items)
            {
                var result = await _wslc.RemoveNetworkAsync(n.Name);
                if (!result.Success)
                {
                    failures.Add(n.Name);
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
                "Some networks were not removed",
                $"{failures.Count} of {items.Count} could not be removed (they may still have attached containers):\n\n{BulkNames(failures)}");
        }
    }

    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? $"{shown}\n… and {list.Count - max} more" : shown;
    }

    private async Task ExecuteAsync(Func<Task<CommandResult>> action)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Operation failed", result.ErrorText);
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
