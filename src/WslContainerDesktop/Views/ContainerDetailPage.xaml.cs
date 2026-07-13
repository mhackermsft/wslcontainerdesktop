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

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class ContainerDetailPage : Page
{
    private const int MaxLogChars = 400_000;
    private readonly StringBuilder _logBuffer = new();

    public ContainerDetailPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ContainersViewModel>();
        InitializeComponent();

        ViewModel.LogLineReceived += OnLogLine;
        ViewModel.LogCleared += OnLogCleared;
        ViewModel.SelectionCleared += OnSelectionCleared;
    }

    public ContainersViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logBuffer.Clear();
        LogText.Text = string.Empty;
        ViewModel.ResumeStreaming();
        ViewModel.StartStatsPolling();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.StopStreaming();
        ViewModel.StopStatsPolling();
        ViewModel.LogLineReceived -= OnLogLine;
        ViewModel.LogCleared -= OnLogCleared;
        ViewModel.SelectionCleared -= OnSelectionCleared;
    }

    private void OnLogLine(string line)
    {
        _logBuffer.AppendLine(line);
        if (_logBuffer.Length > MaxLogChars)
        {
            _logBuffer.Remove(0, _logBuffer.Length - MaxLogChars);
        }

        LogText.Text = _logBuffer.ToString();
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    private void OnLogCleared()
    {
        _logBuffer.Clear();
        LogText.Text = string.Empty;
    }

    private void OnSelectionCleared()
    {
        // The container was removed; go back to the list.
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => OnLogCleared();

    private void WrapToggle_Click(object sender, RoutedEventArgs e)
    {
        LogText.TextWrapping = WrapToggle.IsChecked == true
            ? TextWrapping.Wrap
            : TextWrapping.NoWrap;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RemoveCommand.ExecuteAsync(ViewModel.Selected);
    }

    private void DetailTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (LogsPanel is null || SummaryPanel is null || InspectPanel is null || StatsPanel is null || FilesPanel is null)
        {
            return;
        }

        var selected = sender.SelectedItem;
        LogsPanel.Visibility = selected == TabLogs ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = selected == TabSummary ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = selected == TabStats ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = selected == TabFiles ? Visibility.Visible : Visibility.Collapsed;
        InspectPanel.Visibility = selected == TabInspect ? Visibility.Visible : Visibility.Collapsed;

        if (selected == TabFiles)
        {
            _ = ViewModel.EnsureFilesLoadedAsync();
        }
    }

    private async void FilesRefresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshFilesAsync();

    private async void FilesUp_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.NavigateUpAsync();

    private async void FilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ContainerFileEntry entry)
        {
            await ViewModel.OpenFileEntryAsync(entry);
        }
    }

    private async void PreviewFile_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.PreviewFileAsync();

    private async void DeleteFile_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.DeleteSelectedFileAsync();

    private async void CopyOut_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.CopySelectedFileOutAsync(folder.Path);
        }
    }

    private async void CopyInFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.CopyIntoCurrentDirectoryAsync(file.Path);
        }
    }

    private async void CopyInFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.CopyIntoCurrentDirectoryAsync(folder.Path);
        }
    }

    private static nint GetMainWindowHandle() =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
}
