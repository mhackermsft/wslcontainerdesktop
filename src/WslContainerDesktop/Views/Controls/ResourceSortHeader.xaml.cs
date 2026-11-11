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

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views.Controls;

/// <summary>Table heading that selects or reverses a resource sort.</summary>
public sealed partial class ResourceSortHeader : UserControl
{
    /// <summary>Dependency property backing the current projection.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ResourceGridViewBase), typeof(ResourceSortHeader),
        new PropertyMetadata(null, OnStateChanged));
    /// <summary>Dependency property backing the sort column identifier.</summary>
    public static readonly DependencyProperty SortKeyProperty = DependencyProperty.Register(
        nameof(SortKey), typeof(string), typeof(ResourceSortHeader), new PropertyMetadata(string.Empty));
    /// <summary>Dependency property backing the localized heading.</summary>
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(ResourceSortHeader), new PropertyMetadata(null, OnHeaderChanged));

    private bool _subscribed;

    /// <summary>Creates a sortable heading.</summary>
    public ResourceSortHeader()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The projection controlled by this heading.</summary>
    public ResourceGridViewBase? State
    {
        get => (ResourceGridViewBase?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>The stable identifier of the corresponding column.</summary>
    public string SortKey
    {
        get => (string)GetValue(SortKeyProperty);
        set => SetValue(SortKeyProperty, value);
    }

    /// <summary>Localized heading content.</summary>
    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    private static void OnHeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ResourceSortHeader)sender).Bindings.Update();

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var header = (ResourceSortHeader)sender;
        if (header._subscribed && args.OldValue is ResourceGridViewBase old)
            old.PropertyChanged -= header.OnSortChanged;
        if (header._subscribed && args.NewValue is ResourceGridViewBase next)
            next.PropertyChanged += header.OnSortChanged;
        header.UpdateIndicator();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed && State is { } state)
            state.PropertyChanged += OnSortChanged;
        _subscribed = true;
        UpdateIndicator();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed && State is { } state)
            state.PropertyChanged -= OnSortChanged;
        _subscribed = false;
    }

    private void OnSortChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ResourceGridViewBase.SelectedSort) or
            nameof(ResourceGridViewBase.Descending) or nameof(ResourceGridViewBase.DirectionLabel))
            UpdateIndicator();
    }

    private void UpdateIndicator()
    {
        var active = State?.SelectedSort?.Key == SortKey;
        var descending = State?.Descending == true;
        Indicator.Text = active ? descending ? "↓" : "↑" : string.Empty;
        var help = !active
            ? UiText.Get("ResourceGrid_HeaderInactive", "Activate to sort ascending.")
            : descending
                ? UiText.Get("ResourceGrid_HeaderDescending", "Sorted descending. Activate to sort ascending.")
                : UiText.Get("ResourceGrid_HeaderAscending", "Sorted ascending. Activate to sort descending.");
        AutomationProperties.SetHelpText(SortButton, help);
        ToolTipService.SetToolTip(SortButton, help);
    }

    private void Sort_Click(object sender, RoutedEventArgs args) => State?.SortBy(SortKey);
}
