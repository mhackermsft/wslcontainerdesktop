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
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

/// <summary>Page that lists forwarded or published endpoints and provides open/copy shortcuts.</summary>
public sealed partial class EndpointsPage : Page
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public EndpointsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<PortsViewModel>();
        InitializeComponent();
    }

    /// <summary>Endpoint/port view model bound by the page.</summary>
    public PortsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RefreshCommand.Execute(null);
    }

    private static PortEndpointRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PortEndpointRow;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.OpenCommand.Execute(row);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.CopyCommand.Execute(row);
        }
    }
}
