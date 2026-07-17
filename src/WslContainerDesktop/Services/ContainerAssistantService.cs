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

using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class ContainerAssistantService(
    ISettingsService settings,
    IEnumerable<IAiChatProvider> providers,
    AssistantToolset tools,
    IAssistantActionGate gate,
    IActivityLog activity,
    ILogger<ContainerAssistantService> logger) : IContainerAssistant
{
    private const int MaxToolIterations = 8;
    private readonly List<AiChatMessage> _history = new() { new AiChatMessage { Role = "system", Content = SystemPrompt } };
    private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();

    public async Task<AssistantTurnResult> SendAsync(string userMessage, CancellationToken ct = default)
    {
        if (!settings.AiFeaturesEnabled || settings.AiProvider == AiProviderKind.None)
        {
            return OneMessage(AssistantMessageRole.Error,
                "Enable AI features and choose a tool-capable provider in Settings before using the assistant.");
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return OneMessage(AssistantMessageRole.Assistant, "What would you like to do with your containers?");
        }

        try
        {
            lock (_stateGate)
            {
                _history.Add(new AiChatMessage { Role = "user", Content = userMessage.Trim() });
            }

            return await ContinueLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OneMessage(AssistantMessageRole.Error, "Assistant request canceled.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Container assistant failed.");
            return OneMessage(AssistantMessageRole.Error, ex.Message);
        }
    }

    public async Task<AssistantTurnResult> ApproveAsync(AssistantApprovalRequest approval, CancellationToken ct = default)
    {
        PendingApproval? pending;
        lock (_stateGate)
        {
            _pending.Remove(approval.Id, out pending);
        }

        if (pending is null)
        {
            return OneMessage(AssistantMessageRole.Error, "That approval request is no longer available.");
        }

        Audit(ActivityKind.AssistantApprovalApproved, $"Approved: {pending.Tool.Call.Name}", pending.Tool.Details);
        try
        {
            var output = await ExecuteToolAsync(pending.Tool, ct).ConfigureAwait(false);
            lock (_stateGate)
            {
                _history.Add(ToolResult(pending.Tool.Call, output));
            }

            return await ContinueLoopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Approved assistant action failed.");
            return OneMessage(AssistantMessageRole.Error, ex.Message);
        }
    }

    public async Task<AssistantTurnResult> RejectAsync(AssistantApprovalRequest approval, CancellationToken ct = default)
    {
        PendingApproval? pending;
        lock (_stateGate)
        {
            _pending.Remove(approval.Id, out pending);
        }

        Audit(ActivityKind.AssistantApprovalRejected, $"Rejected: {approval.ToolName}", approval.Details);
        if (pending is not null)
        {
            lock (_stateGate)
            {
                _history.Add(ToolResult(pending.Tool.Call, "The user rejected this tool call. Do not perform the action; explain that it was not run."));
            }

            return await ContinueLoopAsync(ct).ConfigureAwait(false);
        }

        return OneMessage(AssistantMessageRole.Assistant, "Rejected. The action was not run.");
    }

    private async Task<AssistantTurnResult> ContinueLoopAsync(CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Kind == settings.AiProvider)
            ?? throw new InvalidOperationException($"AI provider '{settings.AiProvider}' is not registered for assistant chat.");
        var definitions = await tools.GetDefinitionsAsync(ct).ConfigureAwait(false);
        var result = new AssistantTurnResult();

        for (var i = 0; i < MaxToolIterations; i++)
        {
            IReadOnlyList<AiChatMessage> snapshot;
            lock (_stateGate)
            {
                snapshot = _history.ToList();
            }

            var turn = await provider.ChatAsync(snapshot, definitions, ct).ConfigureAwait(false);
            if (turn.ToolCalls.Count == 0)
            {
                var text = string.IsNullOrWhiteSpace(turn.AssistantText)
                    ? "Done."
                    : turn.AssistantText.Trim();
                lock (_stateGate)
                {
                    _history.Add(new AiChatMessage { Role = "assistant", Content = text });
                }

                result.Messages.Add(AssistantMessage(AssistantMessageRole.Assistant, text));
                return result;
            }

            lock (_stateGate)
            {
                _history.Add(new AiChatMessage
                {
                    Role = "assistant",
                    Content = turn.AssistantText,
                    ToolCalls = turn.ToolCalls,
                });
            }

            foreach (var call in turn.ToolCalls)
            {
                var resolved = await tools.ResolveAsync(call, ct).ConfigureAwait(false);
                if (gate.RequiresApproval(resolved.Category))
                {
                    var approval = new AssistantApprovalRequest
                    {
                        ToolName = call.Name,
                        Category = resolved.Category,
                        Risk = gate.Classify(resolved.Category),
                        Summary = resolved.Summary,
                        Details = resolved.Details,
                    };
                    lock (_stateGate)
                    {
                        _pending[approval.Id] = new PendingApproval(resolved);
                    }

                    Audit(ActivityKind.AssistantToolInvoked, $"Approval required: {call.Name}", resolved.Details);
                    result.Approval = approval;
                    result.Messages.Add(AssistantMessage(AssistantMessageRole.Assistant,
                        "This tool call needs approval. Review the resolved action below before it runs."));
                    return result;
                }

                var output = await ExecuteToolAsync(resolved, ct).ConfigureAwait(false);
                lock (_stateGate)
                {
                    _history.Add(ToolResult(call, output));
                }
            }
        }

        result.Messages.Add(AssistantMessage(AssistantMessageRole.Error, "Stopped because the assistant reached the tool-iteration limit."));
        return result;
    }

    private async Task<string> ExecuteToolAsync(AssistantResolvedToolCall tool, CancellationToken ct)
    {
        Audit(ActivityKind.AssistantToolInvoked, $"Invoked: {tool.Call.Name}", tool.Details);
        var output = await tool.ExecuteAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output) ? "Succeeded." : output;
    }

    private void Audit(ActivityKind kind, string title, string detail) =>
        activity.Record(new ActivityEvent
        {
            Category = ActivityCategory.Assistant,
            Kind = kind,
            Title = title,
            Detail = detail,
        });

    private static AiChatMessage ToolResult(AiToolCall call, string output) => new()
    {
        Role = "tool",
        ToolCallId = call.Id,
        ToolName = call.Name,
        Content = output,
    };

    private static AssistantChatMessage AssistantMessage(AssistantMessageRole role, string text) => new()
    {
        Role = role,
        Text = text,
    };

    private static AssistantTurnResult OneMessage(AssistantMessageRole role, string text) => new()
    {
        Messages = { AssistantMessage(role, text) },
    };

    private sealed record PendingApproval(AssistantResolvedToolCall Tool);

    private const string SystemPrompt = """
        You are the Container AI Assistant for WSL Container Desktop.
        Scope: manage WSL containers, images, volumes, networks, compose projects/templates, and k3s only when k3s tools are provided.
        Use only the declared tools for live data or actions. Refuse unrelated requests.
        Never claim you can access the host OS, host filesystem, credentials, secrets, arbitrary network tools, or arbitrary shell commands.
        Do not ask the user to run commands when an allowlisted tool can do the work.
        For WordPress/blog/database requests, prefer the WordPress compose template when available.
        For bulk operations, call the bulk tool; the app will resolve the concrete target list and approval.
        Explain results concisely after tool calls complete.
        """;
}
