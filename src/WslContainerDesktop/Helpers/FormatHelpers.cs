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

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Helpers;

/// <summary>Formatting helpers shared by view models and converters.</summary>
public static class FormatHelpers
{
    /// <summary>Formats a byte count using binary units such as KB, MB, and GB.</summary>
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

    /// <summary>Formats a timestamp as a short relative age such as <c>just now</c> or <c>2 hours ago</c>.</summary>
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
            return UiText.Get("Resource_Text_a7f8e7cc0d43", "just now");
        }

        if (span.TotalMinutes < 60)
        {
            var m = (int)span.TotalMinutes;
            return m == 1
            ? UiText.Get("Resource_Text_dcd75e915e17", "{0} minute ago", m)
            : UiText.Get("Resource_Text_41904cc8b86a", "{0} minutes ago", m);
        }

        if (span.TotalHours < 24)
        {
            var h = (int)span.TotalHours;
            return h == 1
            ? UiText.Get("Resource_Text_cc4eac830ba9", "{0} hour ago", h)
            : UiText.Get("Resource_Text_cee33896303a", "{0} hours ago", h);
        }

        if (span.TotalDays < 30)
        {
            var d = (int)span.TotalDays;
            return d == 1
            ? UiText.Get("Resource_Text_7a0320794cdb", "{0} day ago", d)
            : UiText.Get("Resource_Text_9344533c4118", "{0} days ago", d);
        }

        if (span.TotalDays < 365)
        {
            var mo = (int)(span.TotalDays / 30);
            return mo == 1
            ? UiText.Get("Resource_Text_6d6cc85f99eb", "{0} month ago", mo)
            : UiText.Get("Resource_Text_3c784acc314d", "{0} months ago", mo);
        }

        var y = (int)(span.TotalDays / 365);
        return y == 1
            ? UiText.Get("Resource_Text_5dfeef082bb5", "{0} year ago", y)
            : UiText.Get("Resource_Text_9a3274443563", "{0} years ago", y);
    }
}
