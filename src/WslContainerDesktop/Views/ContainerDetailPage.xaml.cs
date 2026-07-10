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
        if (LogsPanel is null || SummaryPanel is null || InspectPanel is null || StatsPanel is null)
        {
            return;
        }

        var selected = sender.SelectedItem;
        LogsPanel.Visibility = selected == TabLogs ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = selected == TabSummary ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = selected == TabStats ? Visibility.Visible : Visibility.Collapsed;
        InspectPanel.Visibility = selected == TabInspect ? Visibility.Visible : Visibility.Collapsed;
    }
}
