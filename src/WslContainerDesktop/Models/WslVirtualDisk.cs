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

/// <summary>What a <see cref="WslVirtualDisk"/> backs, which drives its label and icon.</summary>
public enum WslDiskKind
{
    /// <summary>A registered WSL distro's root filesystem (<c>ext4.vhdx</c>).</summary>
    Distro,

    /// <summary>The wslc container engine's image/container storage (<c>storage.vhdx</c>).</summary>
    EngineStorage,
}

/// <summary>
/// A WSL virtual hard disk (<c>.vhdx</c>) found on the host, with its current on-disk size.
/// These files grow as data is written but do not shrink automatically when data is deleted,
/// so the app offers a compaction action to reclaim the unused blocks.
/// </summary>
public sealed class WslVirtualDisk
{
    /// <summary>Friendly display name, e.g. "Ubuntu" or "Container engine storage".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What this disk backs.</summary>
    public WslDiskKind Kind { get; init; }

    /// <summary>Absolute path to the .vhdx file on the host.</summary>
    public string VhdxPath { get; init; } = string.Empty;

    /// <summary>Current on-disk size of the .vhdx, in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Segoe Fluent glyph for the disk's kind.</summary>
    public string Glyph => Kind == WslDiskKind.EngineStorage ? "\uEDA2" : "\uE977";

    /// <summary>Human label for the disk's kind.</summary>
    public string KindLabel => Kind == WslDiskKind.EngineStorage ? "Engine storage" : "WSL distro";
}
