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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Helpers;

internal static class NetworkDisplayList
{
    private static readonly HashSet<string> BuiltInNames =
        new(StringComparer.OrdinalIgnoreCase) { "bridge", "host", "none" };

    internal static IReadOnlyList<NetworkInfo> Create(IEnumerable<NetworkInfo> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);

        var items = networks.ToList();
        foreach (var item in items)
        {
            item.IsBuiltIn = BuiltInNames.Contains(item.Name);
        }

        if (!items.Any(item => item.Name.Equals("bridge", StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(NetworkInfo.DefaultBridge());
        }

        return items
            .OrderBy(item => SortRank(item))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int SortRank(NetworkInfo item)
    {
        if (item.Name.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return item.IsBuiltIn ? 1 : 2;
    }
}
