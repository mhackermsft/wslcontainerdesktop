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

internal static class ContainerIdentity
{
    /// <summary>Correlates persisted full IDs and modern short IDs only when the match is unique.</summary>
    internal static string? ResolveId(IEnumerable<string> candidates, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        string? match = null;
        var count = 0;
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, id, StringComparison.Ordinal))
                return candidate;
            if (id.Length >= 12 && candidate.Length >= 12 &&
                id.All(char.IsAsciiHexDigit) && candidate.All(char.IsAsciiHexDigit) &&
                (candidate.StartsWith(id, StringComparison.Ordinal) || id.StartsWith(candidate, StringComparison.Ordinal)))
            {
                match = candidate;
                count++;
            }
        }
        return count == 1 ? match : null;
    }
}
