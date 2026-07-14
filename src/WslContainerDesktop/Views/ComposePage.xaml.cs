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

public sealed partial class ComposePage : Page
{
    public ComposePage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ComposeViewModel>();
        InitializeComponent();
    }

    public ComposeViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }

    private static ComposeProjectRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ComposeProjectRow;

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.UpCommand.Execute(row);
        }
    }

    private void DownButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.DownCommand.Execute(row);
        }
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RestartCommand.Execute(row);
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RemoveCommand.Execute(row);
        }
    }
}
