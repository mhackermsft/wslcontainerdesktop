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

/// <summary>Backs the Networks page, listing WSL container networks and exposing create, inspect, prune and removal commands.</summary>
public partial class NetworksViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay
    {
        get
        {
            if (_loadedSummary is not { } summary || StatusMessage != summary.Raw)
                return UiText.Translate(StatusMessage);
            if (summary.UserCount == 0)
                return summary.BuiltInCount == 1
                    ? UiText.Get("Root_Network_BuiltInSingular", "{0} built-in network", summary.BuiltInCount)
                    : UiText.Get("Root_Network_BuiltInPlural", "{0} built-in networks", summary.BuiltInCount);
            if (summary.UserCount == 1)
                return summary.BuiltInCount == 1
                    ? UiText.Get("Root_Network_UserSingularBuiltInSingular", "{0} user network + {1} built-in network", summary.UserCount, summary.BuiltInCount)
                    : UiText.Get("Root_Network_UserSingularBuiltInPlural", "{0} user network + {1} built-in networks", summary.UserCount, summary.BuiltInCount);
            return summary.BuiltInCount == 1
                ? UiText.Get("Root_Network_UserPluralBuiltInSingular", "{0} user networks + {1} built-in network", summary.UserCount, summary.BuiltInCount)
                : UiText.Get("Root_Network_UserPluralBuiltInPlural", "{0} user networks + {1} built-in networks", summary.UserCount, summary.BuiltInCount);
        }
    }

    private (string Raw, int BuiltInCount, int UserCount)? _loadedSummary;

    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;

    // Limits how long the "Used by" column may take, and lets a newer refresh cancel an older one.
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _usageCts;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isBusy;

    /// <summary>
    /// True when the empty-list message should show: not while a refresh is still loading, which
    /// would briefly (and wrongly) claim there are no networks.
    /// </summary>
    public bool ShowEmptyState => !IsBusy && Networks.Count == 0;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    /// <summary>Value for selected shown or edited by the view.</summary>
    [ObservableProperty]
    private NetworkInfo? _selected;

    /// <summary>Whether selection mode for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    /// <summary>Bindable state for selected count used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => UiText.Get("Resource_Text_e5084bffcc17", "{0} selected", SelectedCount);

    /// <summary>Value for networks shown or edited by the view.</summary>
    public ObservableCollection<NetworkInfo> Networks { get; } = new();

    /// <summary>Creates the Networks view model and stores its injected services.</summary>
    public NetworksViewModel(IWslcService wslc, DialogService dialogs)
    {
        _wslc = wslc;
        _dialogs = dialogs;
        Networks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        if (Networks.Count == 0)
        {
            StatusMessage = "Loading networks…";
        }

        try
        {
            var networks = NetworkDisplayList.Create(await _wslc.ListNetworksAsync());
            foreach (var n in networks)
            {
                n.UsagePending = true;
            }

            CollectionSync.ReplaceAll(Networks, networks);
            var builtInCount = Networks.Count(n => n.IsBuiltIn);
            var userCount = Networks.Count - builtInCount;
            var builtInLabel = $"{builtInCount} built-in network{(builtInCount == 1 ? "" : "s")}";
            var rawSummary = userCount == 0
                ? builtInLabel
                : $"{userCount} user network{(userCount == 1 ? "" : "s")} + {builtInLabel}";
            _loadedSummary = (rawSummary, builtInCount, userCount);
            StatusMessage = rawSummary;

            // The list is shown now; "Used by" fills in afterwards so it never holds up the page.
            _ = ResolveUsageAsync(networks);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_fce244da09aa", "Failed to load networks"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fills in which containers use each network. The "Used by" column is extra detail, so a
    /// failure here leaves every network's usage as "Unknown" instead of failing the page.
    /// </summary>
    private async Task ResolveUsageAsync(IReadOnlyList<NetworkInfo> networks)
    {
        // A newer refresh supersedes this one, and a stuck engine can't keep "Checking…" forever.
        _usageCts?.Cancel();
        using var cts = new CancellationTokenSource(UsageTimeout);
        _usageCts = cts;
        try
        {
            var containers = await _wslc.ListContainersAsync(all: true, ct: cts.Token);
            await NetworkUsageResolver.ResolveAsync(networks, containers,
                (id, ct) => _wslc.InspectContainerAsync(id, ct), cts.Token);
        }
        catch (Exception ex)
        {
            // Includes cancellation and timeout. Usage shows "Unknown"; the network list itself is still valid.
            System.Diagnostics.Debug.WriteLine($"Network usage could not be resolved: {ex.Message}");
            foreach (var network in networks)
            {
                network.UsagePending = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_usageCts, cts))
                _usageCts = null;
        }
    }

    /// <summary>Command handler for create actions triggered from the view.</summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        var dialog = new CreateNetworkDialog();
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.NetworkName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        await ExecuteAsync(() => _wslc.CreateNetworkAsync(
            name,
            dialog.Driver,
            dialog.DriverOptions,
            dialog.Labels,
            dialog.Subnet,
            dialog.Gateway,
            dialog.IpRange,
            internalNetwork: dialog.InternalNetwork));
    }

    /// <summary>Command handler for remove actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RemoveAsync(NetworkInfo? network)
    {
        network ??= Selected;
        if (network is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_13ebc1f3d6b6", "Remove network"),
            UiText.Get("Resource_Text_dfd89cd8c84d", "Remove network \"{0}\"?", network.Name),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.RemoveNetworkAsync(network.Name));
    }

    /// <summary>Command handler for inspect actions triggered from the view.</summary>
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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_f306c4f01fc3", "Inspect · {0}", network.Name),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for prune actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(UiText.Get("Resource_Text_6b11865fce3e", "Prune networks"), UiText.Get("Resource_Text_31e11fbaef14", "Remove all unused networks?"), UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.PruneNetworksAsync());
    }

    /// <summary>Handles is selection mode changed changes and updates related view-model state.</summary>
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
            UiText.Get("Resource_Text_680e9fe1f01f", "Remove networks"),
            UiText.Get("Resource_Text_cd105da7078d", "Remove {0} network(s)? (Built-in networks are skipped.)\n\n{1}", items.Count, BulkNames(items.Select(n => n.Name))),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
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
                UiText.Get("Resource_Text_e596a4450bb9", "Some networks were not removed"),
                UiText.Get("Resource_Text_2f1332b9df3c", "{0} of {1} could not be removed (they may still have attached containers):\n\n{2}", failures.Count, items.Count, BulkNames(failures)));
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

    /// <summary>Helper for the execute workflow in this view model.</summary>
    private async Task ExecuteAsync(Func<Task<CommandResult>> action)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"), result.ErrorText);
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
        foreach (var item in Networks) item.RefreshLocalizedText();
    }
}
