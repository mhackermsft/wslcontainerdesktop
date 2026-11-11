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
using Microsoft.Extensions.Logging.Abstractions;
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
            if (_combined is not null)
                return _combined.IsBusy || _combined.HasRefreshFailure || !_combined.HasLoadedSelection
                    ? _combined.StatusText
                    : UiText.Get("Networks_CombinedSummary", "{0} networks across {1} active sessions.",
                        Networks.Count, _combined.Sessions.Count(session => session.IsAvailable && session.IsSelected));
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
    private readonly WslcSessionContext _sessionContext;
    private readonly ILogger<NetworksViewModel> _logger;
    private readonly CombinedSessionViewModel? _combined;
    private readonly WslcSessionResourceActions? _sessionActions;
    private readonly Func<WslcSessionInfo, string, IWslcService>? _sessionClient;
    private readonly IWslcSessionSelectionService? _sessionSelection;
    private readonly Dictionary<NetworkInfo, CombinedSessionRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly List<(string Session, string Detail)> _detailErrors = [];
    private bool _detailsTimedOut;
    private WslcSessionContext.SelectionSnapshot? _inventorySelection;
    private CancellationTokenSource? _refreshCts;

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

    /// <summary>Search and sort state for the network list.</summary>
    public ResourceGridView<NetworkInfo> Grid { get; }

    /// <summary>Creates the Networks view model and stores its injected services.</summary>
#if !WSLCD_TEST_NO_DISPATCHER
    public NetworksViewModel(IWslcService wslc, DialogService dialogs, WslcSessionContext sessionContext,
        CombinedSessionViewModel combined, WslcSessionResourceActions sessionActions, WslcSessionClientFactory clients,
        ILogger<NetworksViewModel> logger, IWslcSessionSelectionService sessionSelection)
        : this(wslc, dialogs, sessionContext, combined, sessionActions,
            (session, path) => clients.Create(session, path, managed: false), logger, sessionSelection)
    {
    }
