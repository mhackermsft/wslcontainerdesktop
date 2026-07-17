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

using System.IO;
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
using Windows.UI;
using WslContainerDesktop.Models;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class ContainerDetailPage : Page
{
    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static string CollapseGlyph(bool collapsed) =>
        collapsed ? "\uE70D" : "\uE70E";

    public static string CollapseTooltip(bool collapsed) =>
        collapsed ? "Expand" : "Collapse";

    private const int MaxLogChars = 400_000;
    private const int MaxLogLines = 6000;
    private readonly StringBuilder _logBuffer = new();
    private readonly List<string> _lines = new();

    // Search/highlight state for the Logs tab.
    private readonly List<int> _matchLineIndices = new();
    private int _currentMatchOrdinal = -1;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _renderTimer;

    // Context flyouts built in the constructor so event handlers have direct references.
    private readonly MenuFlyout _itemContextFlyout;
    private readonly MenuFlyout _backgroundContextFlyout;

    // StorageFile staging for non-blocking drag-out support. When the selection changes to a
    // file, a background task resolves the staged temp copy into a StorageFile so that
    // DragItemsStarting can set it synchronously without blocking the UI thread.
    private Task<StorageFile?>? _stagedStorageFileTask;
    private ContainerFileEntry? _stagedForEntry;

    public ContainerDetailPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<ContainersViewModel>();
        InitializeComponent();

        ViewModel.LogLineReceived += OnLogLine;
        ViewModel.LogCleared += OnLogCleared;
        ViewModel.SelectionCleared += OnSelectionCleared;

        // When the selected file changes, begin resolving the staged temp copy into a
        // StorageFile in the background so drag-out is ready without blocking the UI thread.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContainersViewModel.SelectedFile))
            {
                OnSelectedFileChangedForDrag(ViewModel.SelectedFile);
            }
        };

        _itemContextFlyout = BuildItemContextFlyout();
        _backgroundContextFlyout = BuildBackgroundContextFlyout();
    }

    public ContainersViewModel ViewModel { get; }

    private void OnSelectedFileChangedForDrag(ContainerFileEntry? entry)
    {
        _stagedStorageFileTask = null;
        _stagedForEntry = null;

        if (entry is not { IsDirectory: false })
        {
            return;
        }

        _stagedForEntry = entry;
        var stagingTask = ViewModel.DragStagingTask;
        if (stagingTask is null)
        {
            return;
        }

        // Chain from the path-staging task to create the StorageFile on a background thread.
        _stagedStorageFileTask = stagingTask.ContinueWith(
            async t =>
            {
                var path = t.IsCompletedSuccessfully ? t.Result : null;
                if (!File.Exists(path))
                {
                    return null;
                }

                return await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            },
            TaskContinuationOptions.None
        ).Unwrap();
    }

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

        _lines.Add(line);
        if (_lines.Count > MaxLogLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLogLines);
        }

        if (IsSearchOrHighlightActive)
        {
            // Throttle rebuilds while streaming so highlighting stays responsive.
            ScheduleRender();
        }
        else
        {
            LogText.TextHighlighters.Clear();
            LogText.Text = _logBuffer.ToString();
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void OnLogCleared()
    {
        _logBuffer.Clear();
        _lines.Clear();
        _matchLineIndices.Clear();
        _currentMatchOrdinal = -1;
        LogText.TextHighlighters.Clear();
        LogText.Text = string.Empty;
        UpdateMatchUi();
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
        var wrap = WrapToggle.IsChecked == true;
        LogText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

        // Wrapping only takes effect if the ScrollViewer stops offering unlimited
        // horizontal space; otherwise the TextBlock is measured at infinite width.
        LogScroll.HorizontalScrollMode = wrap
            ? ScrollMode.Disabled
            : ScrollMode.Auto;
        LogScroll.HorizontalScrollBarVisibility = wrap
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    // ---- Logs: search / filter / highlight ------------------------------

    private bool IsSearchActive => !string.IsNullOrEmpty(SearchBox?.Text);

    private bool IsSearchOrHighlightActive =>
        IsSearchActive || HighlightErrorsToggle?.IsChecked == true;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderLogs();

    private void SearchOption_Click(object sender, RoutedEventArgs e) => RenderLogs();

    private void HighlightErrors_Click(object sender, RoutedEventArgs e) => RenderLogs();

    private void ScheduleRender()
    {
        _renderTimer ??= DispatcherQueue.CreateTimer();
        if (_renderTimer.IsRunning)
        {
            return;
        }

        _renderTimer.Interval = TimeSpan.FromMilliseconds(150);
        _renderTimer.IsRepeating = false;
        _renderTimer.Tick -= RenderTimer_Tick;
        _renderTimer.Tick += RenderTimer_Tick;
        _renderTimer.Start();
    }

    private void RenderTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        RenderLogs(autoScroll: true);

    private void RenderLogs() => RenderLogs(autoScroll: false);

    private void RenderLogs(bool autoScroll)
    {
        if (LogText is null)
        {
            return;
        }

        var query = SearchBox.Text ?? string.Empty;
        var hasQuery = query.Length > 0;
        var caseSensitive = CaseToggle.IsChecked == true;
        var filter = FilterToggle.IsChecked == true && hasQuery;
        var highlightErrors = HighlightErrorsToggle.IsChecked == true;
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Fast path: nothing active -> plain text, mirrors the original behavior.
        if (!hasQuery && !highlightErrors)
        {
            LogText.TextHighlighters.Clear();
            LogText.Text = _logBuffer.ToString();
            _matchLineIndices.Clear();
            _currentMatchOrdinal = -1;
            UpdateMatchUi();
            if (autoScroll)
            {
                LogScroll.UpdateLayout();
                LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
            }

            return;
        }

        // Choose the lines to display (all, or only matching when filtering).
        var displayLines = filter
            ? _lines.Where(l => l.Contains(query, comparison)).ToList()
            : _lines;

        // Join with a single '\n' so string offsets used for TextHighlighter ranges
        // match the character indices of LogText.Text exactly (avoids any '\r\n'
        // width ambiguity).
        var text = string.Join('\n', displayLines);
        LogText.Text = text;

        var searchRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        var errorRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        var warnRanges = new List<Microsoft.UI.Xaml.Documents.TextRange>();
        _matchLineIndices.Clear();

        var offset = 0;
        const int newLineLen = 1;
        for (var i = 0; i < displayLines.Count; i++)
        {
            var line = displayLines[i];

            if (highlightErrors)
            {
                var severity = ClassifySeverity(line);
                if (severity == LogSeverity.Error)
                {
                    errorRanges.Add(new Microsoft.UI.Xaml.Documents.TextRange(offset, line.Length));
                }
                else if (severity == LogSeverity.Warning)
                {
                    warnRanges.Add(new Microsoft.UI.Xaml.Documents.TextRange(offset, line.Length));
                }
            }

            if (hasQuery)
            {
                var lineHasMatch = false;
                var searchStart = 0;
                while (searchStart <= line.Length)
                {
                    var idx = line.IndexOf(query, searchStart, comparison);
                    if (idx < 0)
                    {
                        break;
                    }

                    searchRanges.Add(new Microsoft.UI.Xaml.Documents.TextRange(offset + idx, query.Length));
                    lineHasMatch = true;
                    searchStart = idx + query.Length;
                }

                if (lineHasMatch)
                {
                    _matchLineIndices.Add(i);
                }
            }

            offset += line.Length + newLineLen;
        }

        LogText.TextHighlighters.Clear();

        if (errorRanges.Count > 0)
        {
            AddHighlighter(
                errorRanges,
                Color.FromArgb(0xFF, 0x3A, 0x1D, 0x1D),
                Color.FromArgb(0xFF, 0xFF, 0x8A, 0x8A));
        }

        if (warnRanges.Count > 0)
        {
            AddHighlighter(
                warnRanges,
                Color.FromArgb(0xFF, 0x38, 0x2E, 0x16),
                Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E));
        }

        if (searchRanges.Count > 0)
        {
            AddHighlighter(
                searchRanges,
                Color.FromArgb(0xFF, 0xFF, 0xD5, 0x4F),
                Color.FromArgb(0xFF, 0x10, 0x10, 0x14));
        }

        // Clamp the current match ordinal to the new match set.
        if (_matchLineIndices.Count == 0)
        {
            _currentMatchOrdinal = -1;
        }
        else if (_currentMatchOrdinal >= _matchLineIndices.Count)
        {
            _currentMatchOrdinal = _matchLineIndices.Count - 1;
        }

        UpdateMatchUi();

        if (autoScroll)
        {
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        }
    }

    private void AddHighlighter(
        List<Microsoft.UI.Xaml.Documents.TextRange> ranges,
        Color? background,
        Color foreground)
    {
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Foreground = new SolidColorBrush(foreground),
            // Always set a background: TextHighlighter defaults to a system highlight
            // color (yellow) when left unset, which would leak onto severity lines.
            Background = new SolidColorBrush(background ?? Microsoft.UI.Colors.Transparent),
        };

        foreach (var range in ranges)
        {
            highlighter.Ranges.Add(range);
        }

        LogText.TextHighlighters.Add(highlighter);
    }

    private enum LogSeverity
    {
        None,
        Warning,
        Error,
    }

    private static LogSeverity ClassifySeverity(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("panic", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return LogSeverity.Error;
        }

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return LogSeverity.Warning;
        }

        return LogSeverity.None;
    }

    private void UpdateMatchUi()
    {
        var count = _matchLineIndices.Count;
        var hasMatches = count > 0;

        if (!IsSearchActive)
        {
            MatchCountText.Text = string.Empty;
        }
        else if (!hasMatches)
        {
            MatchCountText.Text = "No results";
        }
        else
        {
            var ordinal = _currentMatchOrdinal >= 0 ? _currentMatchOrdinal + 1 : 1;
            MatchCountText.Text = $"{ordinal}/{count}";
        }

        PrevMatchButton.IsEnabled = hasMatches;
        NextMatchButton.IsEnabled = hasMatches;
    }

    private void PrevMatch_Click(object sender, RoutedEventArgs e) => MoveMatch(-1);

    private void NextMatch_Click(object sender, RoutedEventArgs e) => MoveMatch(1);

    private void MoveMatch(int direction)
    {
        if (_matchLineIndices.Count == 0)
        {
            return;
        }

        if (_currentMatchOrdinal < 0)
        {
            _currentMatchOrdinal = direction > 0 ? 0 : _matchLineIndices.Count - 1;
        }
        else
        {
            _currentMatchOrdinal =
                (_currentMatchOrdinal + direction + _matchLineIndices.Count) % _matchLineIndices.Count;
        }

        ScrollToLine(_matchLineIndices[_currentMatchOrdinal]);
        UpdateMatchUi();
    }

    private void ScrollToLine(int lineIndex)
    {
        var totalLines = Math.Max(LogText.Text.Split('\n').Length, 1);
        LogScroll.UpdateLayout();
        var fraction = totalLines <= 1 ? 0 : (double)lineIndex / totalLines;
        var target = LogScroll.ScrollableHeight * fraction;
        LogScroll.ChangeView(null, target, null, disableAnimation: true);
    }

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var content = LogText.Text ?? string.Empty;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{ViewModel.Selected?.Name ?? "container"}-logs",
        };
        picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("Log file", new List<string> { ".log" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetMainWindowHandle());

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await FileIO.WriteTextAsync(file, content);
        }
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
        if (LogsPanel is null || SummaryPanel is null || InspectPanel is null || StatsPanel is null || FilesPanel is null || ChangesPanel is null)
        {
            return;
        }

        var selected = sender.SelectedItem;
        LogsPanel.Visibility = selected == TabLogs ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = selected == TabSummary ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = selected == TabStats ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = selected == TabFiles ? Visibility.Visible : Visibility.Collapsed;
        InspectPanel.Visibility = selected == TabInspect ? Visibility.Visible : Visibility.Collapsed;
        ChangesPanel.Visibility = selected == TabChanges ? Visibility.Visible : Visibility.Collapsed;

        if (selected == TabFiles)
        {
            _ = ViewModel.EnsureFilesLoadedAsync();
        }
        else if (selected == TabChanges)
        {
            _ = ViewModel.EnsureChangesLoadedAsync();
        }
    }

    private async void ChangesRefresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshChangesAsync();

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
        // Walk the visual tree from the tapped element to get the actual data item.
        // This is more reliable than ViewModel.SelectedFile, which may lag with touch input.
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

        await ViewModel.OpenFileEntryAsync(tappedEntry ?? ViewModel.SelectedFile);
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
            if (entry?.IsDirectory == true)
            {
                ViewModel.FilesStatusMessage = "To download a folder, right-click → Download to…";
            }

            e.Cancel = true;
            return;
        }

        // Check the pre-staged StorageFile (resolved in the background when the file was selected).
        if (_stagedStorageFileTask is null ||
            !_stagedStorageFileTask.IsCompletedSuccessfully ||
            _stagedStorageFileTask.Result is null ||
            _stagedForEntry != entry)
        {
            ViewModel.FilesStatusMessage = "File not yet staged for drag — select it and try again.";
            e.Cancel = true;
            return;
        }

        e.Data.SetStorageItems([_stagedStorageFileTask.Result]);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
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
