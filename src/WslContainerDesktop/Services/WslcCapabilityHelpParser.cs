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

internal static class WslcCapabilityHelpParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    internal static HashSet<string>? ReadEntries(string text, string command, bool options)
    {
        // A successful exit alone is not evidence: some CLIs return root help for unknown commands.
        if (!Regex.IsMatch(text, @"(?m)^\s*Usage:\s+wslc(?:\.exe)?\s+" +
            Regex.Escape(command) + @"\s+(?:\[|<)", RegexOptions.CultureInvariant, RegexTimeout))
        {
            return null;
        }

        if (!options && ReadEntries(text, command, true) is null)
        {
            return null;
        }

        var heading = options ? "Options:" : "Commands:";
        var lines = text.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == heading);
        if (start < 0)
        {
            return null;
        }

        var entries = new HashSet<string>(StringComparer.Ordinal);
        var pattern = options
            ? @"^\s+(?:-\S+\s+)?(?<entry>--[a-z][a-z0-9-]*)\s{2,}\S"
            : @"^\s{2,}(?<entry>[a-z][a-z0-9-]*)\s{2,}\S";
        foreach (var line in lines.Skip(start + 1))
        {
            if (string.IsNullOrWhiteSpace(line) || !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant, RegexTimeout);
            if (match.Success)
            {
                entries.Add(match.Groups["entry"].Value);
            }
        }

        // Incomplete/truncated help must not become a confident absence.
        return entries.Count > 0 && (!options || entries.Contains("--help")) ? entries : null;
    }
}
