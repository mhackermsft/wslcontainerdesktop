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
/// Imports VS Code dev-container metadata into the app's own dev-container model.
/// </summary>
public interface IDevContainerImporter
{
    /// <summary>
    /// Reads dev-container configuration rooted at a workspace folder.
    /// </summary>
    /// <param name="workspacePath">Folder containing the workspace and optional <c>.devcontainer</c> directory.</param>
    /// <param name="ct">Cancels file reads.</param>
    /// <returns>The imported configuration or a user-readable failure.</returns>
    Task<DevContainerImportResult> ImportAsync(string workspacePath, CancellationToken ct = default);
}
