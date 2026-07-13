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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class ContainerDetailPage : Page
{
    private const int MaxLogChars = 400_000;
    private readonly StringBuilder _logBuffer = new();

    // Context flyouts built in the constructor so event handlers have direct references.
    private readonly MenuFlyout _itemContextFlyout;
    private readonly MenuFlyout _backgroundContextFlyout;

    public ContainerDetailPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ContainersViewModel>();
        InitializeComponent();

        ViewModel.LogLineReceived += OnLogLine;
        ViewModel.LogCleared += OnLogCleared;
        ViewModel.SelectionCleared += OnSelectionCleared;

        _itemContextFlyout = BuildItemContextFlyout();
        _backgroundContextFlyout = BuildBackgroundContextFlyout();
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

    // ---- Logs tab -------------------------------------------------------

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

    // ---- Header / navigation --------------------------------------------

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

    // ---- Tab switching --------------------------------------------------

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

    // ---- Files tab — toolbar buttons ------------------------------------

    private async void FilesRefresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshFilesAsync();

    private async void FilesUp_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.NavigateUpAsync();

    private async void FilesNewFolder_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateFolderAsync();

    private async void CopyInFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());

        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            await ViewModel.CopyMultipleIntoCurrentDirectoryAsync(files.Select(f => f.Path));
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

    // ---- Files tab — list interactions ----------------------------------

    private async void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        await ViewModel.OpenFileEntryAsync(ViewModel.SelectedFile);
    }

    private void FilesList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Walk the visual tree from the tapped element to find a ListViewItem.
        var element = e.OriginalSource as DependencyObject;
        ContainerFileEntry? tappedEntry = null;

        while (element is not null)
        {
            if (element is ListViewItem lvi && lvi.Content is ContainerFileEntry entry)
            {
                tappedEntry = entry;
                break;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        var showOptions = new FlyoutShowOptions
        {
            Position = e.GetPosition(FilesList),
            ShowMode = FlyoutShowMode.Standard,
        };

        if (tappedEntry is not null)
        {
            ViewModel.SelectedFile = tappedEntry;
            _itemContextFlyout.ShowAt(FilesList, showOptions);
        }
        else
        {
            _backgroundContextFlyout.ShowAt(FilesList, showOptions);
        }
    }

    // ---- Files tab — drag-in (files from Windows Explorer → container) --

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Upload to container";
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            await ViewModel.CopyMultipleIntoCurrentDirectoryAsync(paths);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ---- Files tab — drag-out (container → Windows Explorer) ------------

    private void FilesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var entry = ViewModel.SelectedFile;
        if (entry is null || entry.IsDirectory)
        {
            // Folders require recursive staging; use Download to… instead.
            if (entry?.IsDirectory == true)
            {
                ViewModel.FilesStatusMessage = "To download a folder, right-click → Download to…";
            }

            e.Cancel = true;
            return;
        }

        var stagingTask = ViewModel.DragStagingTask;
        if (stagingTask is null || !stagingTask.IsCompletedSuccessfully || stagingTask.Result is null)
        {
            ViewModel.FilesStatusMessage = "File not yet staged — select it and try dragging again.";
            e.Cancel = true;
            return;
        }

        var tempPath = stagingTask.Result;
        if (!System.IO.File.Exists(tempPath))
        {
            e.Cancel = true;
            return;
        }

        try
        {
            // GetFileFromPathAsync is a WinRT async operation. Running it on a ThreadPool thread
            // prevents the STA re-entrancy deadlock that would occur if awaited on the UI thread
            // inside a synchronous event handler.
            var storageFile = Task.Run(async () =>
                await StorageFile.GetFileFromPathAsync(tempPath).AsTask().ConfigureAwait(false)
            ).GetAwaiter().GetResult();

            e.Data.SetStorageItems([storageFile]);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        catch
        {
            e.Cancel = true;
        }
    }

    // ---- Context flyout: item actions -----------------------------------

    private MenuFlyout BuildItemContextFlyout()
    {
        var open = new MenuFlyoutItem { Text = "Open" };
        open.Click += async (_, _) => await ViewModel.OpenFileAsync();

        var downloadTo = new MenuFlyoutItem { Text = "Download to…" };
        downloadTo.Click += CtxDownloadTo_Click;

        var copyPath = new MenuFlyoutItem { Text = "Copy path" };
        copyPath.Click += (_, _) => ViewModel.CopyPathToClipboard();

        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, _) => await ViewModel.RenameAsync();

        var delete = new MenuFlyoutItem { Text = "Delete" };
        delete.Click += async (_, _) => await ViewModel.DeleteSelectedFileAsync();

        var flyout = new MenuFlyout();
        flyout.Items.Add(open);
        flyout.Items.Add(downloadTo);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(copyPath);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(rename);
        flyout.Items.Add(delete);
        return flyout;
    }

    private async void CtxDownloadTo_Click(object sender, RoutedEventArgs e)
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

    // ---- Context flyout: background (empty space) actions ---------------

    private MenuFlyout BuildBackgroundContextFlyout()
    {
        var uploadFiles = new MenuFlyoutItem { Text = "Upload files…" };
        uploadFiles.Click += CopyInFile_Click;

        var newFolder = new MenuFlyoutItem { Text = "New folder" };
        newFolder.Click += FilesNewFolder_Click;

        var refresh = new MenuFlyoutItem { Text = "Refresh" };
        refresh.Click += FilesRefresh_Click;

        var flyout = new MenuFlyout();
        flyout.Items.Add(uploadFiles);
        flyout.Items.Add(newFolder);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(refresh);
        return flyout;
    }

    // ---- Helpers --------------------------------------------------------

    private static nint GetMainWindowHandle() =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
}
