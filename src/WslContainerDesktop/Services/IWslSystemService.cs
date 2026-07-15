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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Host-level operations on the WSL virtual machine itself (as opposed to the container engine):
/// reading resource limits from <c>.wslconfig</c>, reporting platform/distro info, and shutting
/// WSL down.
/// </summary>
public interface IWslSystemService
{
    /// <summary>Reads the resource limits from <c>%USERPROFILE%\.wslconfig</c> (or defaults when absent).</summary>
    Task<WslConfigInfo> ReadConfigAsync(CancellationToken ct = default);

    /// <summary>Reads host WSL platform info (<c>wsl --version</c>) and the distro list (<c>wsl -l -v</c>).</summary>
    Task<WslPlatformInfo> GetPlatformInfoAsync(CancellationToken ct = default);

    /// <summary>Shuts down all WSL distros (<c>wsl --shutdown</c>), releasing their virtual disks.</summary>
    Task<CommandResult> ShutdownWslAsync(CancellationToken ct = default);
}
