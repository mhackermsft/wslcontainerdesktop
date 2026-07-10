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
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class ContainersPage : Page
{
    public ContainersPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ContainersViewModel>();
        InitializeComponent();
    }

    public ContainersViewModel ViewModel { get; }

    private void ContainersList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ContainerRowViewModel row)
        {
            ViewModel.Selected = row;
            Frame.Navigate(typeof(ContainerDetailPage));
        }
    }

    private static ContainerRowViewModel? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ContainerRowViewModel;

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.StartCommand.Execute(row);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.StopCommand.Execute(row);
        }
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RestartCommand.Execute(row);
        }
    }

    private void TerminalButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.TerminalCommand.Execute(row);
        }
    }

    private void OpenPortMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.OpenPortCommand.Execute(row);
        }
    }

    private void KillMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.KillCommand.Execute(row);
        }
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RemoveCommand.Execute(row);
        }
    }
}
