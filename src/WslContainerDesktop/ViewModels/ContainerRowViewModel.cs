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

using CommunityToolkit.Mvvm.ComponentModel;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Observable wrapper around <see cref="ContainerInfo"/> so the grid can update a
/// row in place (state, ports) without losing selection during polling.
/// </summary>
public partial class ContainerRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _image = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private ContainerState _state;

    [ObservableProperty]
    private string _portsDisplay = string.Empty;

    [ObservableProperty]
    private string _network = "-";

    [ObservableProperty]
    private DateTimeOffset _created;

    /// <summary>True once the network has been resolved (via inspect), so we don't refetch every poll.</summary>
    public bool NetworkResolved { get; set; }

    public ContainerRowViewModel(ContainerInfo model)
    {
        Update(model);
    }

    public string Id { get; private set; } = string.Empty;

    public ContainerInfo Model { get; private set; } = new();

    public string ShortId => Model.ShortId;

    public bool IsRunning => State == ContainerState.Running;

    public bool IsStopped => State is ContainerState.Stopped or ContainerState.Created;

    public PortMapping? PrimaryHttpPort =>
        Model.Ports.FirstOrDefault(p => p.HostPort > 0);

    public void Update(ContainerInfo model)
    {
        Model = model;
        Id = model.Id;
        Name = model.Name;
        Image = model.Image;
        State = model.State;
        PortsDisplay = model.Ports.Count == 0
            ? "-"
            : string.Join(", ", model.Ports.Select(p => p.Display));
        Created = model.CreatedUtc;
    }
}
