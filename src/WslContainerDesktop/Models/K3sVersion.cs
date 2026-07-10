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

namespace WslContainerDesktop.Models;

/// <summary>A parsed k3s / Kubernetes semantic version (e.g. "v1.36.2+k3s1").</summary>
public readonly record struct K3sVersion(int Major, int Minor, int Patch, string Original)
    : IComparable<K3sVersion>
{
    private static readonly Regex Pattern = new(@"v?(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    public static bool TryParse(string? text, out K3sVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var m = Pattern.Match(text);
        if (!m.Success)
        {
            return false;
        }

        version = new K3sVersion(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value),
            text.Trim());
        return true;
    }

    /// <summary>The Kubernetes minor channel for this version, e.g. "v1.32".</summary>
    public string MinorChannel => $"v{Major}.{Minor}";

    public int CompareTo(K3sVersion other)
    {
        if (Major != other.Major)
        {
            return Major.CompareTo(other.Major);
        }

        if (Minor != other.Minor)
        {
            return Minor.CompareTo(other.Minor);
        }

        return Patch.CompareTo(other.Patch);
    }

    public static bool operator <(K3sVersion a, K3sVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(K3sVersion a, K3sVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(K3sVersion a, K3sVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(K3sVersion a, K3sVersion b) => a.CompareTo(b) >= 0;
}
