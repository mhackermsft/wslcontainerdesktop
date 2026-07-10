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
/// Central home for cross-cutting tunable values that were previously inline magic numbers, so
/// they are discoverable and can't drift between the places that use them.
/// </summary>
internal static class AppConstants
{
    /// <summary>Minimum allowed list auto-refresh interval, in seconds.</summary>
    public const int RefreshIntervalMinSeconds = 2;

    /// <summary>Maximum allowed list auto-refresh interval, in seconds.</summary>
    public const int RefreshIntervalMaxSeconds = 120;

    /// <summary>How often the background monitor re-mints Azure ACR tokens.</summary>
    public static readonly TimeSpan AzureTokenRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>Default number of log lines to tail when first showing container/pod logs.</summary>
    public const int DefaultLogTailLines = 500;
}
