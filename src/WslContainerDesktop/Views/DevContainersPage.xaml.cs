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
using Windows.Storage.Pickers;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class DevContainersPage : Page
{
    public DevContainersPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<DevContainersViewModel>();
        InitializeComponent();
    }

    public DevContainersViewModel ViewModel { get; }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiSafe.Run(ViewModel.RefreshAsync);
    }

    private static DevContainerRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as DevContainerRow;

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.ImportFolderAsync(folder.Path);
        }
    });

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.UpCommand.Execute(row);
        }
    }

    private void RebuildButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RebuildCommand.Execute(row);
        }
    }

    private void RebuildNoCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RebuildNoCacheCommand.Execute(row);
        }
    }

    private void TerminalButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.OpenTerminalCommand.Execute(row);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.StopCommand.Execute(row);
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
