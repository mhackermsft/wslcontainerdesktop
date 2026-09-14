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

using System.Globalization;

namespace WslContainerDesktop.Services;

/// <summary>
/// Recognizes the names a repeatedly deployed template produces. A deployment steps aside onto
/// <c>name-2</c>, <c>name-3</c> rather than disturbing what is already running, so the gallery has
/// to group those back together to offer them as separate, individually removable deployments.
/// </summary>
public static class TemplateDeploymentNaming
{
    /// <summary>
    /// True for the base name itself or one of its numbered repeats. The digits-only check matters:
    /// "sqlserver-backup" is somebody else's container, not a second deployment of "sqlserver", and
    /// treating it as one would offer to delete it.
    /// </summary>
    public static bool IsBaseOrSuffixed(string? candidate, string? baseName)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(baseName))
            return false;
        if (string.Equals(candidate, baseName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!candidate.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = candidate[(baseName.Length + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Sort key for a deployment name: the base name is first, then repeats in numeric order so
    /// "name-10" does not sort above "name-2" the way plain text ordering would.
    /// </summary>
    public static int SuffixOf(string? target, string? baseName)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(baseName))
            return int.MaxValue;
        if (string.Equals(target, baseName, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (!IsBaseOrSuffixed(target, baseName))
            return int.MaxValue;

        return int.TryParse(target[(baseName.Length + 1)..], NumberStyles.None,
            CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }
}
