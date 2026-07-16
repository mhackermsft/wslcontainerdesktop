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

namespace WslContainerDesktop.ViewModels;

/// <summary>One published endpoint: a container's port mapped to a host port.</summary>
public sealed class PortEndpointRow
{
    public required string ContainerName { get; init; }
    public required string ContainerId { get; init; }
    public required int HostPort { get; init; }
    public required int ContainerPort { get; init; }
    public required string Protocol { get; init; }

    /// <summary>Browser-usable URL, e.g. <c>http://localhost:8080</c>.</summary>
    public required string HostUrl { get; init; }

    /// <summary>The clickable/copyable host authority, e.g. <c>localhost:8080</c>.</summary>
    public string HostAddress => $"localhost:{HostPort}";

    /// <summary>TCP endpoints are assumed to be openable in a browser.</summary>
    public bool IsHttp => Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase);

    public string PortDisplay => $"{ContainerPort}/{Protocol}";
}

/// <summary>
/// Aggregates every published port across all running containers into a single clickable list,
/// backed by the same live <see cref="StatusMonitor"/> data the dashboard and tray use.
/// </summary>
public partial class PortsViewModel : ObservableObject
{
    private readonly StatusMonitor _monitor;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Signature of the endpoints currently shown, used to skip no-op rebuilds.</summary>
    private string _signature = string.Empty;

    [ObservableProperty]
    private bool _hasEndpoints;

    public ObservableCollection<PortEndpointRow> Endpoints { get; } = new();

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
            rows.Select(r => $"{r.ContainerId}:{r.HostPort}:{r.ContainerPort}:{r.Protocol}"));
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
}
