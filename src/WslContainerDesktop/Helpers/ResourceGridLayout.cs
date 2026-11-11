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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WslContainerDesktop.Helpers;

/// <summary>Switches resource rows and their header together using the available table width.</summary>
public static class ResourceGridLayout
{
    /// <summary>Updates the header and currently realized rows.</summary>
    public static void UpdateTable(Border header, ListView list, double width, double breakpoint)
    {
        var wide = width >= breakpoint;
        header.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in list.Items)
            if (list.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: Grid row })
                UpdateRow(row, wide);
    }

    /// <summary>Updates a newly realized row or one whose width has changed.</summary>
    public static void UpdateRow(Grid row, bool wide)
    {
        var wideGrid = row.Children.OfType<FrameworkElement>().Single(child => child.Name == "WideGrid");
        var narrowPanel = row.Children.OfType<FrameworkElement>().Single(child => child.Name == "NarrowPanel");
        wideGrid.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        narrowPanel.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
    }
}
