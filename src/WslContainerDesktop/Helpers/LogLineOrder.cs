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

namespace WslContainerDesktop.Helpers;

/// <summary>
/// Puts container log lines back into the order the container wrote them.
/// </summary>
/// <remarks>
/// <c>wslc logs</c> writes the container's stdout and stderr to two separate pipes. When the
/// backlog is replayed both pipes fill at once, so reading them concurrently mixes the two
/// streams in arbitrary order (an old startup message can appear after a request made an hour
/// later). Asking for <c>--timestamps</c> gives every line an RFC 3339 prefix that we can sort
/// on, and then remove again when the user has not asked to see timestamps.
/// </remarks>
public static class LogLineOrder
{
    /// <summary>
    /// Splits a line of the form <c>2026-09-30T20:52:51.444044637Z message</c> into its time and
    /// the rest of the line. Returns false (leaving the line untouched) when there is no timestamp.
    /// </summary>
    public static bool TrySplitTimestamp(string line, out DateTimeOffset timestamp, out string message)
    {
        timestamp = default;
        message = line;

        var space = line.IndexOf(' ');
        var token = space < 0 ? line : line[..space];

        // The shortest valid form is "yyyy-MM-ddTHH:mm:ssZ" (20 characters).
        if (token.Length < 20 || token[4] != '-' || token[10] != 'T')
        {
            return false;
        }

        // DateTimeOffset parsing accepts at most 7 fractional digits, but the engine emits up to 9
        // (and trims trailing zeros), so parse the whole seconds and fraction separately.
        var zoneStart = token.IndexOfAny(['Z', '+', '-'], 19);
        if (zoneStart < 0)
        {
            return false;
        }

        var dot = token.IndexOf('.', 19);
        var secondsEnd = dot >= 0 && dot < zoneStart ? dot : zoneStart;
        var baseText = token[..secondsEnd] + token[zoneStart..];
        if (!DateTimeOffset.TryParse(baseText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        if (dot >= 0 && dot < zoneStart)
        {
            var fraction = token[(dot + 1)..zoneStart];
            if (fraction.Length == 0 || !fraction.All(char.IsAsciiDigit))
            {
                return false;
            }

            // Ticks are 100 ns, i.e. 7 digits; pad or truncate the fraction to that precision.
            var ticksText = fraction.Length >= 7 ? fraction[..7] : fraction.PadRight(7, '0');
            parsed = parsed.AddTicks(long.Parse(ticksText, CultureInfo.InvariantCulture));
        }

        timestamp = parsed;
        message = space < 0 ? string.Empty : line[(space + 1)..];
        return true;
    }

    /// <summary>
    /// Sorts timestamped log lines oldest first. The sort is stable, so lines with the same time
    /// (or without a timestamp) keep the order they arrived in; a line without a timestamp stays
    /// attached after the line that preceded it.
    /// </summary>
    public static List<string> OrderByTimestamp(IReadOnlyList<string> lines)
    {
        var keyed = new List<(DateTimeOffset Time, int Index, string Line)>(lines.Count);
        var last = DateTimeOffset.MinValue;
        for (var i = 0; i < lines.Count; i++)
        {
            if (TrySplitTimestamp(lines[i], out var time, out _))
            {
                last = time;
            }

            keyed.Add((last, i, lines[i]));
        }

        return keyed
            .OrderBy(k => k.Time)
            .ThenBy(k => k.Index)
            .Select(k => k.Line)
            .ToList();
    }

    /// <summary>Removes the leading timestamp, if present, so the line reads as the container wrote it.</summary>
    public static string StripTimestamp(string line) =>
        TrySplitTimestamp(line, out _, out var message) ? message : line;
}
