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

/// <summary>How a path changed relative to the container's image (docker-diff semantics).</summary>
public enum FsChangeKind
{
    /// <summary>Path exists in the container but not in the image.</summary>
    Added,

    /// <summary>Path exists in both but its metadata (size/mtime/mode) differs.</summary>
    Changed,

    /// <summary>Path existed in the image but has been removed from the container.</summary>
    Deleted,
}

/// <summary>
/// A single filesystem change between a container and its base image, equivalent to a line
/// of `docker diff` output.
/// </summary>
public sealed class ContainerFsChange
{
    public FsChangeKind Kind { get; init; }

    public string Path { get; init; } = string.Empty;

    /// <summary>Single-letter marker shown in the badge (A / C / D), matching `docker diff`.</summary>
    public string KindGlyph => Kind switch
    {
        FsChangeKind.Added => "A",
        FsChangeKind.Changed => "C",
        FsChangeKind.Deleted => "D",
        _ => "?",
    };

    /// <summary>Accessible/tooltip label for the change kind.</summary>
    public string KindLabel => Kind switch
    {
        FsChangeKind.Added => "Added",
        FsChangeKind.Changed => "Changed",
        FsChangeKind.Deleted => "Deleted",
        _ => "Unknown",
    };

    /// <summary>Badge color hex, consumed by <c>HexToBrushConverter</c> in XAML.</summary>
    public string KindColorHex => Kind switch
    {
        FsChangeKind.Added => "#1F7A3D",   // green
        FsChangeKind.Changed => "#BF8214", // amber
        FsChangeKind.Deleted => "#B02A2A", // red
        _ => "#6E6E6E",
    };
}
