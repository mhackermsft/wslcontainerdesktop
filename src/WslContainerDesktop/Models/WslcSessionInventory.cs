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

/// <summary>One session's independently observed resource inventories.</summary>
public sealed record WslcSessionInventory(
    WslcSessionInfo Session,
    WslcInventoryPart<ContainerInfo> Containers,
    WslcInventoryPart<ImageInfo> Images,
    WslcInventoryPart<VolumeInfo> Volumes,
    WslcInventoryPart<NetworkInfo> Networks,
    bool SessionChanged = false,
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>Whether every requested inventory is complete and still belongs to this session generation.</summary>
    public bool IsComplete => !SessionChanged && Containers.IsComplete && Images.IsComplete &&
        Volumes.IsComplete && Networks.IsComplete;
}

/// <summary>One resource type, distinguishing an empty inventory from a failed or timed-out read.</summary>
public sealed record WslcInventoryPart<T>(IReadOnlyList<T>? Items, string? Error = null, bool TimedOut = false,
    bool UnexpectedCancellation = false)
{
    /// <summary>True only for a successful, complete listing.</summary>
    public bool IsComplete => Items is not null && Error is null && !TimedOut && !UnexpectedCancellation;
}

/// <summary>A bounded, generation-validated container-only observation for one session.</summary>
public sealed record WslcSessionContainerInventory(
    WslcSessionInfo Session, WslcInventoryPart<ContainerInfo> Containers, bool SessionChanged = false,
    DateTimeOffset? ObservedAt = null)
{
    /// <summary>Whether the listing succeeded for the same session generation.</summary>
    public bool IsComplete => !SessionChanged && Containers.IsComplete;
}

/// <summary>One bounded background observation of non-selected sessions.</summary>
public sealed record WslcAutomationInventory(
    string ExecutablePath, IReadOnlyList<WslcSessionContainerInventory> Sessions, string? Error = null,
    string? SelectedSessionName = null, bool IsPartial = false, IReadOnlyList<string>? ActiveSessionNames = null,
    IReadOnlyList<string>? SupervisedSessionNames = null,
    IReadOnlyList<WslcSessionInfo>? DiscoveredSessions = null, DateTimeOffset? DiscoveredAt = null);
