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

/// <summary>
/// Lifecycle state reported by wslc in the "State" integer of `list --format json`.
/// Verified against wslc 2.9.3: 1 = created, 2 = running, 3 = stopped/exited.
/// </summary>
public enum ContainerState
{
    Unknown = 0,
    Created = 1,
    Running = 2,
    Stopped = 3,
    Paused = 4,
}

public static class ContainerStateExtensions
{
    public static string ToDisplayString(this ContainerState state) => state switch
    {
        ContainerState.Created => "Created",
        ContainerState.Running => "Running",
        ContainerState.Stopped => "Stopped",
        ContainerState.Paused => "Paused",
        _ => "Unknown",
    };

    public static bool IsRunning(this ContainerState state) => state == ContainerState.Running;
}
