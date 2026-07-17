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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class AssistantViewModel : ObservableObject
{
    private readonly IContainerAssistant _assistant;
    private CancellationTokenSource? _sendCts;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<AssistantChatMessage> Messages { get; } = new()
    {
        new AssistantChatMessage
        {
            Role = AssistantMessageRole.Assistant,
            Text = "I can manage WSL containers, images, volumes, networks, compose templates, and scoped k3s actions through approved tools only. What would you like to do?",
        },
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private AssistantApprovalRequest? _pendingApproval;

    public bool HasPendingApproval => PendingApproval is not null;

    public bool IsWorking => IsBusy && PendingApproval is null;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsWorking));

    partial void OnPendingApprovalChanged(AssistantApprovalRequest? value)
    {
        OnPropertyChanged(nameof(HasPendingApproval));
        OnPropertyChanged(nameof(IsWorking));
    }

    public AssistantViewModel(IContainerAssistant assistant)
    {
        _assistant = assistant;
        assistant.ApprovalChanged += (_, approval) =>
        {
            if (_dispatcher is null || _dispatcher.HasThreadAccess)
            {
                PendingApproval = approval;
            }
            else
            {
                _dispatcher.TryEnqueue(() => PendingApproval = approval);
            }
        };
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        Draft = string.Empty;
        PendingApproval = null;
        Messages.Add(new AssistantChatMessage { Role = AssistantMessageRole.User, Text = text });
        await RunAssistantAsync(ct => _assistant.SendAsync(text, ct));
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _sendCts?.Cancel();
    }

    [RelayCommand]
    private async Task ApproveAsync()
    {
        if (PendingApproval is not { } approval)
        {
            return;
        }

        PendingApproval = null;
        await _assistant.ApproveAsync(approval);
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (PendingApproval is not { } approval)
        {
            return;
        }

        PendingApproval = null;
        await _assistant.RejectAsync(approval);
    }

    private async Task RunAssistantAsync(Func<CancellationToken, Task<AssistantTurnResult>> run)
    {
        IsBusy = true;
        _sendCts = new CancellationTokenSource();
        try
        {
            var result = await run(_sendCts.Token);
            foreach (var message in result.Messages)
            {
                Messages.Add(message);
            }

            PendingApproval = result.Approval;
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
            IsBusy = false;
        }
    }
}
