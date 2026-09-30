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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>View model for the AI Assistant page, managing the chat transcript, approval prompts, provider status, and cancellation.</summary>
public partial class AssistantViewModel : ObservableObject
{
    private readonly IContainerAssistant _assistant;
    private readonly ISettingsService _settings;
    private readonly IAiAvailabilityService _availability;
    private readonly ILogger<AssistantViewModel> _logger;
    private CancellationTokenSource? _sendCts;
    private int _turnSeq;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("Create the assistant view model on the UI thread.");
    private const int MaxTimelineEntries = 100;
    private const int MaxEntryCharacters = 8192;

    private const string GreetingText =
        "I can manage WSL containers, images, volumes, networks, compose templates, and scoped k3s actions through approved tools only. What would you like to do?";

    /// <summary>Transcript rows displayed by the Assistant page.</summary>
    public ObservableCollection<AssistantTimelineEntry> Messages { get; } = new()
    {
        new(0, null, null, "Assistant", GreetingText),
    };

    /// <summary>Generated status text that explains what the current assistant turn is doing.</summary>
    [ObservableProperty]
    private string _statusText = "Ready";

    /// <summary>Generated flag set after the user requests cancellation of the active turn.</summary>
    [ObservableProperty]
    private bool _isCancellationRequested;

    /// <summary>Generated flag that tells the UI older timeline entries were trimmed.</summary>
    [ObservableProperty]
    private bool _hasOmittedActivity;

    /// <summary>Generated text bound to the chat input box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    /// <summary>Generated flag used while a turn is running or waiting for approval.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    /// <summary>Generated approval request currently shown to the user.</summary>
    [ObservableProperty]
    private AssistantApprovalRequest? _pendingApproval;

    /// <summary>Generated label naming the configured AI provider and model.</summary>
    [ObservableProperty]
    private string _providerLabel = string.Empty;

    /// <summary>True only when AI is enabled, a provider is chosen, and
    /// <see cref="IAiAvailabilityService.CanUseTools"/> has observed chat and tool support — never
    /// an unconditional "healthy" dot merely because a provider is selected.</summary>
    [ObservableProperty]
    private bool _isProviderAvailable;

    /// <summary>Explains the status dot on hover. A caution dot says only that actions are
    /// unavailable, which leaves the user guessing between a stopped runtime, a missing model and a
    /// model that cannot call tools at all; those need different remedies.</summary>
    [ObservableProperty]
    private string _providerStatusDetail = string.Empty;

    /// <summary>Typed feedback for provider/tool failures during a turn. Cancellation is
    /// informational; real failures are Error with expandable/copyable technical details. Cleared
    /// at the start of each new turn and whenever the provider changes; the transcript is left
    /// intact either way.</summary>
    [ObservableProperty]
    private AiFeedback _feedback = AiFeedback.None;

    /// <summary>True when a tool call is waiting for an approve/reject decision.</summary>
    public bool HasPendingApproval => PendingApproval is not null;

    /// <summary>True while the assistant is doing work that is not blocked on approval.</summary>
    public bool IsWorking => IsBusy && PendingApproval is null;

