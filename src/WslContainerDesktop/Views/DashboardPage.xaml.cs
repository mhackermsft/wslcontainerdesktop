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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views;

/// <summary>Home page that summarizes engine health, resource counts, and running-container performance.</summary>
public sealed partial class DashboardPage : Page
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public DashboardPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
    }

    /// <summary>Dashboard summary view model bound by the page.</summary>
    public DashboardViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiText.LanguageChanged += OnUiLanguageChanged;
        ViewModel.RefreshLocalizedText();
        await ViewModel.RefreshAsync();
        ViewModel.StartStatsPolling();
    }

    private void PerfList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DashboardStatRow row)
        {
            App.Current.MainWindow?.OpenContainerLogs(row.Id, row.Name);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UiText.LanguageChanged -= OnUiLanguageChanged;
        base.OnNavigatedFrom(e);
        ViewModel.StopStatsPolling();
    }
    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
    }
}
