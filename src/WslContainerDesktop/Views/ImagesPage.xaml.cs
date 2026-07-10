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

public sealed partial class ImagesPage : Page
{
    public ImagesPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ImagesViewModel>();
        InitializeComponent();
    }

    public ImagesViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }

    private static ImageInfo? ImageOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ImageInfo;

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.RunCommand.Execute(img);
        }
    }

    private void TagMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.TagCommand.Execute(img);
        }
    }

    private void PushMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.PushCommand.Execute(img);
        }
    }

    private void InspectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.InspectCommand.Execute(img);
        }
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ImageOf(sender) is { } img)
        {
            ViewModel.RemoveCommand.Execute(img);
        }
    }
}
