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

using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views.Controls;

/// <summary>Flyout-style chat surface hosted by <c>MainWindow</c> for local AI assistance with containers and settings.</summary>
public sealed partial class AssistantPanel : UserControl
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public AssistantPanel()
    {
        ViewModel = App.Current.Services.GetRequiredService<AssistantViewModel>();
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += Messages_CollectionChanged;
        Loaded += (_, _) => UiText.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => UiText.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Raised when the user asks the host window to close the assistant overlay.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Assistant conversation view model bound by the panel.</summary>
    public AssistantViewModel ViewModel { get; }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => Bindings.Update());

    /// <summary>Converts a Boolean to visibility for <c>x:Bind</c> expressions in the panel.</summary>
    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Converts a Boolean to the opposite visibility for <c>x:Bind</c> expressions.</summary>
    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void AssistantFeedbackBar_CloseButtonClick(InfoBar sender, object args) =>
        ViewModel.DismissFeedbackCommand.Execute(null);

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Approval is pinned outside the transcript; it never depends on auto-scroll.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?[0] is AssistantTimelineEntry entry)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // A queued scroll from a reset chat must not move the new transcript.
                if (ViewModel.Messages.Any(item => ReferenceEquals(item, entry)))
                    TranscriptList.ScrollIntoView(entry);
            });
        }
    }

    private void DraftBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || IsShiftDown())
        {
            return;
        }

        if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
}
