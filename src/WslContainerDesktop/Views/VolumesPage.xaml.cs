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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslContainerDesktop.Models;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views;

/// <summary>Page that lists container volumes and supports inspect, bulk remove, and refresh operations.</summary>
public sealed partial class VolumesPage : Page
{
    private ResourceGridSelection<VolumeInfo>? _gridSelection;

    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public VolumesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<VolumesViewModel>();
        InitializeComponent();
        Loaded += (_, _) => _gridSelection = new(ViewModel.Grid, VolumesList, UpdateSelectedCount);
        Unloaded += (_, _) => { _gridSelection?.Dispose(); _gridSelection = null; };
    }

    /// <summary>Volume inventory view model bound by the page.</summary>
    public VolumesViewModel ViewModel { get; }

    private void TableRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ResourceGridLayout.UpdateTable(HeaderBorder, VolumesList, e.NewSize.Width, 720);

    private void RowRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ResourceGridLayout.UpdateRow((Grid)sender, TableRoot.ActualWidth >= 720);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiText.LanguageChanged += OnUiLanguageChanged;
        ViewModel.RefreshLocalizedText();
        UiSafe.Run(ViewModel.RefreshOnNavigationAsync);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelRefresh();
        UiText.LanguageChanged -= OnUiLanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private static VolumeInfo? VolumeOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as VolumeInfo;

    private void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeOf(sender) is { } v)
        {
            ViewModel.InspectCommand.Execute(v);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeOf(sender) is { } v)
        {
            ViewModel.RemoveCommand.Execute(v);
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectedCount();

    private void UpdateSelectedCount()
    {
        if (ViewModel.IsSelectionMode)
        {
            ViewModel.SelectedCount = SelectedRows().Count;
        }
    }

    private async void BulkRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows();
        await ViewModel.BulkRemoveAsync(selected);
    }

    private void BulkCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSelectionMode = false;
    }

    private List<VolumeInfo> SelectedRows() => VolumesList.SelectedItems.OfType<VolumeInfo>()
        .Where(ViewModel.Grid.Items.Contains).ToList();
    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
    }
}
