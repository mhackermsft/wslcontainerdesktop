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

using Microsoft.UI.Xaml.Controls;

namespace WslContainerDesktop.Helpers;

/// <summary>Retains visible selections while a resource projection is reordered.</summary>
public sealed class ResourceGridSelection<T> : IDisposable where T : class
{
    private readonly ResourceGridView<T> _grid;
    private readonly ListView _list;
    private readonly Action _selectionReconciled;
    private T[] _selected = [];

    /// <summary>Tracks the selection of a list while its projection changes.</summary>
    public ResourceGridSelection(ResourceGridView<T> grid, ListView list, Action selectionReconciled)
    {
        _grid = grid;
        _list = list;
        _selectionReconciled = selectionReconciled;
        _grid.ProjectionChanging += OnProjectionChanging;
        _grid.ProjectionUpdated += OnProjectionUpdated;
    }

    private void OnProjectionChanging(object? sender, EventArgs args) =>
        _selected = _list.SelectedItems.OfType<T>().ToArray();

    private void OnProjectionUpdated(object? sender, EventArgs args)
    {
        if (_list.SelectionMode == ListViewSelectionMode.Single)
        {
            var selected = _selected.FirstOrDefault(item => _grid.Items.Contains(item));
            if (selected is not null)
                _list.SelectedItem = selected;
            else if (_list.SelectedItem is T current && !_grid.Items.Contains(current))
                _list.SelectedItem = null;
        }
        else if (_list.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            foreach (var item in _list.SelectedItems.OfType<T>()
                         .Where(item => !_grid.Items.Contains(item)).ToArray())
                _list.SelectedItems.Remove(item);
            foreach (var item in _selected.Where(item => _grid.Items.Contains(item)))
                if (!_list.SelectedItems.Contains(item))
                    _list.SelectedItems.Add(item);
        }
        _selected = [];
        _selectionReconciled();
    }

    /// <summary>Stops tracking the list when its page is unloaded.</summary>
    public void Dispose()
    {
        _grid.ProjectionChanging -= OnProjectionChanging;
        _grid.ProjectionUpdated -= OnProjectionUpdated;
    }
}