    /// <summary>True when approve/reject commands should be enabled.</summary>
    public bool CanDecideApproval => IsBusy && HasPendingApproval && !IsCancellationRequested;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
        NotifyApprovalCommands();
    }

    partial void OnIsCancellationRequestedChanged(bool value) => NotifyApprovalCommands();

    private void NotifyApprovalCommands()
    {
        OnPropertyChanged(nameof(CanDecideApproval));
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    partial void OnPendingApprovalChanged(AssistantApprovalRequest? value)
    {
        OnPropertyChanged(nameof(HasPendingApproval));
        OnPropertyChanged(nameof(IsWorking));
        NotifyApprovalCommands();
    }

    /// <summary>Creates the assistant view model and subscribes to provider/settings changes.</summary>
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
            // This legacy event carries the decision object, but has no generation.
            // Never use its null/reset notifications to clear UI state. Service-scoped
            // progress, local decisions and turn completion own clearing instead.
            var generation = Volatile.Read(ref _turnSeq);
            if (approval is not null && IsBusy)
            {
                DispatchTurn(generation, () =>
                {
                    if (!IsCancellationRequested)
                    {
                        PendingApproval = approval;
                    }
                });
            }
        };

        // Independent feature observations are maintained elsewhere (IAiAvailabilityService);
        // reflect it here instead of always showing a green "healthy" dot.
        _availability.Changed += (_, _) => _dispatcher.TryEnqueue(RefreshProviderLabel);
        _settings.Changed += (_, _) =>
        {
            var generation = Volatile.Read(ref _turnSeq);
            _dispatcher.TryEnqueue(() =>
            {
                RefreshProviderLabel();
                if (generation == _turnSeq)
                {
                    Feedback = AiFeedback.None;
                }
            });
        };
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
            AiProviderKind.FoundryLocal => Format("Foundry Local", _settings.AiFoundryLocalModel),
            _ => "No AI provider configured",
        };
        IsProviderAvailable = _settings.AiFeaturesEnabled
            && _settings.AiProvider != AiProviderKind.None
            && _availability.CanUseTools;

        ProviderStatusDetail = !_settings.AiFeaturesEnabled
            ? "AI features are turned off.\n\nTurn them on in Settings > AI."
            : _settings.AiProvider == AiProviderKind.None
            ? "No AI provider is configured.\n\nChoose one in Settings > AI."
            : _availability.Observation is not { } observation
            ? "The provider has not been checked yet.\n\nUse Test capabilities in Settings > AI diagnostics."
            : observation.CanUseTools
            ? "Tool support observed. Every action still passes through the approval gate."
            : observation.Blocker + "\n\n" + observation.NextStep;

        static string Format(string provider, string? model) =>
            string.IsNullOrWhiteSpace(model) ? provider : $"{provider} · {AiTextSanitizer.Sanitize(model.Trim(), 160)}";
    }

    /// <summary>
    /// Re-reads provider metadata when the panel is opened. The cached observation expires and
    /// nothing re-reads it on a timer, so a panel opened later would otherwise show caution for a
    /// provider that is fine. Metadata only: no generation, download or model load is started.
    /// </summary>
    public void BeginRefreshAvailability() => _ = RefreshAvailabilityAsync();

    private async Task RefreshAvailabilityAsync()
    {
        try
        {
            await _availability.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never log provider evidence or the exception object from a background observation.
            // The dot keeps its last state and the tooltip says how to check explicitly.
            _logger.LogDebug("Provider availability refresh could not complete.");
        }
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    /// <summary>Sends the current draft to the assistant as a new turn.</summary>
    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        Draft = string.Empty;
        await RunAssistantAsync(text);
    }

    /// <summary>Requests cancellation of the active assistant turn; completed tool actions are not rolled back.</summary>
    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        IsCancellationRequested = true;
        StatusText = "Cancellation requested. Waiting for the outcome; completed actions are not rolled back.";
        _sendCts?.Cancel();
    }

    /// <summary>Approves the pending tool request.</summary>
    [RelayCommand(CanExecute = nameof(CanDecideApproval))]
    private async Task ApproveAsync()
    {
        if (!CanDecideApproval || PendingApproval is not { } approval)
        {
            return;
        }

        await DecideApprovalAsync(approval, true);
    }

    /// <summary>Rejects the pending tool request.</summary>
    [RelayCommand(CanExecute = nameof(CanDecideApproval))]
    private async Task RejectAsync()
    {
        if (!CanDecideApproval || PendingApproval is not { } approval)
        {
            return;
        }

        await DecideApprovalAsync(approval, false);
    }

    private async Task DecideApprovalAsync(AssistantApprovalRequest approval, bool isApproved)
    {
        var generation = _turnSeq;
        PendingApproval = null;
        StatusText = isApproved ? "Approval sent. Waiting for tool execution." : "Rejection sent. Waiting for the assistant.";
        try
        {
            if (isApproved)
                await _assistant.ApproveAsync(approval);
            else
                await _assistant.RejectAsync(approval);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Assistant approval decision failed ({Type}).", ex.GetType().Name);
            DispatchTurn(generation, () => Feedback = AiErrorClassifier.Classify(ex, AssistantContext()));
        }
    }

    /// <summary>Starts a fresh transcript and invalidates any in-flight turn updates.</summary>
    [RelayCommand]
    private void NewChat()
    {
        // Invalidate any in-flight turn so its result/cancellation message is discarded.
        Interlocked.Increment(ref _turnSeq);
        IsBusy = false;
        _sendCts?.Cancel();
        _sendCts = null;
        _assistant.Reset();
        Messages.Clear();
        Messages.Add(new(0, null, null, "Assistant", GreetingText));
        PendingApproval = null;
        Draft = string.Empty;
        Feedback = AiFeedback.None;
        IsCancellationRequested = false;
        HasOmittedActivity = false;
        StatusText = "New chat. Any previously completed actions have not been rolled back.";
    }

    /// <summary>Clears the assistant feedback banner.</summary>
    [RelayCommand]
    private void DismissFeedback() => Feedback = AiFeedback.None;

    /// <summary>Copies technical feedback details to the clipboard for troubleshooting.</summary>
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

    private void DispatchTurn(int generation, Action update) =>
        AssistantTurnDispatch.Queue(generation, () => _turnSeq, () => IsBusy,
            action =>
            {
                if (!_dispatcher.TryEnqueue(() => action()))
                    _logger.LogDebug("Assistant progress was discarded because the UI dispatcher is shutting down.");
            }, update);

    private async Task RunAssistantAsync(string text)
    {
        var generation = Interlocked.Increment(ref _turnSeq);
        IsBusy = true;
        IsCancellationRequested = false;
        PendingApproval = null;
        StatusText = "Preparing assistant request…";
        Feedback = AiFeedback.None;
        AddEntry(new(generation, null, null, "You", AiTextSanitizer.Sanitize(text, MaxEntryCharacters)));
        var cts = new CancellationTokenSource();
        _sendCts = cts;
        var ct = cts.Token;
        try
        {
            // Capture this turn, never read the current turn when receiving its progress.
            var result = await _assistant.SendAsync(text,
                progress => DispatchTurn(generation, () => ApplyProgress(generation, progress)), ct);
            DispatchTurn(generation, () =>
            {
                var errors = new List<string>();
                foreach (var message in result.Messages)
                {
                    if (message.Role == AssistantMessageRole.Error)
                    {
                        errors.Add(message.Text);
                    }
                    else if (message.Role == AssistantMessageRole.Assistant)
                    {
                        // Replace interim narration with the final answer, rather than
                        // repeating it or presenting it as authoritative execution evidence.
                        SetNarration(generation, message.Text, append: false);
                    }
                    else if (!Messages.Any(entry => entry.Generation == generation &&
                        entry.Kind == AiChatProgressKind.ToolResult && entry.Text == message.Text))
                    {
                        AddEntry(new(generation, AiChatProgressKind.ToolResult, null, ToolResultLabel(message.Text),
                            AiTextSanitizer.Sanitize(message.Text, MaxEntryCharacters)));
                    }
                }

                if (errors.Count > 0)
                    Feedback = AiFeedback.Error("Assistant error", string.Join("\n", errors));
                StatusText = errors.Count > 0 ? "Assistant turn failed." : IsCancellationRequested
                    ? "Turn finished after cancellation was requested. Review recorded outcomes; no actions were rolled back."
                    : "Turn complete. Model narration is not proof of execution; review recorded tool outcomes.";
            });
        }
        catch (OperationCanceledException ex)
        {
            DispatchTurn(generation, () =>
            {
                Feedback = AiErrorClassifier.Classify(ex, AssistantContext(), ct);
                StatusText = ct.IsCancellationRequested
                    ? "Turn cancelled. Completed actions are not rolled back; inspect partial or unknown outcomes before retrying."
                    : "Turn interrupted or timed out. Completed actions are not rolled back; inspect recorded outcomes before retrying.";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError("Assistant turn failed ({Type}): {Detail}", ex.GetType().Name, AiTextSanitizer.Sanitize(ex.Message));
            DispatchTurn(generation, () =>
            {
                Feedback = AiErrorClassifier.Classify(ex, AssistantContext(), ct);
                StatusText = "Turn failed. Completed actions are not rolled back; inspect recorded outcomes before retrying.";
            });
        }
        finally
        {
            // Queue behind progress and completion at the same dispatcher priority.
            // Old finally blocks cannot dispose or clear a new turn's cancellation/approval.
            if (!_dispatcher.TryEnqueue(() =>
            {
                cts.Dispose();
                if (generation != _turnSeq)
                    return;
                _sendCts = null;
                PendingApproval = null;
                IsBusy = false;
            }))
                cts.Dispose();
        }
    }

    /// <summary>
    /// Heading for a collapsed tool-result row. The service prefixes outcomes it never executed
    /// with "Not run" and part-way failures with "Invocation failed", so the heading must not
    /// claim the tool finished in those cases.
    /// </summary>
    private static string ToolResultLabel(string? text) =>
        text?.StartsWith("Not run", StringComparison.Ordinal) == true ? "Tool was not run — show why"
        : text?.StartsWith("Invocation failed", StringComparison.Ordinal) == true ? "Tool failed — show details"
        : "Tool finished — show what it returned";

    private void ApplyProgress(int generation, AiChatProgress progress)
    {
        if (!IsCancellationRequested || progress.Kind is AiChatProgressKind.Completed or AiChatProgressKind.Failed or AiChatProgressKind.Cancelled)
        {
            StatusText = progress.Kind switch
            {
                AiChatProgressKind.Loading => "Loading provider and checking capabilities…",
                AiChatProgressKind.Generating => "Generating a response. Waiting for provider-supplied text…",
                AiChatProgressKind.TextDelta => "Receiving model narration. Tool outcomes are recorded separately.",
                _ => AiTextSanitizer.Sanitize(progress.Text, 1024),
            };
        }

        if (progress.Kind == AiChatProgressKind.TextDelta)
        {
            // Safe sentence/line prose segments arrive incrementally. Providers hold
            // structured or credential-rich content until safe to publish; never display
            // raw transport fragments here. Coalesce deltas into one narration row.
            SetNarration(generation, progress.Text, append: true);
        }
        else if (progress.Kind is AiChatProgressKind.ToolRequested or AiChatProgressKind.AwaitingApproval
            or AiChatProgressKind.ExecutingTool or AiChatProgressKind.ToolResult)
        {
            var label = progress.Kind switch
            {
                AiChatProgressKind.ToolRequested => "Checking a tool request",
                AiChatProgressKind.AwaitingApproval => "Waiting for your approval",
                AiChatProgressKind.ExecutingTool => "Running a tool…",
                // Collapsed rows are read at a glance, so lead with the outcome rather than a
                // category the reader has to open the row to interpret.
                _ => ToolResultLabel(progress.Text),
            };
            var entry = new AssistantTimelineEntry(generation, progress.Kind, progress.ToolCallId, label,
                AiTextSanitizer.Sanitize(progress.Text, MaxEntryCharacters));
            var previous = Messages.FirstOrDefault(item => item.Generation == generation &&
                progress.ToolCallId is not null && item.ToolCallId == progress.ToolCallId);
            if (previous is not null)
                Messages[Messages.IndexOf(previous)] = entry;
            else
                AddEntry(entry);
        }

        if (progress.Kind is AiChatProgressKind.ExecutingTool or AiChatProgressKind.ToolResult
            or AiChatProgressKind.Completed or AiChatProgressKind.Failed or AiChatProgressKind.Cancelled)
            PendingApproval = null;
    }

    private void SetNarration(int generation, string text, bool append)
    {
        var previous = Messages.FirstOrDefault(item => item.Generation == generation &&
            item.Kind == AiChatProgressKind.TextDelta);
        // Once the prose preview fills its budget, do not churn the row on every
        // subsequent segment. The final answer still replaces it (append: false).
        if (append && previous is not null && previous.Text.Length >= MaxEntryCharacters)
            return;
        var narration = append && previous is not null ? previous.Text + text : text;
        var entry = new AssistantTimelineEntry(generation, AiChatProgressKind.TextDelta, null,
            "Assistant · model narration", AiTextSanitizer.Sanitize(narration, MaxEntryCharacters));
        if (previous is not null)
            Messages[Messages.IndexOf(previous)] = entry;
        else if (!string.IsNullOrWhiteSpace(text))
            AddEntry(entry);
    }

    private void AddEntry(AssistantTimelineEntry entry)
    {
        while (Messages.Count >= MaxTimelineEntries)
        {
            Messages.RemoveAt(0);
            HasOmittedActivity = true;
        }
        Messages.Add(entry);
    }
}
