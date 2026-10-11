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
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
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
    public override string ToString() => $"{ContainerName} {HostPort}";

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
    public string HostAddress => new Uri(HostUrl).Authority;

    /// <summary>TCP endpoints are assumed to be openable in a browser.</summary>
    public bool IsHttp => Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compact container-side port/protocol text for the list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PortDisplay => $"{ContainerPort}/{Protocol}";
}

/// <summary>
/// Aggregates every published port across all running containers into a single clickable list,
/// backed by the same live <see cref="StatusMonitor"/> data the dashboard and tray use.
/// </summary>
public partial class PortsViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string InventorySummaryDisplay => UiText.Translate(InventorySummary);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EmptyMessageDisplay => UiText.Translate(EmptyMessage);

    private readonly StatusMonitor _monitor;
    private readonly DispatcherQueue _dispatcher;

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

    /// <summary>Subscribes to the shared engine monitor and seeds the page from its latest snapshot.</summary>
    public PortsViewModel(StatusMonitor monitor)
    {
        _monitor = monitor;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _monitor.StatusChanged += OnStatusChanged;

        if (_monitor.Latest is not null)
        {
            Apply(_monitor.Latest);
        }
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            Apply(e);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Apply(e));
        }
    }

    private void Apply(EngineStatusSnapshot snapshot)
    {
        var unavailable = snapshot.Health is not (EngineHealth.Healthy or EngineHealth.Degraded);
        var unknown = snapshot.Containers.Count(c => c.State == ContainerState.Running && !c.PortsKnown);
        InventorySummary = unavailable ? "Endpoint inventory is unavailable because the engine inventory could not be read."
            : unknown > 0 ? $"Published ports are still unknown for {unknown} running container(s); the list may be incomplete."
            : "Every published port across your running containers, in one place.";
        EmptyMessage = unavailable || unknown > 0 ? InventorySummary
            : "No published ports. Start a container that publishes a port to see it here.";

        var rows = snapshot.Containers
            .Where(c => c.State == ContainerState.Running)
            .SelectMany(c => c.Ports
                .Where(p => p.HostPort > 0)
                .Select(p => new PortEndpointRow
                {
                    ContainerName = c.Name,
                    ContainerId = c.Id,
                    HostPort = p.HostPort,
                    ContainerPort = p.ContainerPort,
                    Protocol = p.ProtocolName,
                    HostUrl = p.HostUrl,
                }))
            .OrderBy(r => r.HostPort)
            .ThenBy(r => r.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // StatusMonitor polls on a timer; rebuilding the collection every tick makes the list
        // visibly flicker. Skip the update entirely when the endpoint set is unchanged.
        var signature = string.Join(
            "|",
            rows.Select(r => $"{r.ContainerId}:{r.ContainerName}:{r.HostPort}:{r.ContainerPort}:{r.Protocol}:{r.HostUrl}"));
        if (signature == _signature && Endpoints.Count == rows.Count)
        {
            return;
        }

        _signature = signature;

        Endpoints.Clear();
        foreach (var row in rows)
        {
            Endpoints.Add(row);
        }

        HasEndpoints = Endpoints.Count > 0;
    }

    /// <summary>Requests a fresh engine poll so the list reflects the latest state.</summary>
    [RelayCommand]
    private void Refresh() => _monitor.RequestRefresh();

    /// <summary>Opens the endpoint URL with the user's default browser.</summary>
    [RelayCommand]
    private void Open(PortEndpointRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.HostUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore browser launch failures (mirrors ContainersViewModel.OpenPort).
        }
    }

    /// <summary>Copies the endpoint host and port to the Windows clipboard.</summary>
    [RelayCommand]
    private void Copy(PortEndpointRow? row)
    {
        if (row is null)
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
