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
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class AssistantViewModel : ObservableObject
{
    private readonly IContainerAssistant _assistant;
    private readonly ISettingsService _settings;
    private readonly IAiAvailabilityService _availability;
    private readonly ILogger<AssistantViewModel> _logger;
    private CancellationTokenSource? _sendCts;
    private int _turnSeq;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private const string GreetingText =
        "I can manage WSL containers, images, volumes, networks, compose templates, and scoped k3s actions through approved tools only. What would you like to do?";

    public ObservableCollection<AssistantChatMessage> Messages { get; } = new()
    {
        new AssistantChatMessage
        {
            Role = AssistantMessageRole.Assistant,
            Text = GreetingText,
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

    [ObservableProperty]
    private string _providerLabel = string.Empty;

    /// <summary>True only when AI is enabled, a provider is chosen, and
    /// <see cref="IAiAvailabilityService.IsAvailable"/> has actually verified connectivity — never
    /// an unconditional "healthy" dot merely because a provider is selected.</summary>
    [ObservableProperty]
    private bool _isProviderAvailable;

    /// <summary>Typed feedback for provider/tool failures during a turn. Cancellation is
    /// informational; real failures are Error with expandable/copyable technical details. Cleared
    /// at the start of each new turn and whenever the provider changes; the transcript is left
    /// intact either way.</summary>
    [ObservableProperty]
    private AiFeedback _feedback = AiFeedback.None;

    public bool HasPendingApproval => PendingApproval is not null;

    public bool IsWorking => IsBusy && PendingApproval is null;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsWorking));

    partial void OnPendingApprovalChanged(AssistantApprovalRequest? value)
    {
        OnPropertyChanged(nameof(HasPendingApproval));
        OnPropertyChanged(nameof(IsWorking));
    }

    public AssistantViewModel(
        IContainerAssistant assistant,
        ISettingsService settings,
        IAiAvailabilityService availability,
        ILogger<AssistantViewModel> logger)
    {
        _assistant = assistant;
        _settings = settings;
        _availability = availability;
        _logger = logger;
        RefreshProviderLabel();
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

        // Availability is (re)verified with a live round-trip elsewhere (IAiAvailabilityService);
        // reflect it here instead of always showing a green "healthy" dot.
        _availability.Changed += (_, _) => _dispatcher.TryEnqueue(RefreshProviderLabel);
        _settings.Changed += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            RefreshProviderLabel();
            Feedback = AiFeedback.None;
        });
    }

    /// <summary>Recomputes the active provider/model badge and availability dot; call whenever the
    /// panel is shown.</summary>
    public void RefreshProviderLabel()
    {
        ProviderLabel = _settings.AiProvider switch
        {
            AiProviderKind.Ollama => Format("Ollama", _settings.AiOllamaModel),
            AiProviderKind.GitHubCopilot => Format("GitHub Copilot", _settings.AiGitHubCopilotModel),
            AiProviderKind.AzureOpenAi => Format("Azure OpenAI", _settings.AiAzureOpenAiDeployment),
            AiProviderKind.OpenAi => Format("OpenAI", _settings.AiOpenAiModel),
            _ => "No AI provider configured",
        };
        IsProviderAvailable = _settings.AiFeaturesEnabled
            && _settings.AiProvider != AiProviderKind.None
            && _availability.IsAvailable;

        static string Format(string provider, string? model) =>
            string.IsNullOrWhiteSpace(model) ? provider : $"{provider} · {model.Trim()}";
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

    [RelayCommand]
    private void NewChat()
    {
        // Invalidate any in-flight turn so its result/cancellation message is discarded.
        _turnSeq++;
        _sendCts?.Cancel();
        _sendCts = null;
        _assistant.Reset();
        Messages.Clear();
        Messages.Add(new AssistantChatMessage { Role = AssistantMessageRole.Assistant, Text = GreetingText });
        PendingApproval = null;
        Draft = string.Empty;
        IsBusy = false;
        Feedback = AiFeedback.None;
    }

    [RelayCommand]
    private void DismissFeedback() => Feedback = AiFeedback.None;

    [RelayCommand]
    private void CopyFeedbackDetails()
    {
        if (!Feedback.HasTechnicalDetails)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText($"{Feedback.Title}\n{Feedback.Message}\n\n{Feedback.TechnicalDetails}");
        Clipboard.SetContent(package);
    }

    private AiErrorContext AssistantContext() => AiErrorContext.For(_settings.AiProvider, "Assistant chat");

    private async Task RunAssistantAsync(Func<CancellationToken, Task<AssistantTurnResult>> run)
    {
        var generation = ++_turnSeq;
        IsBusy = true;
        Feedback = AiFeedback.None;
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        try
        {
            var result = await run(ct);
            if (generation != _turnSeq)
            {
                return;
            }

            // Provider failures the assistant service already caught arrive as Error-role
            // messages; surface those as feedback instead of a plain chat bubble, and keep the
            // transcript limited to actual conversation turns.
            var errors = new List<string>();
            foreach (var message in result.Messages)
            {
                if (message.Role == AssistantMessageRole.Error)
                {
                    errors.Add(message.Text);
                    continue;
                }

                Messages.Add(message);
            }

            if (errors.Count > 0)
            {
                Feedback = AiFeedback.Error("Assistant error", string.Join("\n", errors));
            }

            PendingApproval = result.Approval;
        }
        catch (OperationCanceledException)
        {
            if (generation == _turnSeq)
            {
                Feedback = AiErrorClassifier.Canceled(AssistantContext());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant turn failed.");
            if (generation == _turnSeq)
            {
                Feedback = AiErrorClassifier.Classify(ex, AssistantContext(), ct);
            }
        }
        finally
        {
            if (generation == _turnSeq)
            {
                _sendCts?.Dispose();
                _sendCts = null;
                IsBusy = false;
            }
        }
    }
}
