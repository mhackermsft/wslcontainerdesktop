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

namespace WslContainerDesktop.Models;

public enum NativeHealthState { Unknown, Absent, Starting, Healthy, Unhealthy, Disabled }

/// <summary>Fresh inspect evidence, independent of the container's process state.</summary>
public sealed record NativeHealthObservation(
    NativeHealthState State,
    NativeHealthOptions? Configuration = null,
    string? Diagnostic = null)
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool OwnsCommandProbe => State is not (NativeHealthState.Absent or NativeHealthState.Disabled);
}
