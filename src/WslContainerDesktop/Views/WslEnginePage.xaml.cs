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

/// <summary>Page for WSL container engine status, storage location, and WSL session maintenance actions.</summary>
public sealed partial class WslEnginePage : Page
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public WslEnginePage()
    {
        ViewModel = App.Current.Services.GetRequiredService<WslEngineViewModel>();
        InitializeComponent();
    }

    /// <summary>WSL engine view model bound by the page.</summary>
    public WslEngineViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshAsync();
    }

    private void ChangeStorageLocation_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.ChangeStorageLocationAsync(folder.Path);
        }
    });
}
