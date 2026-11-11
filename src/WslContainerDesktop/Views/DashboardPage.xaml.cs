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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views;

/// <summary>Home page that summarizes engine health, resource counts, and running-container performance.</summary>
public sealed partial class DashboardPage : Page
{
    private NavigationView? _navigation;
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public DashboardPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
    }

    /// <summary>Dashboard summary view model bound by the page.</summary>
    public DashboardViewModel ViewModel { get; }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiText.LanguageChanged += OnUiLanguageChanged;
        ViewModel.RefreshLocalizedText();
        ViewModel.StartStatsPolling();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        for (DependencyObject? element = this; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not NavigationView navigation) continue;
            if (!ReferenceEquals(_navigation, navigation))
            {
                if (_navigation is not null) _navigation.DisplayModeChanged -= Navigation_DisplayModeChanged;
                _navigation = navigation;
                _navigation.DisplayModeChanged += Navigation_DisplayModeChanged;
            }
            break;
        }
        UpdatePerformanceLayout();
    }

    private void Navigation_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        UpdatePerformanceLayout();

    private void PerfViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePerformanceLayout();

    private void UpdatePerformanceLayout()
    {
        var wide = _navigation?.DisplayMode == NavigationViewDisplayMode.Expanded || PerfViewport.ActualWidth >= 600;
        ViewModel.SetPerformanceLayout(wide);
        PerfRoot.MinWidth = wide ? 642 : 0;
        PerfHeader.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        PerfList.Margin = wide ? new Thickness(0, 40, 0, 6) : new Thickness(0, 6, 0, 6);
    }

    private void PerfList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DashboardStatRow row)
        {
            if (!row.CanNavigate || row.Owner is null)
            {
                UiSafe.Run(() => App.Current.Services.GetRequiredService<DialogService>().ShowLocalizedMessageAsync(
                    UiText.Get("Containers_NavigationUnavailableTitle", "Container not opened"),
                    UiText.Get("Dashboard_IdentityUnavailable",
                        "This container's session or identity is incomplete. Refresh before opening it.")));
                return;
            }
            App.Current.MainWindow?.OpenContainerLogs(row.Id, row.Name, owner: row.Owner,
                expectedTarget: WslcContainerActionTarget.Capture(row.Session, row.Container));
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UiText.LanguageChanged -= OnUiLanguageChanged;
        if (_navigation is not null)
        {
            _navigation.DisplayModeChanged -= Navigation_DisplayModeChanged;
            _navigation = null;
        }
        base.OnNavigatedFrom(e);
        ViewModel.StopStatsPolling();
    }
    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
    }
}
