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
    private readonly List<AiChatMessage> _history = new() { new AiChatMessage { Role = "system", Content = SystemPrompt } };
    private readonly Dictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();

    public event EventHandler<AssistantApprovalRequest?>? ApprovalChanged;

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
            var provider = providers.FirstOrDefault(p => p.Kind == settings.AiProvider)
                ?? throw new InvalidOperationException($"AI provider '{settings.AiProvider}' is not registered for assistant chat.");
            var definitions = await tools.GetDefinitionsAsync(ct).ConfigureAwait(false);
            IReadOnlyList<AiChatMessage> snapshot;
            lock (_stateGate)
            {
                _history.Add(new AiChatMessage { Role = "user", Content = userMessage.Trim() });
                snapshot = _history.ToList();
            }

            var text = await provider.RunTurnAsync(snapshot, definitions, InvokeToolAsync, ct).ConfigureAwait(false);
            var finalText = string.IsNullOrWhiteSpace(text) ? "Done." : text.Trim();
            lock (_stateGate)
            {
                _history.Add(new AiChatMessage { Role = "assistant", Content = finalText });
            }

            return OneMessage(AssistantMessageRole.Assistant, finalText);
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
        finally
        {
            ApprovalChanged?.Invoke(this, null);
        }
    }

    public Task<AssistantTurnResult> ApproveAsync(AssistantApprovalRequest approval, CancellationToken ct = default)
    {
        PendingApproval? pending;
        lock (_stateGate)
        {
            _pending.Remove(approval.Id, out pending);
        }

        pending?.Decision.TrySetResult(true);
        return Task.FromResult(new AssistantTurnResult());
    }

    public Task<AssistantTurnResult> RejectAsync(AssistantApprovalRequest approval, CancellationToken ct = default)
    {
        PendingApproval? pending;
        lock (_stateGate)
        {
            _pending.Remove(approval.Id, out pending);
        }

        pending?.Decision.TrySetResult(false);
        return Task.FromResult(new AssistantTurnResult());
    }

    public void Reset()
    {
        lock (_stateGate)
        {
            foreach (var pending in _pending.Values)
            {
                pending.Decision.TrySetCanceled();
            }

            _pending.Clear();
            _history.Clear();
            _history.Add(new AiChatMessage { Role = "system", Content = SystemPrompt });
        }

        ApprovalChanged?.Invoke(this, null);
    }

    private async Task<string> InvokeToolAsync(AiToolCall call, CancellationToken ct)
    {
        var resolved = await tools.ResolveAsync(call, ct).ConfigureAwait(false);
        if (!gate.RequiresApproval(resolved.Call.Name, resolved.Category))
        {
            return await ExecuteToolAsync(resolved, ct).ConfigureAwait(false);
        }

        var approval = new AssistantApprovalRequest
        {
            ToolName = call.Name,
            Category = resolved.Category,
            Risk = gate.Classify(resolved.Category),
            Summary = resolved.Summary,
            Details = resolved.Details,
        };
        var pending = new PendingApproval(resolved, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_stateGate)
        {
            _pending[approval.Id] = pending;
        }

        Audit(ActivityKind.AssistantToolInvoked, $"Approval required: {call.Name}", resolved.Details);
        ApprovalChanged?.Invoke(this, approval);
        try
        {
            await using var registration = ct.Register(() => pending.Decision.TrySetCanceled(ct));
            var approved = await pending.Decision.Task.ConfigureAwait(false);
            ApprovalChanged?.Invoke(this, null);
            if (!approved)
            {
                Audit(ActivityKind.AssistantApprovalRejected, $"Rejected: {call.Name}", resolved.Details);
                return "The user rejected this action. Do not perform it; explain that it was not run.";
            }

            Audit(ActivityKind.AssistantApprovalApproved, $"Approved: {call.Name}", resolved.Details);
            return await ExecuteToolAsync(resolved, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
            {
                _pending.Remove(approval.Id);
            }
        }
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

    private static AssistantChatMessage AssistantMessage(AssistantMessageRole role, string text) => new()
    {
        Role = role,
        Text = text,
    };

    private static AssistantTurnResult OneMessage(AssistantMessageRole role, string text) => new()
    {
        Messages = { AssistantMessage(role, text) },
    };

    private sealed record PendingApproval(
        AssistantResolvedToolCall Tool,
        TaskCompletionSource<bool> Decision);

    private const string SystemPrompt = """
        You are the Container AI Assistant for WSL Container Desktop.
        Scope: manage WSL containers, images, volumes, networks, compose projects/templates, and k3s only when k3s tools are provided.
        Use only the declared tools for live data or actions. Refuse unrelated requests.
        Never claim you can access the host OS, host filesystem, credentials, secrets, arbitrary network tools, or arbitrary shell commands.
        Do not ask the user to run commands when an allowlisted tool can do the work.
        For WordPress/blog/database requests, prefer the WordPress compose template when available.
        For ANY app that needs more than one container (e.g. app + database, app + cache, front end + API), deploy it with deploy_compose (or deploy_template), never as separate run_container calls: only compose gives the services a shared network so they resolve each other by service name over DNS. When writing compose YAML, reference other services by their service name as the host (e.g. WordPress WORDPRESS_DB_HOST=db).
        Use run_container only for a single standalone container.
        To answer questions about which image versions/tags exist in a configured remote registry, or what the newest tag is, use list_registry_repositories and list_registry_tags; do not guess tags. These browse configured ACR or private Docker Registry v2 hosts (Docker Hub's global catalog is not browsable).
        For bulk operations, call the bulk tool; the app will resolve the concrete target list and approval.
        When the user targets a subset of containers by name (e.g. "starting with wordpress_", "the nginx ones"), set the bulk tool's namePrefix or nameContains filter accordingly. Only omit both filters when the user clearly means every container.
        Explain results concisely after tool calls complete.
        """;
}
