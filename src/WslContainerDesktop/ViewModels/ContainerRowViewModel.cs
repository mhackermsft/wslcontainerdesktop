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
    /// <summary>
    /// Returns the container name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Container name shown in the main grid.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Image reference the container was created from.</summary>
    [ObservableProperty]
    private string _image = string.Empty;

    /// <summary>Container lifecycle state from <c>wslc</c>, with dependent UI flags refreshed when it changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(CanOpenInBrowser))]
    private ContainerState _state;

    /// <summary>Comma-separated published-port display text for the grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenInBrowser))]
    private string _portsDisplay = string.Empty;

    /// <summary>Primary network name resolved from container inspect.</summary>
    [ObservableProperty]
    private string _network = "-";

    /// <summary>Full size text for details, including virtual size when known.</summary>
    [ObservableProperty]
    private string _sizeDisplay = "-";

    /// <summary>Compact list-column size: the writable layer only; <see cref="SizeDisplay"/> adds the virtual size.</summary>
    [ObservableProperty]
    private string _sizeShort = "-";

    /// <summary>Container creation time used for sorting and display.</summary>
    [ObservableProperty]
    private DateTimeOffset _created;

    /// <summary>True when a probe found GPU passthrough in the running container.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuTooltip))]
    private bool _hasGpu;

    /// <summary>GPU name returned by the probe, if the runtime reported one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuTooltip))]
    private string? _gpuName;

    /// <summary>App-owned health state used by the health badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    private ContainerHealthState _health = ContainerHealthState.Unknown;

    /// <summary>True when the container has either native or app-supervised health check metadata.</summary>
    [ObservableProperty]
    private bool _hasHealthCheck;

    /// <summary>Number of auto-restart attempts already made for the health watchdog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    private int _healthRestartCount;

    /// <summary>Maximum app-owned restarts allowed before the row reports a down state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    private int _healthMaxRestarts;

    /// <summary>Detailed health message shown in the badge tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    private string _healthDetail = string.Empty;

    /// <summary>True once the network has been resolved (via inspect), so we don't refetch every poll.</summary>
    public bool NetworkResolved { get; set; }

    /// <summary>
    /// The compose project this container belongs to, or <c>null</c> if it is standalone. Set by
    /// <see cref="ContainersViewModel"/> during reconcile by matching the container name against
    /// stored compose projects; used to group the Containers list.
    /// </summary>
    public string? Project { get; set; }

    /// <summary>True once GPU access has been probed for the current running instance.</summary>
    public bool GpuChecked { get; set; }

    /// <summary>Tooltip text for the GPU badge.</summary>
    public string GpuTooltip => string.IsNullOrWhiteSpace(GpuName)
        ? "GPU passthrough enabled"
        : $"GPU: {GpuName}";

    /// <summary>Tooltip text for the health badge.</summary>
    public string HealthTooltip => !string.IsNullOrWhiteSpace(HealthDetail) ? HealthDetail : Health switch
    {
        ContainerHealthState.Healthy => "Health check: healthy",
        ContainerHealthState.Degraded => HealthMaxRestarts > 0
            ? $"Health check: unhealthy — auto-restarting ({HealthRestartCount}/{HealthMaxRestarts})"
            : "Health check: unhealthy",
        ContainerHealthState.Down => HealthMaxRestarts > 0
            ? $"Health check: down after {HealthMaxRestarts} restart attempt(s)"
            : "Health check: down",
        _ => "Health check: pending",
    };

    /// <summary>Creates a grid row from the latest container model.</summary>
    public ContainerRowViewModel(ContainerInfo model)
    {
        Update(model);
    }

    /// <summary>Full container id used for <c>wslc</c> operations.</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>Latest raw container model backing this row.</summary>
    public ContainerInfo Model { get; private set; } = new();

    /// <summary>Short id displayed in compact UI surfaces.</summary>
    public string ShortId => Model.ShortId;

    /// <summary>True when commands that require a running container should be enabled.</summary>
    public bool IsRunning => State == ContainerState.Running;

    /// <summary>True when start-like commands should be enabled.</summary>
    public bool IsStopped => State is ContainerState.Stopped or ContainerState.Created;

    /// <summary>First published host port, used as the default browser target.</summary>
    public PortMapping? PrimaryHttpPort =>
        Model.Ports.FirstOrDefault(p => p.HostPort > 0);

    /// <summary>True when there is a running, published port to open; otherwise the browser action is hidden.</summary>
    public bool CanOpenInBrowser => IsRunning && PrimaryHttpPort is not null;

    /// <summary>
    /// Copies only the size fields from <paramref name="sized"/> (a slower <c>list --size</c> result),
    /// leaving state, ports and everything else as the latest poll reported them.
    /// </summary>
    public void ApplySize(ContainerInfo sized)
    {
        if (!sized.SizeKnown)
            return;

        Model.Size = sized.Size;
        Model.SizeRwBytes = sized.SizeRwBytes;
        Model.SizeRootFsBytes = sized.SizeRootFsBytes;
        SizeDisplay = Model.SizeDisplay;
        SizeShort = Model.SizeRwBytes is long ? Model.SizeRwDisplay : Model.SizeDisplay;
    }

    /// <summary>Refreshes this row in place so selection and group position can be preserved.</summary>
    public void Update(ContainerInfo model)
    {
        if (!model.SizeKnown && Model.SizeKnown)
        {
            model.Size = Model.Size;
            model.SizeRwBytes = Model.SizeRwBytes;
            model.SizeRootFsBytes = Model.SizeRootFsBytes;
        }

        Model = model;
        Id = model.Id;
        Name = model.Name;
        Image = model.Image;
        State = model.State;
        PortsDisplay = !model.PortsKnown ? "Unknown" : model.Ports.Count == 0
            ? "-"
            : string.Join(", ", model.Ports.Select(p => p.Display));
        if (model.SizeKnown)
        {
            SizeDisplay = model.SizeDisplay;
            SizeShort = model.SizeRwBytes is long ? model.SizeRwDisplay : model.SizeDisplay;
        }

        Created = model.CreatedUtc;

        // GPU access is a property of the running instance; clear it when not running so it
        // is re-probed on the next start.
        if (State != ContainerState.Running)
        {
            GpuChecked = false;
            HasGpu = false;
            GpuName = null;
        }
    }
}
