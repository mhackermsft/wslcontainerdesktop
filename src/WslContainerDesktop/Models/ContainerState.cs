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
/// Normalized lifecycle state from numeric or textual `list --format json` responses.
/// Verified against wslc 2.9.3: 1 = created, 2 = running, 3 = stopped/exited.
/// </summary>
public enum ContainerState
{
    /// <summary>Represents the unknown option.</summary>
    Unknown = 0,
    /// <summary>Represents the created option.</summary>
    Created = 1,
    /// <summary>Represents the running option.</summary>
    Running = 2,
    /// <summary>Represents the stopped option.</summary>
    Stopped = 3,
    /// <summary>Represents the paused option.</summary>
    Paused = 4,
}

/// <summary>Model object that stores container state extensions information used by services, view models, or dialogs.</summary>
public static class ContainerStateExtensions
{
    /// <summary>Converts model data for to display string scenarios.</summary>
    /// <param name="state">The state value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static string ToDisplayString(this ContainerState state) => state switch
    {
        ContainerState.Created => "Created",
        ContainerState.Running => "Running",
        ContainerState.Stopped => "Stopped",
        ContainerState.Paused => "Paused",
        _ => "Unknown",
    };

    /// <summary>Checks whether this value is running.</summary>
    /// <param name="state">The state value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static bool IsRunning(this ContainerState state) => state == ContainerState.Running;
}
