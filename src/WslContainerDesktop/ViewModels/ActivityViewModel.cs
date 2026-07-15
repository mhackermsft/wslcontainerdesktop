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
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the Activity page: exposes the persisted event timeline (optionally filtered by
/// category) plus Clear. The underlying <see cref="IActivityLog"/> is populated in the
/// background from engine snapshot diffs, so the feed stays live without this page polling.
/// </summary>
public partial class ActivityViewModel : ObservableObject
{
    private readonly IActivityLog _log;

    [ObservableProperty]
    private string _selectedFilter = "All";

    /// <summary>Category filter chips shown above the timeline.</summary>
    public IReadOnlyList<string> Filters { get; } = new[] { "All", "Engine", "Container", "Image" };

    /// <summary>The events matching the current filter, most-recent-first.</summary>
    public ObservableCollection<ActivityEvent> Events { get; } = new();

    public bool HasEvents => Events.Count > 0;

    public ActivityViewModel(IActivityLog log)
    {
        _log = log;
        _log.Events.CollectionChanged += OnLogChanged;
        Rebuild();
    }

    partial void OnSelectedFilterChanged(string value) => Rebuild();

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    [RelayCommand]
    private void Refresh() => Rebuild();

    [RelayCommand]
    private void Clear() => _log.Clear();

    private void Rebuild()
    {
        Events.Clear();
        foreach (var evt in _log.Events.Where(Matches))
        {
            Events.Add(evt);
        }

        OnPropertyChanged(nameof(HasEvents));
    }

    private bool Matches(ActivityEvent evt) => SelectedFilter switch
    {
        "Engine" => evt.Category == ActivityCategory.Engine,
        "Container" => evt.Category == ActivityCategory.Container,
        "Image" => evt.Category == ActivityCategory.Image,
        _ => true,
    };
}
