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

public sealed partial class RegistriesPage : Page
{
    public RegistriesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<RegistriesViewModel>();
        InitializeComponent();
    }

    public RegistriesViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
    }

    private static RegistryEntry? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as RegistryEntry;

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.LoginCommand.Execute(row);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.LogoutCommand.Execute(row);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RemoveCommand.Execute(row);
        }
    }
}
