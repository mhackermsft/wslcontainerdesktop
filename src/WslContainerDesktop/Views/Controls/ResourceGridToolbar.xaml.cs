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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views.Controls;

/// <summary>Search and sort controls shared by resource pages.</summary>
public sealed partial class ResourceGridToolbar : UserControl
{
    /// <summary>Dependency property backing the current projection.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ResourceGridViewBase), typeof(ResourceGridToolbar),
        new PropertyMetadata(null, OnStateChanged));

    /// <summary>Creates the toolbar.</summary>
    public ResourceGridToolbar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The projection controlled by this toolbar.</summary>
    public ResourceGridViewBase State
    {
        get => (ResourceGridViewBase)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ResourceGridToolbar)sender).Bindings.Update();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        State?.RefreshLocalizedText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        UiText.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs args) => State?.RefreshLocalizedText();

    private void Flyout_Opened(object? sender, object args) => SearchBox.Focus(FocusState.Programmatic);

    private void ClearFilter_Click(object sender, RoutedEventArgs args) => State.SearchText = string.Empty;

    private void Direction_Click(object sender, RoutedEventArgs args) => State.Descending = !State.Descending;
}
