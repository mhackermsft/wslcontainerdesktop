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
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class K8sDetailPage : Page
{
    public K8sDetailPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<K8sDetailViewModel>();
        InitializeComponent();
        ViewModel.Deleted += OnDeleted;
    }

    public K8sDetailViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Hide the Logs tab for kinds that don't produce logs.
        TabLogs.Visibility = e.Parameter is K8sResourceRef { SupportsLogs: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (e.Parameter is K8sResourceRef reference)
        {
            await ViewModel.LoadAsync(reference);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Deleted -= OnDeleted;
    }

    private void OnDeleted()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void DetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (SummaryPanel is null || KubePanel is null || DescribePanel is null || LogsPanel is null)
        {
            return;
        }

        var selected = sender.SelectedItem;
        SummaryPanel.Visibility = selected == TabSummary ? Visibility.Visible : Visibility.Collapsed;
        KubePanel.Visibility = selected == TabKube ? Visibility.Visible : Visibility.Collapsed;
        DescribePanel.Visibility = selected == TabDescribe ? Visibility.Visible : Visibility.Collapsed;
        LogsPanel.Visibility = selected == TabLogs ? Visibility.Visible : Visibility.Collapsed;
    }
}
