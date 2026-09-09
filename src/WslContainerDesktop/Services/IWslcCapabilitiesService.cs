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

public interface IWslcCapabilitiesService
{
    /// <summary>
    /// Gets independently probed optional features. Cancellation cancels only this caller's wait.
    /// Unknown must be surfaced with its diagnostic, not silently treated as Unsupported.
    /// </summary>
    Task<WslcCapabilities> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards cached evidence; the next request shares a fresh bounded probe.</summary>
    void Invalidate();
}
