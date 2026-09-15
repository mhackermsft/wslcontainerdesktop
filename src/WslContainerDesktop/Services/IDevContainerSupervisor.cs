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

public sealed record DevContainerOperationResult(bool Success, string Detail);

public interface IDevContainerSupervisor
{
    /// <summary>
    /// Starts or rebuilds a dev container. Nonempty Windows initialize commands require explicit
    /// approval of the supplied immutable snapshot on every call, before any preparation or mutation.
    /// Missing/declined approval fails the operation; cancellation throws. Compose host hooks remain blocked.
    /// </summary>
    Task<DevContainerOperationResult> UpAsync(DevContainerConfig config, bool rebuild = false, bool noCache = false,
        CancellationToken ct = default,
        Func<DevContainerHostCommandReview, CancellationToken, Task<bool>>? approveHostCommandsAsync = null);
    Task StopAsync(DevContainerConfig config, CancellationToken ct = default);
    Task RemoveAsync(DevContainerConfig config, CancellationToken ct = default);
    void OpenTerminal(DevContainerConfig config, string containerId);
}
