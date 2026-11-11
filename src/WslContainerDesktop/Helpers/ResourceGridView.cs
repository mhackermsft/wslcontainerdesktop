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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Helpers;

/// <summary>A localized sort choice whose identity survives language changes.</summary>
public sealed class ResourceSortOption(string key, string resourceKey, string fallback) : ObservableObject
{
    /// <summary>Stable, nonlocalized column identifier.</summary>
    public string Key { get; } = key;
    /// <summary>Localized column name.</summary>
    public string Label => UiText.Get(resourceKey, fallback);
    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(Label));
}

/// <summary>Controls common to resource-list projections.</summary>
public abstract class ResourceGridViewBase : ObservableObject
{
    private string _searchText = string.Empty;
    private ResourceSortOption? _selectedSort;
    private bool _descending;
    private bool _hasNoMatches;

    /// <summary>Available sort columns, including the original inventory order.</summary>
    public ObservableCollection<ResourceSortOption> SortOptions { get; } = [];

    /// <summary>Text searched across the displayed resource information.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasFilter));
                OnPropertyChanged(nameof(FilterButtonCaption));
                OnPropertyChanged(nameof(FilterButtonName));
                Refresh();
            }
        }
    }

    /// <summary>Whether the current search hides nonmatching rows.</summary>
    public bool HasFilter => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Compact label indicating whether a filter is active.</summary>
    public string FilterButtonCaption => HasFilter
        ? UiText.Get("ResourceGrid_Filtered", "Filtered")
        : UiText.Get("ResourceGrid_Filter", "Filter");

    /// <summary>Accessible description of the filter and sort control.</summary>
    public string FilterButtonName => HasFilter
        ? UiText.Get("ResourceGrid_FilterButtonActive", "Filter and sort; filter active: {0}", SearchText.Trim())
        : UiText.Get("ResourceGrid_FilterButton", "Filter and sort");

    /// <summary>Current sort column.</summary>
    public ResourceSortOption? SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is not null && !SortOptions.Contains(value))
                throw new ArgumentException("The sort option does not belong to this grid.", nameof(value));
            if (SetProperty(ref _selectedSort, value))
            {
                OnPropertyChanged(nameof(CanReverse));
                Refresh();
            }
        }
    }

    /// <summary>Whether the selected column is sorted in reverse.</summary>
    public bool Descending
    {
        get => _descending;
        set
        {
            if (SetProperty(ref _descending, value))
            {
                OnPropertyChanged(nameof(DirectionLabel));
                Refresh();
            }
        }
    }

    /// <summary>Whether reversing the current sort has an effect.</summary>
    public bool CanReverse => SelectedSort is { Key.Length: > 0 };
    /// <summary>Localized direction displayed to the user.</summary>
    public string DirectionLabel => Descending
        ? UiText.Get("ResourceGrid_Descending", "Descending")
        : UiText.Get("ResourceGrid_Ascending", "Ascending");

    /// <summary>Whether a nonempty search excluded every available row.</summary>
    public bool HasNoMatches
    {
        get => _hasNoMatches;
        protected set => SetProperty(ref _hasNoMatches, value);
    }

    /// <summary>Selects a column or reverses its current order.</summary>
    public void SortBy(string key)
    {
        var option = SortOptions.Single(option => option.Key == key);
        if (ReferenceEquals(option, SelectedSort))
            Descending = !Descending;
        else
        {
            Descending = false;
            SelectedSort = option;
        }
    }

    /// <summary>Updates localized labels and search results without changing the controls' state.</summary>
    public void RefreshLocalizedText()
    {
        foreach (var option in SortOptions)
            option.RefreshLocalizedText();
        OnPropertyChanged(nameof(DirectionLabel));
        OnPropertyChanged(nameof(FilterButtonCaption));
        OnPropertyChanged(nameof(FilterButtonName));
        Refresh();
    }

    /// <summary>Re-evaluates the projection after source data changes.</summary>
    public abstract void Refresh();
}

/// <summary>A typed sort column with a numeric, date, or textual key.</summary>
public sealed record ResourceGridColumn<T>(
    string Key, string ResourceKey, string Fallback, Func<T, IComparable?> Value);

