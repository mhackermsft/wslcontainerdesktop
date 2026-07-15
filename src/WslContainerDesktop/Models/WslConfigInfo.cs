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
/// The resource-limiting values read from the user's <c>%USERPROFILE%\.wslconfig</c>
/// <c>[wsl2]</c> section. When the file is absent WSL applies its built-in defaults
/// (roughly 50% of host RAM and all logical processors), so each value is nullable and
/// null means "not set — WSL default applies".
/// </summary>
public sealed class WslConfigInfo
{
    /// <summary>Full path to the .wslconfig file (whether or not it exists).</summary>
    public string ConfigPath { get; init; } = string.Empty;

    /// <summary>True when a .wslconfig file exists on disk.</summary>
    public bool Exists { get; init; }

    /// <summary>Raw <c>memory=</c> value (e.g. "8GB"), or null when unset.</summary>
    public string? Memory { get; init; }

    /// <summary>Raw <c>processors=</c> value (e.g. "4"), or null when unset.</summary>
    public string? Processors { get; init; }

    /// <summary>Raw <c>swap=</c> value (e.g. "2GB" or "0"), or null when unset.</summary>
    public string? Swap { get; init; }

    /// <summary>Display text for the memory limit, falling back to the WSL default note.</summary>
    public string MemoryDisplay => Memory ?? "Default (≈50% of host RAM)";

    /// <summary>Display text for the processor count, falling back to the WSL default note.</summary>
    public string ProcessorsDisplay => Processors ?? "Default (all logical processors)";

    /// <summary>Display text for the swap size, falling back to the WSL default note.</summary>
    public string SwapDisplay => Swap ?? "Default (≈25% of host RAM)";
}
