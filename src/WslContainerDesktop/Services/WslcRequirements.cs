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

namespace WslContainerDesktop.Services;

/// <summary>
/// Shared version constants for the minimum supported <c>wslc.exe</c> WSL containers CLI.
/// UI and validation code use this to explain when the installed engine is too old.
/// </summary>
public static class WslcRequirements
{
    /// <summary>Minimum <c>wslc.exe</c> file version that supports the app's GA container features.</summary>
    public static readonly Version MinimumVersion = new(3, 0, 1, 0);

    /// <summary>Human-readable form of <see cref="MinimumVersion"/> for messages and links.</summary>
    public const string MinimumVersionDisplay = "3.0.1";

    /// <summary>Microsoft announcement that explains how to obtain a compatible WSL containers build.</summary>
    public const string AnnouncementUrl =
        "https://blogs.windows.com/windowsdeveloper/2026/09/29/wsl-containers-now-generally-available/";
}
