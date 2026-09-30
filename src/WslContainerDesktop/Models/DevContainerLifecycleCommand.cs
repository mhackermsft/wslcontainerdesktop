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

/// <summary>Model object that stores dev container lifecycle command information used by services, view models, or dialogs.</summary>
public sealed class DevContainerLifecycleCommand
{
    /// <summary>Gets or sets the step.</summary>
    public string Step { get; set; } = string.Empty;
    /// <summary>Gets or sets the command.</summary>
    public string Command { get; set; } = string.Empty;
}
