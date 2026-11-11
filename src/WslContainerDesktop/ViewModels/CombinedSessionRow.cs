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

using WslContainerDesktop.Models;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>One display row carrying its resource and owning session generation.</summary>
public sealed class CombinedSessionRow : ObservableObject
{
    private CombinedSessionRow(WslcSessionInfo session, string name, string detail)
    {
        Session = session;
        Name = name;
        Detail = detail;
    }

    /// <summary>The owning engine session generation.</summary>
    public WslcSessionInfo Session { get; }
    /// <summary>The unmodified session name reported by WSLC.</summary>
    public string SessionName => Session.Name;
    /// <summary>The resource name or reference.</summary>
    public string Name { get; }
    /// <summary>Additional technical information shown in the row.</summary>
    public string Detail { get; }
    /// <summary>Localized container state, refreshed in place on language changes.</summary>
    public string StateText => Container?.State switch
    {
        ContainerState.Created => UiText.Get("Workload_Text_Created_d70b9e", "Created"),
        ContainerState.Running => UiText.Get("Workload_Text_Running_f4ccae", "Running"),
        ContainerState.Stopped => UiText.Get("Workload_Text_Stopped_1a4f63", "Stopped"),
        ContainerState.Paused => UiText.Get("Common_Text0241", "Paused"),
        _ => UiText.Get("Common_Text0003", "Unknown"),
    };
    /// <summary>Accessible name for the container action menu.</summary>
    public string ActionsName => UiText.Get("CombinedSession_ActionsFor", "Actions for {0} in {1}", Name, SessionName);
    /// <summary>Accessible name for inspecting this resource.</summary>
    public string InspectName => UiText.Get("CombinedSession_InspectFor", "Inspect {0} in {1}", Name, SessionName);
    /// <summary>Accessible name for removing this resource.</summary>
    public string RemoveName => UiText.Get("CombinedSession_RemoveFor", "Remove {0} in {1}", Name, SessionName);
    /// <summary>Accessible name for opening an endpoint.</summary>
    public string OpenName => UiText.Get("CombinedSession_OpenFor", "Open endpoint {0} for {1} in {2}", Detail, Name, SessionName);
    /// <summary>The container, when this is a container or endpoint row.</summary>
    public ContainerInfo? Container { get; private init; }
    /// <summary>The image, when this is an image row.</summary>
    public ImageInfo? Image { get; private init; }
    /// <summary>The volume, when this is a volume row.</summary>
    public VolumeInfo? Volume { get; private init; }
    /// <summary>The network, when this is a network row.</summary>
    public NetworkInfo? Network { get; private init; }
    /// <summary>The published port, when this is an endpoint row.</summary>
    public PortMapping? Port { get; private init; }

    private bool HasStableSession => Session.ID > 0 && Session.CreatorPid > 0 &&
        !string.IsNullOrWhiteSpace(Session.Name);