/// <summary>A presentation-only projection over an authoritative resource inventory.</summary>
public sealed class ResourceGridView<T> : ResourceGridViewBase where T : class
{
    private readonly ObservableCollection<T> _source;
    private readonly Func<T, IEnumerable<string?>> _searchValues;
    private readonly Dictionary<string, ResourceGridColumn<T>> _columns;
    private readonly HashSet<INotifyPropertyChanged> _subscribed = new(ReferenceEqualityComparer.Instance);
    private int _deferCount;
    private bool _refreshPending;
    private bool _subscriptionsPending;

    /// <summary>Creates a projection that preserves source row instances.</summary>
    public ResourceGridView(ObservableCollection<T> source, Func<T, IEnumerable<string?>> searchValues,
        params ResourceGridColumn<T>[] columns)
    {
        _source = source;
        _searchValues = searchValues;
        _columns = columns.ToDictionary(column => column.Key, StringComparer.Ordinal);
        SortOptions.Add(new ResourceSortOption(string.Empty, "ResourceGrid_OriginalOrder", "Original order"));
        foreach (var column in columns)
            SortOptions.Add(new ResourceSortOption(column.Key, column.ResourceKey, column.Fallback));
        SelectedSort = SortOptions[0];
        _source.CollectionChanged += (_, _) =>
        {
            if (_deferCount > 0)
                _subscriptionsPending = true;
            else
                UpdateSubscriptions();
            Refresh();
        };
        UpdateSubscriptions();
        Refresh();
    }

    /// <summary>Filtered and sorted rows, retaining their original identities.</summary>
    public ObservableCollection<T> Items { get; } = [];

    /// <summary>Raised before the displayed rows or their order are updated.</summary>
    public event EventHandler? ProjectionChanging;

    /// <summary>Raised after a complete projection, rather than after each individual row change.</summary>
    public event EventHandler? ProjectionUpdated;

    /// <summary>Coalesces a batch of source updates into one projection.</summary>
    public IDisposable DeferRefresh()
    {
        _deferCount++;
        return new Deferral(this);
    }

    private void EndDeferral()
    {
        if (--_deferCount != 0)
            return;
        if (_subscriptionsPending)
        {
            _subscriptionsPending = false;
            UpdateSubscriptions();
        }
        if (_refreshPending)
        {
            _refreshPending = false;
            Refresh();
        }
    }

    private void UpdateSubscriptions()
    {
        var current = _source.OfType<INotifyPropertyChanged>()
            .ToHashSet<INotifyPropertyChanged>(ReferenceEqualityComparer.Instance);
        foreach (var item in _subscribed.Where(item => !current.Contains(item)).ToArray())
        {
            item.PropertyChanged -= OnItemChanged;
            _subscribed.Remove(item);
        }
        foreach (var item in current.Where(item => !_subscribed.Contains(item)))
        {
            item.PropertyChanged += OnItemChanged;
            _subscribed.Add(item);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) || SelectedSort is { Key.Length: > 0 })
            Refresh();
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        if (_deferCount > 0)
        {
            _refreshPending = true;
            return;
        }
        var query = SearchText.Trim();
        IEnumerable<T> rows = _source;
        if (query.Length > 0)
            rows = rows.Where(row => _searchValues(row).Any(value =>
                value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true));
        if (SelectedSort is { Key.Length: > 0 } selected)
        {
            var column = _columns[selected.Key];
            rows = rows.OrderBy(row => column.Value(row), new ValueComparer(Descending));
        }
        var desired = rows.ToList();
        ProjectionChanging?.Invoke(this, EventArgs.Empty);
        var included = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!included.Contains(Items[i]))
                Items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], desired[i]))
                continue;
            var existing = -1;
            for (var j = i + 1; j < Items.Count; j++)
                if (ReferenceEquals(Items[j], desired[i]))
                {
                    existing = j;
                    break;
                }
            if (existing >= 0)
                Items.Move(existing, i);
            else
                Items.Insert(i, desired[i]);
        }
        HasNoMatches = query.Length > 0 && _source.Count > 0 && desired.Count == 0;
        ProjectionUpdated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Deferral(ResourceGridView<T> owner) : IDisposable
    {
        private ResourceGridView<T>? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndDeferral();
    }

    private sealed class ValueComparer(bool descending) : IComparer<IComparable?>
    {
        public int Compare(IComparable? x, IComparable? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            var result = x is string a && y is string b
                ? StringComparer.CurrentCultureIgnoreCase.Compare(a, b)
                : x.CompareTo(y);
            return descending ? -Math.Sign(result) : Math.Sign(result);
        }
    }
}
