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

namespace WslContainerDesktop.Models;

/// <summary>Immutable identity for an image, volume, or network action.</summary>
public sealed record WslcResourceActionTarget(
    WslcSessionInfo Session, WslcResourceActionTarget.ResourceKind Kind,
    string Id, string Name, DateTimeOffset? CreatedAt = null)
{
    /// <summary>Resource namespaces supported by the session view.</summary>
    public enum ResourceKind { Container, Image, Volume, Network }

    /// <summary>Captures an image identity without merging identical IDs across sessions.</summary>
    public static WslcResourceActionTarget Capture(WslcSessionInfo session, ImageInfo image) =>
        new(session, ResourceKind.Image, image.Id, image.Reference);

    /// <summary>Captures a network's engine ID and name.</summary>
    public static WslcResourceActionTarget Capture(WslcSessionInfo session, NetworkInfo network) =>
        new(session, ResourceKind.Network, network.Id ?? string.Empty, network.Name);
}
