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

/// <summary>Parses the version text printed by <c>wslc --version</c> into comparable <see cref="Version"/> values.</summary>
public static partial class WslcVersionParser
{
    /// <summary>Extracts and parses a <c>wslc</c> or <c>container</c> version line from command output.</summary>
    public static bool TryParseOutput(string? output, out string rawVersion, out Version version)
    {
        rawVersion = string.Empty;
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = VersionLineRegex().Match(output);
        if (!match.Success)
        {
            return false;
        }

        rawVersion = match.Groups[1].Value;
        return TryParseVersion(rawVersion, out version);
    }

    /// <summary>Parses a version string and fills missing build or revision parts with zero.</summary>
    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (!Version.TryParse(value, out var parsed))
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
        return true;
    }

    /// <summary>Returns a parsed version, or null when the value is not a valid version.</summary>
    public static Version? ParseVersion(string? value) =>
        TryParseVersion(value, out var version) ? version : null;

    [GeneratedRegex(@"(?m)^(?:wslc|container)\s+(\d+(?:\.\d+){1,3})\s*$",
        RegexOptions.CultureInvariant)]
    /// <summary>Matches the exact version line formats emitted by supported preview CLIs.</summary>
    private static partial Regex VersionLineRegex();
}
