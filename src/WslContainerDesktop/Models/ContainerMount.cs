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

/// <summary>Immutable or init-only data model that carries container mount information between services and view models.</summary>
public sealed record ContainerMount(
    string Type, string? Name, string? Source, string? Destination,
    bool? ReadOnly, bool? IsAnonymous)
{
    /// <summary>Gets the volume name.</summary>
    public string? VolumeName => Type.Equals("volume", StringComparison.OrdinalIgnoreCase)
        ? IsVolumeIdentifier(Name) ? Name : IsVolumeIdentifier(Source) ? Source : null
        : null;

    /// <summary>Checks whether this value is volume identifier.</summary>
    /// <param name="value">The value value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static bool IsVolumeIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
