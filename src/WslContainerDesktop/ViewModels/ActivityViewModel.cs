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
    private readonly IWslcService _wslc;
    private readonly IEngineEventStream _stream;
    private bool _seeded;
    private bool _refreshingLanguage;

    /// <summary>Bindable state for selected filter used by the view.</summary>
    [ObservableProperty]
    private string _selectedFilter = UiText.Get("Common_Text0236", "All");

    /// <summary>Bindable state for search text used by the view.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Whether paused for view binding.</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Whether connected for view binding.</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    private string _statusMessage = UiText.Get("Common_Text0194", "Ready");

    /// <summary>Category filter chips shown above the timeline.</summary>
    public ObservableCollection<string> Filters { get; } = new() { UiText.Get("Common_Text0236", "All"), UiText.Get("Common_Text0237", "Engine"), UiText.Get("Common_Text0238", "Container"), UiText.Get("Common_Text0239", "Network"), UiText.Get("Common_Text0240", "Image") };

    /// <summary>The events matching the current filter, most-recent-first.</summary>
    public ObservableCollection<ActivityEvent> Events { get; } = new();

    /// <summary>Bindable state for has events used by the view.</summary>
    public bool HasEvents => Events.Count > 0;

    /// <summary>Creates the Activity view model and stores its injected services.</summary>
    public ActivityViewModel(IActivityLog log, IWslcService wslc, IEngineEventStream stream)
    {
        _log = log;
        _wslc = wslc;
        _stream = stream;
        _isConnected = stream.IsConnected;
        UiText.LanguageChanged += (_, _) => RefreshLocalizedText();
        _log.Events.CollectionChanged += OnLogChanged;
        _stream.ConnectionChanged += OnConnectionChanged;
        Rebuild();
    }

    /// <summary>Handles selected filter changed changes and updates related view-model state.</summary>
    partial void OnSelectedFilterChanged(string value)
    {
        if (!_refreshingLanguage) Rebuild();
    }
    /// <summary>Handles search text changed changes and updates related view-model state.</summary>
    partial void OnSearchTextChanged(string value) => Rebuild();
    /// <summary>Handles is paused changed changes and updates related view-model state.</summary>
    partial void OnIsPausedChanged(bool value)
    {
        if (value)
        {
            StatusMessage = UiText.Get("Common_Text0241", "Paused");
            return;
        }

        StatusMessage = IsConnected ? UiText.Get("Common_Text0242", "Connected to live engine events") : UiText.Get("Common_Text0243", "Event stream disconnected; polling still continues");
        Rebuild();
    }

    /// <summary>Handles log changed changes and updates related view-model state.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsPaused)
        {
            Rebuild();
        }
    }

    /// <summary>Handles connection changed changes and updates related view-model state.</summary>
    private void OnConnectionChanged(object? sender, bool connected)
    {
        IsConnected = connected;
        if (!IsPaused)
        {
            StatusMessage = connected ? UiText.Get("Common_Text0242", "Connected to live engine events") : UiText.Get("Common_Text0243", "Event stream disconnected; polling still continues");
        }
    }

    /// <summary>Command handler for initialize actions triggered from the view.</summary>
    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_seeded)
        {
            Rebuild();
            return;
        }

        _seeded = true;
        StatusMessage = UiText.Get("Common_Text0244", "Loading recent engine events…");
        try
        {
            var until = DateTimeOffset.UtcNow;
            var events = await _wslc.GetEventsAsync(until.AddHours(-1), until);
            foreach (var evt in events.OrderBy(e => e.Timestamp))
            {
                _log.RecordEngineEvent(evt);
            }

            StatusMessage = _stream.IsConnected ? UiText.Get("Common_Text0242", "Connected to live engine events") : UiText.Get("Common_Text0245", "Recent events loaded; live stream disconnected");
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Get("Common_Text0246", "Recent engine events unavailable: ") + ex.Message;
        }

        Rebuild();
    }

    /// <summary>Command handler for clear actions triggered from the view.</summary>
    [RelayCommand]
    private void Clear() => _log.Clear();

    /// <summary>Refreshes rebuild state for the view model.</summary>
    private void Rebuild()
    {
        Events.Clear();
        foreach (var evt in _log.Events.Where(Matches))
        {
            Events.Add(evt);
        }

        OnPropertyChanged(nameof(HasEvents));
    }

    /// <summary>Helper for the matches workflow in this view model.</summary>
    private bool Matches(ActivityEvent evt)
    {
        var categoryMatches = Filters.IndexOf(SelectedFilter) switch
        {
            1 => evt.Category == ActivityCategory.Engine,
            2 => evt.Category == ActivityCategory.Container,
            3 => evt.Category == ActivityCategory.Network,
            4 => evt.Category == ActivityCategory.Image,
            _ => true,
        };
        return categoryMatches && MatchesSearch(evt);
    }

    /// <summary>Helper for the matches search workflow in this view model.</summary>
    private bool MatchesSearch(ActivityEvent evt)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var needle = SearchText.Trim();
        return Contains(evt.Title, needle) || Contains(evt.DisplayTitle, needle) ||
            Contains(evt.Detail, needle) || Contains(evt.DisplayDetail, needle) ||
            Contains(evt.ActorId, needle) ||
            Contains(evt.ContainerId, needle) ||
            evt.Attributes.Any(kvp => Contains(kvp.Key, needle) || Contains(kvp.Value, needle));
    }

    /// <summary>Helper for the contains workflow in this view model.</summary>
    private static bool Contains(string? value, string needle) =>
        value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Preserves the selected category and paused state when labels change.</summary>
    private void RefreshLocalizedText()
    {
        var index = Math.Max(0, Filters.IndexOf(SelectedFilter));
        var translated = new[] { UiText.Get("Common_Text0236", "All"), UiText.Get("Common_Text0237", "Engine"),
            UiText.Get("Common_Text0238", "Container"), UiText.Get("Common_Text0239", "Network"), UiText.Get("Common_Text0240", "Image") };
        _refreshingLanguage = true;
        try
        {
            for (var i = 0; i < translated.Length; i++)
                Filters[i] = translated[i];
            SelectedFilter = Filters[index];
        }
        finally
        {
            _refreshingLanguage = false;
        }
        StatusMessage = UiText.Translate(StatusMessage);
        if (!IsPaused)
            Rebuild();
        else
        {
            var visible = Events.ToArray();
            Events.Clear();
            foreach (var entry in visible) Events.Add(entry);
        }
    }
}
