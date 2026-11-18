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

using Windows.Storage;

namespace WslContainerDesktop.Services;

/// <summary>App-owned storage locations shared by services that keep local runtime data.</summary>
internal static class AppStoragePaths
{
    /// <summary>
    /// The packaged app's local cache folder, or <c>%LOCALAPPDATA%\WslContainerDesktop</c> when the app runs
    /// without package identity.
    /// </summary>
    public static string LocalCache()
    {
        try
        {
            return ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslContainerDesktop");
        }
    }
}
