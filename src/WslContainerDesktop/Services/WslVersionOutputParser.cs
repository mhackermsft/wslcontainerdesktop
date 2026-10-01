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

using System.Text.RegularExpressions;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads the WSL and kernel versions from <c>wsl --version</c> without depending on the display
/// language. WSL prints one localized message whose labels vary ("Kernel version", "Version du
/// noyau", "カーネル バージョン", …) and some locales use a full-width colon (<c>：</c>). The values
/// are always filled in the same order, though: WSL, kernel, WSLg, MSRDC, Direct3D, DXCore, Windows.
/// </summary>
public static partial class WslVersionOutputParser
{
    /// <summary>
    /// Returns the WSL and kernel versions, or empty strings when they can't be found. The first
    /// version-like value is taken as WSL and the next as the kernel, so a stray line before the
    /// list (for example a warning) doesn't shift them.
    /// </summary>
    public static (string WslVersion, string KernelVersion) Parse(string output)
    {
        var values = new List<string>(2);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var separator = line.LastIndexOfAny([':', '\uFF1A']);
            if (separator < 0)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (values.Count == 0 && !VersionLike().IsMatch(value))
            {
                continue;
            }

            values.Add(value);
            if (values.Count == 2)
            {
                break;
            }
        }

        return (values.ElementAtOrDefault(0) ?? string.Empty, values.ElementAtOrDefault(1) ?? string.Empty);
    }

    [GeneratedRegex(@"^\d+(?:\.\d+)+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLike();
}
