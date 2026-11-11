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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
#if !WSLCD_TEST_NO_DISPATCHER
using Microsoft.UI.Dispatching;
#endif
using Windows.ApplicationModel.DataTransfer;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.ViewModels;

/// <summary>One published endpoint: a container's port mapped to a host port.</summary>
/// <summary>One published container endpoint shown on the Ports page.</summary>
public sealed class PortEndpointRow
{
    /// <summary>
    /// Returns the container name and host port. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => $"{SessionName}: {ContainerName} {HostPort}";

    /// <summary>The owning session and captured resource identity.</summary>
    public required CombinedSessionRow Source { get; set; }
    /// <summary>Name of the session that owns this endpoint.</summary>
    public string SessionName => Source.SessionName;
    /// <summary>Name of the running container that owns this endpoint.</summary>
    public required string ContainerName { get; init; }
    /// <summary>Full container id used to distinguish same-named rows across refreshes.</summary>
    public required string ContainerId { get; init; }
    /// <summary>Port published on the Windows host.</summary>
    public required int HostPort { get; init; }
    /// <summary>Port inside the container.</summary>
    public required int ContainerPort { get; init; }
    /// <summary>Transport protocol reported by <c>wslc</c>, usually <c>tcp</c>.</summary>
    public required string Protocol { get; init; }

    /// <summary>Browser-usable URL, e.g. <c>http://localhost:8080</c>.</summary>
    public required string HostUrl { get; init; }

    /// <summary>The clickable/copyable host authority, e.g. <c>localhost:8080</c>.</summary>
    public string HostAddress => Uri.TryCreate(HostUrl, UriKind.Absolute, out var uri)
        ? uri.Authority : HostUrl;

    /// <summary>Whether the reported host address can be copied as an authority.</summary>
    public bool CanCopyAddress => Uri.TryCreate(HostUrl, UriKind.Absolute, out _);

    /// <summary>TCP endpoints are assumed to be openable in a browser.</summary>
    public bool IsHttp => Source.CanOpenEndpoint;

    /// <summary>Compact container-side port/protocol text for the list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PortDisplay => $"{ContainerPort}/{Protocol}";
}

/// <summary>
/// Aggregates every published port across all running containers into a single clickable list,
/// using explicitly loaded session inventories and the existing live management-session monitor.
/// </summary>
public partial class PortsViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string InventorySummaryDisplay => _inventory.IsBusy || _inventory.HasRefreshFailure || !_inventory.HasLoadedSelection
        ? _inventory.StatusText
        : UiText.Get("Endpoints_CombinedSummary", "{0} endpoints across {1} active sessions.",
            Endpoints.Count, _inventory.Sessions.Count(session => session.IsAvailable && session.IsSelected));

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EmptyMessageDisplay => _inventory.IsBusy
        ? UiText.Get("CombinedSession_Loading", "Reading session information…")
        : !string.IsNullOrWhiteSpace(_inventory.WarningText) ? _inventory.WarningText
        : UiText.Get("Endpoints_EmptyCombined", "No published endpoints were found in the active sessions.");

    /// <summary>Incomplete session inventories and unavailable resource identities.</summary>
    public string WarningText => string.Join(Environment.NewLine, new[]
    {
        _inventory.WarningText,
        _monitorUnavailable ? UiText.Get("Endpoints_ManagementUnavailable",
            "The selected session is unavailable; its displayed endpoints may be out of date.") : string.Empty,
        _identityUnavailable ? UiText.Get("CombinedSession_IdentityUnavailable",
            "The default session identity could not be determined; no action was started.") : string.Empty,
        Endpoints.Any(row => !row.CanCopyAddress) ? UiText.Get("Endpoints_InvalidAddress",
            "Some endpoints have invalid host addresses and cannot be copied or opened.") : string.Empty,
    }.Where(text => !string.IsNullOrWhiteSpace(text)));

    private readonly StatusMonitor _monitor;
#if !WSLCD_TEST_NO_DISPATCHER
    private readonly DispatcherQueue _dispatcher;
