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

namespace WslContainerDesktop.Helpers;

/// <summary>Formatting helpers shared by view models and converters.</summary>
public static class FormatHelpers
{
    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    public static string RelativeTime(DateTimeOffset time)
    {
        if (time.ToUnixTimeSeconds() <= 0)
        {
            return "-";
        }

        var span = DateTimeOffset.UtcNow - time;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return "just now";
        }

        if (span.TotalMinutes < 60)
        {
            var m = (int)span.TotalMinutes;
            return $"{m} minute{(m == 1 ? "" : "s")} ago";
        }

        if (span.TotalHours < 24)
        {
            var h = (int)span.TotalHours;
            return $"{h} hour{(h == 1 ? "" : "s")} ago";
        }

        if (span.TotalDays < 30)
        {
            var d = (int)span.TotalDays;
            return $"{d} day{(d == 1 ? "" : "s")} ago";
        }

        if (span.TotalDays < 365)
        {
            var mo = (int)(span.TotalDays / 30);
            return $"{mo} month{(mo == 1 ? "" : "s")} ago";
        }

        var y = (int)(span.TotalDays / 365);
        return $"{y} year{(y == 1 ? "" : "s")} ago";
    }
}
