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
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

/// <summary>Page that lists WSL containers, groups them by project, and routes row actions to <c>ContainersViewModel</c>.</summary>
public sealed partial class ContainersPage : Page
{
    private readonly CollectionViewSource _groupedContainers = new() { IsSourceGrouped = true };

    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public ContainersPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ContainersViewModel>();
        InitializeComponent();

        // Bind the list to a grouped view over the project groups (headers come from ContainerGroup).
        _groupedContainers.Source = ViewModel.Groups;
        ContainersList.ItemsSource = _groupedContainers.View;
    }

    /// <summary>Container list/detail view model bound by the page.</summary>
    public ContainersViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = ViewModel.RefreshSizesAsync();
    }

    private void ContainersList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ContainerRowViewModel row)
        {
            ViewModel.Selected = row;
            Frame.Navigate(typeof(ContainerDetailPage));
        }
    }

    private void ContainersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsSelectionMode)
        {
            ViewModel.SelectedCount = ContainersList.SelectedItems.Count;
        }
    }

    private System.Collections.Generic.List<ContainerRowViewModel> SelectedRows() =>
        ContainersList.SelectedItems.OfType<ContainerRowViewModel>().ToList();

    private async void BulkStart_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.BulkStartAsync(SelectedRows());

    private async void BulkStop_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.BulkStopAsync(SelectedRows());

    private async void BulkRemove_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.BulkRemoveAsync(SelectedRows());

    private void BulkCancel_Click(object sender, RoutedEventArgs e) =>
        ViewModel.IsSelectionMode = false;

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

    private void HealthCheckMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.HealthCheckCommand.Execute(row);
        }
    }

    private void SaveAsProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.SaveAsProfileCommand.Execute(row);
        }
    }

    private async void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = row.Name + "-filesystem",
        };
        picker.FileTypeChoices.Add("Tar archive", [".tar"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());
        if (await picker.PickSaveFileAsync() is { } file)
        {
            await ViewModel.ExportContainerAsync(row, file.Path);
        }
    }

    private void AttachMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.AttachCommand.Execute(row);
        }
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.RemoveCommand.Execute(row);
        }
    }

    private static nint GetMainWindowHandle() =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
}
