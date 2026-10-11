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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;

namespace WslContainerDesktop.Services;

/// <summary>Reads Windows display languages independently of the persistent app override.</summary>
internal static class SystemUiLanguages
{
    public static IReadOnlyList<string> Get()
    {
        if (!OperatingSystem.IsWindows()) return [];
        const uint MuiLanguageName = 0x8;
        uint count = 0, length = 0;
        if (!GetUserPreferredUILanguages(MuiLanguageName, ref count, null, ref length) || length == 0)
            return [];
        var buffer = new char[length];
        if (!GetUserPreferredUILanguages(MuiLanguageName, ref count, buffer, ref length)) return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, ref uint count,
        [Out] char[]? buffer, ref uint length);
}
