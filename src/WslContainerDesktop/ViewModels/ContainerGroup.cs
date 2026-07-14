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

using System.Collections.ObjectModel;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// A group of container rows shown under a single header in the Containers list. A group is either
/// a compose project (<see cref="IsProject"/> true, keyed by project name) or the catch-all group
/// for standalone containers. Being an <see cref="ObservableCollection{T}"/> lets a grouped
/// <c>CollectionViewSource</c> bind to it directly while the header template binds to
/// <see cref="Title"/> / <see cref="Count"/>.
/// </summary>
public sealed class ContainerGroup : ObservableCollection<ContainerRowViewModel>
{
    public ContainerGroup(string key, string title, bool isProject)
    {
        Key = key;
        Title = title;
        IsProject = isProject;
    }

    /// <summary>Stable identity for the group (project name, or empty for the standalone group).</summary>
    public string Key { get; }

    /// <summary>Header text shown for the group.</summary>
    public string Title { get; }

    /// <summary>True when the group represents a compose project (vs. standalone containers).</summary>
    public bool IsProject { get; }
}