#endif

    internal NetworksViewModel(IWslcService wslc, DialogService dialogs, WslcSessionContext sessionContext,
        CombinedSessionViewModel? combined = null, WslcSessionResourceActions? sessionActions = null,
        Func<WslcSessionInfo, string, IWslcService>? sessionClient = null, ILogger<NetworksViewModel>? logger = null,
        IWslcSessionSelectionService? sessionSelection = null)
    {
        _wslc = wslc;
        _dialogs = dialogs;
        _sessionContext = sessionContext;
        _logger = logger ?? NullLogger<NetworksViewModel>.Instance;
        _combined = combined;
        _sessionActions = sessionActions;
        _sessionClient = sessionClient;
        _sessionSelection = sessionSelection;
        Grid = new(Networks, network =>
            [network.SessionName, network.Name, network.DriverDisplay, network.UsedByDisplay, network.Id, network.Scope],
            new("session", "ResourceGrid_Session", "Session", network => network.SessionName),
            new("name", "ResourceGrid_Name", "Name", network => network.Name),
            new("driver", "ResourceGrid_Driver", "Driver", network => network.DriverDisplay),
            new("usedby", "ResourceGrid_UsedBy", "Used by", network => network.UsedByDisplay),
            new("id", "ResourceGrid_Id", "ID", network => network.Id));
        if (_combined is not null)
            _combined.PropertyChanged += OnCombinedChanged;
        Networks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_combined is not null)
        {
            await RefreshCombinedAsync();
            return;
        }
        using var selection = _sessionContext.LockSelection();
        _inventorySelection = null;
        var snapshot = _sessionContext.CaptureSnapshot();
        _refreshCts?.Cancel();
        _usageCts?.Cancel();
        using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _refreshCts = refresh;
        IsBusy = true;
        if (Networks.Count == 0)
        {
            StatusMessage = "Loading networks…";
        }

        try
        {
            var networks = NetworkDisplayList.Create(await _wslc.ListNetworksAsync(refresh.Token));
            refresh.Token.ThrowIfCancellationRequested();
            foreach (var n in networks)
            {
                n.UsagePending = true;
            }

            using (Grid.DeferRefresh())
                CollectionSync.ReplaceAll(Networks, networks);
            var builtInCount = Networks.Count(n => n.IsBuiltIn);
            var userCount = Networks.Count - builtInCount;
            var builtInLabel = $"{builtInCount} built-in network{(builtInCount == 1 ? "" : "s")}";
            var rawSummary = userCount == 0
                ? builtInLabel
                : $"{userCount} user network{(userCount == 1 ? "" : "s")} + {builtInLabel}";
            _loadedSummary = (rawSummary, builtInCount, userCount);
            StatusMessage = rawSummary;
            _inventorySelection = snapshot;

            // The list is shown now; "Used by" fills in afterwards so it never holds up the page.
            _ = ResolveUsageAsync(networks);
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            if (ReferenceEquals(_refreshCts, refresh))
                StatusMessage = UiText.Get("SessionInventory_RefreshCancelled",
                    "Inventory refresh cancelled or timed out; displayed data may be stale.");
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_fce244da09aa", "Failed to load networks"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, refresh))
            {
                _refreshCts = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>Cancels page-owned observations when navigation leaves the network page.</summary>
    public void CancelRefresh()
    {
        _refreshCts?.Cancel();
        _usageCts?.Cancel();
    }

    /// <summary>Warnings about incomplete session inventories or network usage.</summary>
    public string CombinedWarning => string.Join(Environment.NewLine,
        new[]
        {
            _combined?.WarningText ?? string.Empty,
            _detailsTimedOut ? UiText.Get("SessionInventory_RefreshCancelled",
                "Inventory refresh cancelled or timed out; displayed data may be stale.") : string.Empty,
        }.Concat(_detailErrors.Select(error =>
            UiText.Get("Networks_SessionDetailsUnavailable",
                "{0}: network usage may be incomplete: {1}", error.Session, error.Detail)))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    /// <summary>Session used by actions that are not tied to an existing row.</summary>
    public string ManagementDestination => UiText.Get("ResourceSessionDestination",
        "Choose a session when creating or pruning. Existing resources stay in their own session.");

    private void OnCombinedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.NetworkRows))
            SyncCombinedRows();
        if (args.PropertyName is nameof(CombinedSessionViewModel.WarningText) or nameof(CombinedSessionViewModel.StatusText))
        {
            OnPropertyChanged(nameof(CombinedWarning));
            OnPropertyChanged(nameof(StatusMessageDisplay));
        }
    }

    private void SyncCombinedRows()
    {
        if (_combined is null)
            return;
        var old = new Dictionary<(WslcSessionInfo, NetworkInfo), Queue<NetworkInfo>>();
        foreach (var pair in _rows)
        {
            var key = (pair.Value.Session, pair.Value.Network!);
            if (!old.TryGetValue(key, out var matches))
                old[key] = matches = new Queue<NetworkInfo>();
            matches.Enqueue(pair.Key);
        }
        var rows = _combined.NetworkRows.Where(row => row.Network is not null).ToArray();
        _rows.Clear();
        foreach (var row in rows)
        {
            var source = row.Network!;
            var network = old.TryGetValue((row.Session, source), out var matches) && matches.Count > 0
                ? matches.Dequeue() : CopyNetwork(source);
            network.SessionName = row.SessionName;
            network.SessionIdentityKnown = row.Session.ID > 0 && row.Session.CreatorPid > 0;
            _rows[network] = row;
        }
        var networks = _rows.Keys.OrderBy(network => network.SessionName, StringComparer.Ordinal)
            .ThenBy(network => network.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!Networks.SequenceEqual(networks, ReferenceEqualityComparer.Instance))
        {
            using (Grid.DeferRefresh())
                CollectionSync.ReplaceAll(Networks, networks);
        }
        Grid.Refresh();
    }

    private static NetworkInfo CopyNetwork(NetworkInfo source) => new()
    {
        Id = source.Id, Name = source.Name, Driver = source.Driver, Scope = source.Scope,
        IsBuiltIn = source.IsBuiltIn, ContainerUsers = source.ContainerUsers.ToArray(),
        UsageComplete = source.UsageComplete, UsagePending = source.UsagePending,
    };

    /// <summary>Reuses recent shared inventory on navigation.</summary>
    public Task RefreshOnNavigationAsync() =>
        _combined is null ? RefreshAsync() : RefreshCombinedAsync(reuseRecent: true);

    private async Task RefreshCombinedAsync(bool reuseRecent = false)
    {
        if (_combined is null || _sessionClient is null)
            throw new InvalidOperationException("The session inventory is not configured.");
        _refreshCts?.Cancel();
        _usageCts?.Cancel();
        using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _refreshCts = refresh;
        IsBusy = true;
        _detailErrors.Clear();
        _detailsTimedOut = false;
        OnPropertyChanged(nameof(CombinedWarning));
        try
        {
            if (reuseRecent) await _combined.RefreshForNavigationAsync(refresh.Token);
            else await _combined.RefreshAllAsync(refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (_combined.HasRefreshFailure)
                return;
            SyncCombinedRows();
            var executable = _combined.CurrentExecutablePath;
            var snapshot = _combined.Inventory;
            var groups = _rows.ToArray().GroupBy(pair => pair.Value.Session).ToArray();
            foreach (var group in groups)
            {
                var session = group.Key;
                var captured = group.ToArray();
                var detail = captured.Select(pair =>
                {
                    var copy = CopyNetwork(pair.Key);
                    copy.ContainerUsers = [];
                    copy.UsageComplete = false;
                    copy.UsagePending = true;
                    return copy;
                }).ToArray();
                foreach (var (network, _) in captured)
                {
                    network.ContainerUsers = [];
                    network.UsageComplete = false;
                    network.UsagePending = true;
                }
                try
                {
                    var client = _sessionClient(session, executable);
                    await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                        session, executable, refresh.Token);
                    var containers = snapshot.Containers.Where(row => row.Session == session)
                        .Select(row => row.Value).ToArray();
                    var complete = !snapshot.Failures.Any(failure => failure.Session == session &&
                        failure.Kind is WslcCombinedInventory.FailureKind.Containers or WslcCombinedInventory.FailureKind.Session);
                    if (complete)
                        await NetworkUsageResolver.ResolveAsync(detail, containers,
                            (id, token) => client.InspectContainerAsync(id, token), refresh.Token);
                    else
                        foreach (var network in detail)
                            network.UsagePending = false;
                    await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                        session, executable, refresh.Token);
                    if (!ReferenceEquals(_refreshCts, refresh))
                        return;
                    for (var index = 0; index < captured.Length; index++)
                    {
                        var (network, originalRow) = captured[index];
                        if (!_rows.TryGetValue(network, out var currentRow) || currentRow.Session != originalRow.Session ||
                            !ReferenceEquals(currentRow.Network, originalRow.Network))
                            continue;
                        network.ContainerUsers = detail[index].ContainerUsers;
                        network.UsageComplete = detail[index].UsageComplete;
                        network.UsagePending = detail[index].UsagePending;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                           System.ComponentModel.Win32Exception or TimeoutException)
                {
                    _logger.LogWarning(ex, "Could not read network usage for session {Session}.", session.Name);
                    if (ReferenceEquals(_refreshCts, refresh))
                        _detailErrors.Add((session.Name, ex.Message));
                    if (ReferenceEquals(_refreshCts, refresh))
                        foreach (var (network, _) in captured)
                            network.UsagePending = false;
                }
                catch (OperationCanceledException ex) when (!refresh.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Network usage was unexpectedly cancelled for session {Session}.", session.Name);
                    if (ReferenceEquals(_refreshCts, refresh))
                        _detailErrors.Add((session.Name, UiText.Get("CombinedSession_UnexpectedCancellation",
                            "The engine cancelled the request unexpectedly.")));
                    if (ReferenceEquals(_refreshCts, refresh))
                        foreach (var (network, _) in captured)
                            network.UsagePending = false;
                }
            }
            OnPropertyChanged(nameof(CombinedWarning));
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            if (ReferenceEquals(_refreshCts, refresh))
            {
                _detailsTimedOut = true;
                OnPropertyChanged(nameof(CombinedWarning));
                foreach (var network in Networks)
                    network.UsagePending = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, refresh))
            {
                _refreshCts = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Fills in which containers use each network. The "Used by" column is extra detail, so a
    /// failure here leaves every network's usage as "Unknown" instead of failing the page.
    /// </summary>
    private async Task ResolveUsageAsync(IReadOnlyList<NetworkInfo> networks)
    {
        using var selection = _sessionContext.LockSelection();
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
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (_combined is not null && target is null) return;
        var dialog = new CreateNetworkDialog();
        if (target is not null)
            dialog.SetSession(target.Selection);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.NetworkName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            if (target is not null) await target.VerifyAsync();
            return await (target?.Client ?? _wslc).CreateNetworkAsync(
                name, dialog.Driver, dialog.DriverOptions, dialog.Labels, dialog.Subnet,
                dialog.Gateway, dialog.IpRange, internalNetwork: dialog.InternalNetwork);
        });
    }

    /// <summary>Command handler for remove actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RemoveAsync(NetworkInfo? network)
    {
        using var selection = _sessionContext.LockSelection();
        network ??= Selected;
        if (network is null)
        {
            return;
        }

        if (!await EnsureCurrentInventoryAsync([network]))
            return;

        if (_combined is not null)
        {
            await RemoveCombinedAsync(network);
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
        using var selection = _sessionContext.LockSelection();
        network ??= Selected;
        if (network is null)
        {
            return;
        }

        if (!await EnsureCurrentInventoryAsync([network]))
            return;

        if (_combined is not null)
        {
            await InspectCombinedAsync(network);
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
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger, startDefault: false);
        if (_combined is not null && target is null) return;
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_6b11865fce3e", "Prune networks"),
            target is null ? UiText.Get("Resource_Text_31e11fbaef14", "Remove all unused networks?")
                : UiText.Get("Networks_PruneForSession", "Remove all unused networks in {0}?", target.DisplayName),
            UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            if (target is not null) await target.VerifyAsync();
            return await (target?.Client ?? _wslc).PruneNetworksAsync();
        });
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
        using var selection = _sessionContext.LockSelection();
        var items = networks?.Where(n => n is not null && n.CanModify).ToList() ?? new List<NetworkInfo>();
        if (items.Count == 0)
        {
            IsSelectionMode = false;
            return;
        }

        if (!await EnsureCurrentInventoryAsync(items))
            return;

        if (_combined is not null)
        {
            await BulkRemoveCombinedAsync(items);
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

    private async Task<bool> EnsureCurrentInventoryAsync(IReadOnlyList<NetworkInfo> items)
    {
        if (_combined is not null)
        {
            if (_combined.IsBusy)
            {
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
                    UiText.Get("CombinedSession_RefreshBusy", "Another session refresh is already running."));
                return false;
            }
            if (items.All(item => Networks.Contains(item) && _rows.TryGetValue(item, out var row) &&
                _combined.IsCurrentRow(row)))
                return true;
        }
        else
        {
            if (_inventorySelection is { } snapshot && snapshot == _sessionContext.CaptureSnapshot() &&
                items.All(Networks.Contains))
                return true;
        }

        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
            UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        return false;
    }

    private async Task InspectCombinedAsync(NetworkInfo network)
    {
        if (_sessionActions is null || !_rows.TryGetValue(network, out var row))
            throw new InvalidOperationException("The selected network has no session target.");
        IsBusy = true;
        try
        {
            var id = !string.IsNullOrWhiteSpace(network.Id) ? network.Id : network.Name;
            var result = await _sessionActions.InspectAsync(row.Session,
                WslcResourceActionTarget.ResourceKind.Network, id);
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("CombinedSession_InspectTitle", "Inspect {0} · {1}", network.Name, row.SessionName),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not inspect network {Network} in session {Session}.", network.Name, row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_fce244da09aa", "Failed to load networks"), ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Network inspection was cancelled for session {Session}.", row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_UnexpectedCancellation", "The engine cancelled the request unexpectedly."));
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveCombinedAsync(NetworkInfo network)
    {
        if (_sessionActions is null || _combined is null || !_rows.TryGetValue(network, out var row))
            throw new InvalidOperationException("The selected network has no session target.");
        IsBusy = true;
        try
        {
            var target = WslcResourceActionTarget.Capture(row.Session, network);
            if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("CombinedSession_RemoveResourceTitle", "Remove resource"),
                    UiText.Get("CombinedSession_RemoveNetworkConfirmation", "Remove network \"{0}\" from session \"{1}\"?",
                        target.Name, target.Session.Name),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;
            if (!await EnsureCurrentInventoryAsync([network]))
                return;
            _combined.InvalidateSession(row.Session);
            var result = await _sessionActions.RemoveAsync(target);
            if (!result.Success)
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"), result.ErrorText);
            await _combined.RefreshSessionAsync(row.Session);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not remove network {Network} in session {Session}.", network.Name, row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"),
                ex is OperationCanceledException
                    ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                        "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                    : ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Network removal was interrupted for session {Session}.", row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying."));
        }
        finally { IsBusy = false; }
    }

    private async Task BulkRemoveCombinedAsync(IReadOnlyList<NetworkInfo> networks)
    {
        if (_sessionActions is null || _combined is null)
            throw new InvalidOperationException("The session actions are not configured.");
        IsBusy = true;
        try
        {
            var targets = new List<WslcResourceActionTarget>();
            foreach (var network in networks)
            {
                if (!_rows.TryGetValue(network, out var row))
                    throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                        "The session, engine, or displayed resources changed. Refresh before taking this action."));
                targets.Add(WslcResourceActionTarget.Capture(row.Session, network));
            }
            if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("Resource_Text_680e9fe1f01f", "Remove networks"),
                    UiText.Get("Networks_CombinedRemoveConfirmation",
                        "Remove {0} network(s) from their listed sessions? Some removals may fail independently.\n\n{1}",
                        targets.Count, BulkNames(targets.Select(target => $"{target.Session.Name}: {target.Name}"))),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;
            if (!await EnsureCurrentInventoryAsync(networks))
                return;
            var failures = new List<string>();
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                try
                {
                    var result = await _sessionActions.RemoveAsync(target);
                    if (!result.Success)
                        failures.Add($"{target.Session.Name}: {target.Name}: {result.ErrorText}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                           System.ComponentModel.Win32Exception or TimeoutException)
                {
                    _logger.LogWarning(ex, "Could not remove network {Network} in session {Session}.", target.Name, target.Session.Name);
                    failures.Add($"{target.Session.Name}: {target.Name}: {ex.Message}");
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Network removal was interrupted for session {Session}.", target.Session.Name);
                    failures.Add(UiText.Get("ResourceSession_BulkInterrupted",
                        "The current action's outcome may be unknown; {0} later item(s) were not attempted.",
                        targets.Count - index - 1));
                    break;
                }
            }
            IsSelectionMode = false;
            await RefreshCombinedAsync();
            if (failures.Count > 0)
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_e596a4450bb9", "Some networks were not removed"),
                    string.Join(Environment.NewLine, failures));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not prepare session-qualified network removal.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_e596a4450bb9", "Some networks were not removed"), ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Bulk network removal was interrupted.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying."));
        }
        finally { IsBusy = false; }
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
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Network operation failed for the chosen session.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"), ex.Message);
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
        using (Grid.DeferRefresh())
            foreach (var item in Networks) item.RefreshLocalizedText();
    }
}
