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

namespace WslContainerDesktop.Views.Controls;

public sealed partial class AssistantPanel : UserControl
{
    public AssistantPanel()
    {
        ViewModel = App.Current.Services.GetRequiredService<AssistantViewModel>();
        InitializeComponent();
        ViewModel.Messages.CollectionChanged += Messages_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public event EventHandler? CloseRequested;

    public AssistantViewModel ViewModel { get; }

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Scroll to the bottom only when a new message is appended.
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToBottom();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Bring the approve/reject prompt into view when it appears (it lives outside the Messages list).
        if (e.PropertyName == nameof(AssistantViewModel.PendingApproval) && ViewModel.PendingApproval is not null)
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TranscriptScroll.UpdateLayout();
            TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, true);
        });

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