    /// <summary>Whether this container has enough identity data for a safe action.</summary>
    public bool CanManage => HasStableSession && Container is { CreatedAtKnown: true, CreatedAt: > 0 } &&
        !string.IsNullOrWhiteSpace(Container.Id) && !string.IsNullOrWhiteSpace(Container.Name);
    /// <summary>Whether starting the container is applicable.</summary>
    public bool CanStart => CanManage && Container!.State is ContainerState.Created or ContainerState.Stopped;
    /// <summary>Whether stopping or restarting the container is applicable.</summary>
    public bool CanStop => CanManage && Container!.State == ContainerState.Running;
    /// <summary>Whether the image has a usable engine identity.</summary>
    public bool CanRemoveImage => HasStableSession && Image is not null && !string.IsNullOrWhiteSpace(Image.Id) &&
        !string.IsNullOrWhiteSpace(Image.Reference);
    /// <summary>Whether a volume can be inspected before asking to remove it.</summary>
    public bool CanRemoveVolume => HasStableSession && Volume is not null && !string.IsNullOrWhiteSpace(Volume.Name);
    /// <summary>Whether a non-built-in network has a usable engine identity.</summary>
    public bool CanRemoveNetwork => HasStableSession && Network is not null && !string.IsNullOrWhiteSpace(Network.Id) &&
        !string.IsNullOrWhiteSpace(Network.Name) &&
        !Network.Name.Equals("bridge", StringComparison.OrdinalIgnoreCase) &&
        !Network.Name.Equals("host", StringComparison.OrdinalIgnoreCase) &&
        !Network.Name.Equals("none", StringComparison.OrdinalIgnoreCase);
    /// <summary>Whether this resource has an identity suitable for inspection.</summary>
    public bool CanInspect => HasStableSession && Port is null &&
        (Container is not null && CanManage ||
         Image is not null && !string.IsNullOrWhiteSpace(Image.Id) ||
         Volume is not null && !string.IsNullOrWhiteSpace(Volume.Name) ||
         Network is not null && (!string.IsNullOrWhiteSpace(Network.Id) ||
             Network.IsBuiltIn && !string.IsNullOrWhiteSpace(Network.Name)));
    /// <summary>Whether a TCP endpoint can be opened as an HTTP URL.</summary>
    public bool CanOpenEndpoint => CanManage && Container!.State == ContainerState.Running &&
        Port is { ContainerPort: > 0 and <= 65535 } && GetEndpointUri() is not null;

    /// <summary>Builds a browser URL only from a valid reported IP address and port.</summary>
    public Uri? GetEndpointUri()
    {
        if (Port is not { Protocol: 6, HostPort: > 0 and <= 65535 })
            return null;
        var binding = Port.BindingAddress?.Trim('[', ']');
        var host = string.IsNullOrWhiteSpace(binding) || binding is "0.0.0.0" or "::"
            ? "localhost" : IPAddress.TryParse(binding, out var address) ? address.ToString() : null;
        if (host is null)
            return null;
        try
        {
            return new UriBuilder(Uri.UriSchemeHttp, host, Port.HostPort).Uri;
        }
        catch (UriFormatException)
        {
            return null; // An address that cannot form a URI is not an openable endpoint.
        }
    }

    /// <summary>Creates a container row.</summary>
    public static CombinedSessionRow FromContainer(WslcCombinedInventory.Resource<ContainerInfo> resource) =>
        new(resource.Session, resource.Value.Name, resource.Value.Image) { Container = resource.Value };
    /// <summary>Creates an image row.</summary>
    public static CombinedSessionRow FromImage(WslcCombinedInventory.Resource<ImageInfo> resource) =>
        new(resource.Session, resource.Value.Reference, resource.Value.ShortId) { Image = resource.Value };
    /// <summary>Creates a volume row.</summary>
    public static CombinedSessionRow FromVolume(WslcCombinedInventory.Resource<VolumeInfo> resource) =>
        new(resource.Session, resource.Value.Name, resource.Value.Driver) { Volume = resource.Value };
    /// <summary>Creates a network row.</summary>
    public static CombinedSessionRow FromNetwork(WslcCombinedInventory.Resource<NetworkInfo> resource) =>
        new(resource.Session, resource.Value.Name, resource.Value.Driver ?? string.Empty) { Network = resource.Value };
    /// <summary>Creates a published-port row.</summary>
    public static CombinedSessionRow FromEndpoint(WslcCombinedInventory.Endpoint endpoint) =>
        new(endpoint.Session, endpoint.Container.Name, endpoint.Port.Display)
        {
            Container = endpoint.Container,
            Port = endpoint.Port,
        };

    /// <summary>Refreshes computed display text without replacing the row.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ActionsName));
        OnPropertyChanged(nameof(InspectName));
        OnPropertyChanged(nameof(RemoveName));
        OnPropertyChanged(nameof(OpenName));
    }

    /// <summary>Provides a useful screen-reader name for the list item.</summary>
    public override string ToString() => $"{SessionName}: {Name}";
}
