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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// One container health observation copied from the status monitor so Compose can make dependency
/// decisions without launching a second probe.
/// </summary>
public sealed record HealthObservationRow(string Id, string Name, ContainerState ContainerState,
    NativeHealthState State, DateTimeOffset ObservedAt, ulong Generation = 0);

/// <summary>
/// Immutable point-in-time health inventory for the <c>wslc.exe</c> executable that produced it.
/// </summary>
public sealed record HealthObservationSnapshot(string ExecutablePath, DateTimeOffset ObservedAt,
    bool Available, IReadOnlyList<HealthObservationRow> Containers);

/// <summary>Read-only immutable evidence from the existing status poller. Never initiates a probe.</summary>
public interface IHealthObservationSource
{
    /// <summary>Returns the latest observed snapshot, or null before the status monitor has one.</summary>
    HealthObservationSnapshot? GetSnapshot();
}
