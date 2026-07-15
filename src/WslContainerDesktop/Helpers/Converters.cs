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

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Helpers;

/// <summary>Maps a <see cref="ContainerState"/> to a status color brush.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value is ContainerState s ? s : ContainerState.Unknown;
        var color = state switch
        {
            ContainerState.Running => Color.FromArgb(255, 45, 200, 95),
            ContainerState.Created => Color.FromArgb(255, 240, 180, 40),
            ContainerState.Paused => Color.FromArgb(255, 240, 180, 40),
            ContainerState.Stopped => Color.FromArgb(255, 150, 150, 150),
            _ => Color.FromArgb(255, 150, 150, 150),
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>ContainerState -> friendly display string.</summary>
public sealed class StateToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is ContainerState s ? s.ToDisplayString() : "Unknown";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Byte count -> human readable size string.</summary>
public sealed class BytesToSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is long bytes ? FormatHelpers.HumanSize(bytes) : "-";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>DateTimeOffset -> relative time string ("5 minutes ago").</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DateTimeOffset dt => FormatHelpers.RelativeTime(dt),
        null => "—",
        _ => "-",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool -> Visibility. Pass parameter "invert" to reverse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility v && v == Visibility.Visible;
}

/// <summary>Inverts a boolean (used to enable/disable controls during work).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not bool b || !b;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool b && !b;
}

/// <summary>Empty collection/string -> Visible (used to show "no items" placeholders).</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isEmpty = value switch
        {
            null => true,
            string s => string.IsNullOrEmpty(s),
            int i => i == 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };

        if (parameter is string p && string.Equals(p, "invert", StringComparison.OrdinalIgnoreCase))
        {
            isEmpty = !isEmpty;
        }

        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool -> ListViewSelectionMode. true = Multiple; false = Single, or None when
/// ConverterParameter is "None" (used by the containers list, whose default is None).</summary>
public sealed class BoolToSelectionModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var multi = value is bool b && b;
        if (multi)
        {
            return Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Multiple;
        }

        return parameter is string p && string.Equals(p, "None", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.UI.Xaml.Controls.ListViewSelectionMode.None
            : Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Single;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool -> brush for activity rows (true = error red, false = accent).</summary>
public sealed class ErrorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isError = value is bool b && b;
        return isError
            ? new SolidColorBrush(Color.FromArgb(255, 230, 70, 70))
            : (object)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool -> status brush (true = green healthy, false = red).</summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var healthy = value is bool b && b;
        var color = healthy
            ? Color.FromArgb(255, 45, 200, 95)
            : Color.FromArgb(255, 230, 70, 70);
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ContainerHealthState"/> to a badge color brush.</summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value is ContainerHealthState s ? s : ContainerHealthState.Unknown;
        var color = state switch
        {
            ContainerHealthState.Healthy => Color.FromArgb(255, 31, 122, 61),
            ContainerHealthState.Degraded => Color.FromArgb(255, 191, 130, 20),
            ContainerHealthState.Down => Color.FromArgb(255, 176, 42, 42),
            _ => Color.FromArgb(255, 110, 110, 110),
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound string equals the ConverterParameter (used for sub-nav sections).</summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    /// <summary>When true, visibility is inverted (visible when the values differ).</summary>
    public bool IsInverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var current = value as string ?? string.Empty;
        var target = parameter as string ?? string.Empty;
        var equal = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
        if (IsInverse)
        {
            equal = !equal;
        }

        return equal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Converts a "#AARRGGBB" or "#RRGGBB" hex string to a SolidColorBrush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hex = (value as string ?? "#9AA0A6").TrimStart('#');
        byte a = 255, r = 154, g = 160, b = 166;
        try
        {
            if (hex.Length == 8)
            {
                a = System.Convert.ToByte(hex.Substring(0, 2), 16);
                r = System.Convert.ToByte(hex.Substring(2, 2), 16);
                g = System.Convert.ToByte(hex.Substring(4, 2), 16);
                b = System.Convert.ToByte(hex.Substring(6, 2), 16);
            }
            else if (hex.Length == 6)
            {
                r = System.Convert.ToByte(hex.Substring(0, 2), 16);
                g = System.Convert.ToByte(hex.Substring(2, 2), 16);
                b = System.Convert.ToByte(hex.Substring(4, 2), 16);
            }
        }
        catch
        {
            // fall back to grey
        }

        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