#endif
    private readonly CombinedSessionViewModel _inventory;
    private readonly CombinedSessionActionsViewModel _actions;
    private readonly WslcSessionContext _sessionContext;
    private readonly ILogger<PortsViewModel> _logger;
    private bool _monitorUnavailable;
    private bool _identityUnavailable;

    /// <summary>Signature of the endpoints currently shown, used to skip no-op rebuilds.</summary>
    private string _signature = string.Empty;

    /// <summary>Generated flag that switches the view between endpoint rows and the empty state.</summary>
    [ObservableProperty]
    private bool _hasEndpoints;

    /// <summary>Status text explaining whether the endpoint inventory is complete.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InventorySummaryDisplay))]
    private string _inventorySummary = "Every published port across your running containers, in one place.";

    /// <summary>Message shown when no endpoint rows are available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyMessageDisplay))]
    private string _emptyMessage = "No published ports. Start a container that publishes a port to see it here.";

    /// <summary>Published endpoints currently visible in the page.</summary>
    public ObservableCollection<PortEndpointRow> Endpoints { get; } = new();

    /// <summary>Search and sort state for the endpoint list.</summary>
    public ResourceGridView<PortEndpointRow> Grid { get; }

    /// <summary>Subscribes to the shared engine monitor and seeds the page from its latest snapshot.</summary>
    public PortsViewModel(StatusMonitor monitor, CombinedSessionViewModel inventory,
        CombinedSessionActionsViewModel actions, WslcSessionContext sessionContext, ILogger<PortsViewModel> logger)
    {
        _monitor = monitor;
        _inventory = inventory;
        _actions = actions;
        _sessionContext = sessionContext;
        _logger = logger;
        Grid = new(Endpoints, endpoint =>
            [endpoint.SessionName, endpoint.ContainerName, endpoint.ContainerId, endpoint.HostPort.ToString(),
             endpoint.PortDisplay, endpoint.HostAddress, endpoint.HostUrl],
            new("session", "ResourceGrid_Session", "Session", endpoint => endpoint.SessionName),
            new("container", "ResourceGrid_Container", "Container", endpoint => endpoint.ContainerName),
            new("hostport", "ResourceGrid_HostPort", "Host port", endpoint => endpoint.HostPort),
            new("containerport", "ResourceGrid_ContainerPort", "Container port", endpoint => endpoint.ContainerPort),
            new("address", "ResourceGrid_Address", "Address", endpoint => endpoint.HostAddress));
#if !WSLCD_TEST_NO_DISPATCHER
        _dispatcher = DispatcherQueue.GetForCurrentThread();
#endif
        _monitor.StatusChanged += OnStatusChanged;
        _inventory.PropertyChanged += OnInventoryChanged;
        Apply();
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
#if WSLCD_TEST_NO_DISPATCHER
        ApplyMonitor(e);
#else
        if (_dispatcher.HasThreadAccess)
        {
            ApplyMonitor(e);
        }
        else
        {
            _dispatcher.TryEnqueue(() => ApplyMonitor(e));
        }
#endif
    }

    private void ApplyMonitor(EngineStatusSnapshot snapshot)
    {
        _monitorUnavailable = snapshot.Health == EngineHealth.Down;
        OnPropertyChanged(nameof(WarningText));
        if (snapshot.Health is not (EngineHealth.Healthy or EngineHealth.Degraded))
            return;
        try
        {
            _identityUnavailable = false;
            _inventory.UpdateManagedContainers(snapshot.Session, snapshot.ExecutablePath, snapshot.Containers);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not identify the selected session for live endpoint updates.");
            _identityUnavailable = true;
        }
        OnPropertyChanged(nameof(WarningText));
    }

    private void OnInventoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.EndpointRows))
            Apply();
        if (args.PropertyName is nameof(CombinedSessionViewModel.StatusText) or
            nameof(CombinedSessionViewModel.WarningText) or nameof(CombinedSessionViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(InventorySummaryDisplay));
            OnPropertyChanged(nameof(EmptyMessageDisplay));
            OnPropertyChanged(nameof(WarningText));
        }
    }

    private void Apply()
    {
        var rows = _inventory.EndpointRows
            .Where(row => row.Container is not null && row.Port is not null)
            .Select(row => new PortEndpointRow
            {
                Source = row,
                ContainerName = row.Container!.Name,
                ContainerId = row.Container.Id,
                HostPort = row.Port!.HostPort,
                ContainerPort = row.Port.ContainerPort,
                Protocol = row.Port.ProtocolName,
                HostUrl = row.Port.HostUrl,
            })
            .OrderBy(r => r.HostPort)
            .ThenBy(r => r.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // StatusMonitor polls on a timer; rebuilding the collection every tick makes the list
        // visibly flicker. Skip the update entirely when the endpoint set is unchanged.
        var signature = string.Join(
            "|",
            rows.Select(r => $"{r.SessionName}:{r.Source.Session.ID}:{r.Source.Session.CreatorPid}:{r.ContainerId}:{r.ContainerName}:{r.HostPort}:{r.ContainerPort}:{r.Protocol}:{r.HostUrl}:{r.IsHttp}"));
        if (signature == _signature && Endpoints.Count == rows.Count)
        {
            for (var index = 0; index < rows.Count; index++)
                Endpoints[index].Source = rows[index].Source;
            return;
        }

        _signature = signature;

        using (Grid.DeferRefresh())
        {
            Endpoints.Clear();
            foreach (var row in rows)
                Endpoints.Add(row);
        }

        HasEndpoints = Endpoints.Count > 0;
        OnPropertyChanged(nameof(WarningText));
    }

    /// <summary>Reads all active sessions without changing the management selector.</summary>
    [RelayCommand]
    private Task RefreshAsync() => _inventory.RefreshAllAsync();

    /// <summary>Reuses recent shared inventory on navigation.</summary>
    public Task RefreshOnNavigationAsync() => _inventory.RefreshForNavigationAsync();

    /// <summary>Opens the endpoint URL with the user's default browser.</summary>
    [RelayCommand]
    private async Task OpenAsync(PortEndpointRow? row)
    {
        if (row is null || !row.IsHttp)
        {
            return;
        }

        await _actions.OpenEndpointAsync(row.Source);
    }

    /// <summary>Copies the endpoint host and port to the Windows clipboard.</summary>
    [RelayCommand]
    private void Copy(PortEndpointRow? row)
    {
        if (row is null || !row.CanCopyAddress)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(row.HostAddress);
        Clipboard.SetContent(package);
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
    }
}
