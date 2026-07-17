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
    AssistantToolset tools,
    IAssistantActionGate gate,
    IActivityLog activity,
    ILogger<ContainerAssistantService> logger) : IContainerAssistant
{
    private readonly Dictionary<string, PendingAction> _pending = new(StringComparer.Ordinal);
    private readonly object _pendingGate = new();

    public async Task<AssistantTurnResult> SendAsync(string userMessage, CancellationToken ct = default)
    {
        var result = new AssistantTurnResult();
        if (!settings.AiFeaturesEnabled || settings.AiProvider == AiProviderKind.None)
        {
            result.Messages.Add(AssistantMessage(AssistantMessageRole.Error,
                "Enable AI features and choose a provider in Settings before using the assistant."));
            return result;
        }

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            result.Messages.Add(AssistantMessage(AssistantMessageRole.Assistant, "What would you like to do with your containers?"));
            return result;
        }

        try
        {
            var action = await ResolveIntentAsync(userMessage, ct).ConfigureAwait(false);
            if (action is null)
            {
                result.Messages.Add(AssistantMessage(AssistantMessageRole.Assistant,
                    "I can help list containers/images/volumes/networks, show logs/inspect details, pull images, deploy templates such as WordPress, run a hello-world/nginx container, or stop/remove containers. I cannot access the host OS or run arbitrary shell commands."));
                return result;
            }

            if (gate.RequiresApproval(action.Category))
            {
                var approval = new AssistantApprovalRequest
                {
                    ToolName = action.ToolName,
                    Category = action.Category,
                    Risk = gate.Classify(action.Category),
                    Summary = action.Summary,
                    Details = action.Details,
                };
                lock (_pendingGate)
                {
                    _pending[approval.Id] = action;
                }

                Audit(ActivityKind.AssistantToolInvoked, $"Approval required: {action.ToolName}", action.Details);
                result.Approval = approval;
                result.Messages.Add(AssistantMessage(AssistantMessageRole.Assistant,
                    "This action needs approval. Review the resolved action below before it runs."));
                return result;
            }

            var output = await ExecuteAsync(action, ct).ConfigureAwait(false);
            result.Messages.Add(AssistantMessage(AssistantMessageRole.Assistant, output));
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Messages.Add(AssistantMessage(AssistantMessageRole.Error, "Assistant request canceled."));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Container assistant failed.");
            result.Messages.Add(AssistantMessage(AssistantMessageRole.Error, ex.Message));
            return result;
        }
    }

    public async Task<AssistantTurnResult> ApproveAsync(AssistantApprovalRequest approval, CancellationToken ct = default)
    {
        PendingAction? action;
        lock (_pendingGate)
        {
            _pending.Remove(approval.Id, out action);
        }

        if (action is null)
        {
            return OneMessage(AssistantMessageRole.Error, "That approval request is no longer available.");
        }

        Audit(ActivityKind.AssistantApprovalApproved, $"Approved: {action.ToolName}", action.Details);
        try
        {
            return OneMessage(AssistantMessageRole.Assistant, await ExecuteAsync(action, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Approved assistant action failed.");
            return OneMessage(AssistantMessageRole.Error, ex.Message);
        }
    }

    public Task<AssistantTurnResult> RejectAsync(AssistantApprovalRequest approval, CancellationToken ct = default)
    {
        PendingAction? action;
        lock (_pendingGate)
        {
            _pending.Remove(approval.Id, out action);
        }

        Audit(ActivityKind.AssistantApprovalRejected, $"Rejected: {approval.ToolName}", approval.Details);
        return Task.FromResult(OneMessage(AssistantMessageRole.Assistant,
            action is null ? "Rejected. The action was not run." : $"Rejected '{action.ToolName}'. The action was not run."));
    }

    private async Task<PendingAction?> ResolveIntentAsync(string message, CancellationToken ct)
    {
        var text = message.Trim();
        var lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "what's running", "whats running", "list containers", "show containers", "what is running"))
        {
            return ReadOnly("list_containers", "List all containers", "", tools.ListContainersAsync);
        }

        if (ContainsAny(lower, "list images", "show images"))
        {
            return ReadOnly("list_images", "List images", "", tools.ListImagesAsync);
        }

        if (ContainsAny(lower, "list volumes", "show volumes"))
        {
            return ReadOnly("list_volumes", "List volumes", "", tools.ListVolumesAsync);
        }

        if (ContainsAny(lower, "list networks", "show networks"))
        {
            return ReadOnly("list_networks", "List networks", "", tools.ListNetworksAsync);
        }

        if (ContainsAny(lower, "engine status", "engine health"))
        {
            return ReadOnly("engine_status", "Check engine status", "", tools.EngineStatusAsync);
        }

        if (ContainsAny(lower, "compose projects", "list compose"))
        {
            return new PendingAction("list_compose_projects", AssistantPermissionCategory.ReadOnly, "List compose projects", "",
                _ => Task.FromResult(tools.ListComposeProjects()));
        }

        if (lower.StartsWith("logs ", StringComparison.Ordinal) || lower.Contains("show logs for ", StringComparison.Ordinal))
        {
            var id = ExtractAfter(text, "show logs for ") ?? ExtractAfter(text, "logs ") ?? string.Empty;
            return ReadOnly("get_container_logs", $"Show logs for {id}", $"Container: {id}\nTail: 200",
                token => tools.GetContainerLogsAsync(id, 200, token));
        }

        if (lower.StartsWith("inspect ", StringComparison.Ordinal) || lower.Contains("inspect container ", StringComparison.Ordinal))
        {
            var id = ExtractAfter(text, "inspect container ") ?? ExtractAfter(text, "inspect ") ?? string.Empty;
            return ReadOnly("inspect_container", $"Inspect {id}", $"Container: {id}", token => tools.InspectContainerAsync(id, token));
        }

        if (lower.Contains("wordpress", StringComparison.Ordinal))
        {
            return StateChanging("deploy_template", AssistantPermissionCategory.ComposeTemplate,
                "Deploy the WordPress + MySQL template", "Template: wordpress", token => tools.DeployTemplateAsync("wordpress", token));
        }

        if (ContainsAny(lower, "deploy hello world", "run hello world", "hello-world"))
        {
            var options = lower.Contains("nginx", StringComparison.Ordinal)
                ? AssistantToolset.CreateNginxHelloWorldOptions()
                : AssistantToolset.CreateHelloWorldOptions();
            return StateChanging("run_container", AssistantPermissionCategory.CreateRun,
                $"Run container {options.Name}", DescribeRun(options), token => tools.RunContainerAsync(options, token));
        }

        if (ContainsAny(lower, "deploy nginx", "run nginx"))
        {
            var options = AssistantToolset.CreateNginxHelloWorldOptions();
            return StateChanging("run_container", AssistantPermissionCategory.CreateRun,
                "Run nginx on localhost:8080", DescribeRun(options), token => tools.RunContainerAsync(options, token));
        }

        if (lower.StartsWith("pull ", StringComparison.Ordinal))
        {
            var image = ExtractAfter(text, "pull ") ?? string.Empty;
            return StateChanging("pull_image", AssistantPermissionCategory.CreateRun,
                $"Pull image {image}", $"Image: {image}", token => tools.PullImageAsync(image, token));
        }

        if (ContainsAny(lower, "stop all running containers", "stop all containers"))
        {
            var preview = await tools.StopAllContainersAsyncPreview(ct).ConfigureAwait(false);
            return StateChanging("stop_all_containers", AssistantPermissionCategory.Lifecycle,
                "Stop all running containers", "Targets:\n" + string.Join(Environment.NewLine, preview),
                async token => (await tools.StopAllContainersAsync(token).ConfigureAwait(false)).Result);
        }

        if (ContainsAny(lower, "remove all running containers", "delete all running containers"))
        {
            var preview = await tools.RemoveAllContainersAsyncPreview(onlyRunning: true, ct).ConfigureAwait(false);
            return StateChanging("remove_all_containers", AssistantPermissionCategory.Destructive,
                "Remove all running containers", "Targets:\n" + string.Join(Environment.NewLine, preview),
                async token => (await tools.RemoveAllContainersAsync(onlyRunning: true, token).ConfigureAwait(false)).Result);
        }

        if (ContainsAny(lower, "remove all containers", "delete all containers"))
        {
            var preview = await tools.RemoveAllContainersAsyncPreview(onlyRunning: false, ct).ConfigureAwait(false);
            return StateChanging("remove_all_containers", AssistantPermissionCategory.Destructive,
                "Remove all containers", "Targets:\n" + string.Join(Environment.NewLine, preview),
                async token => (await tools.RemoveAllContainersAsync(onlyRunning: false, token).ConfigureAwait(false)).Result);
        }

        return null;
    }

    private async Task<string> ExecuteAsync(PendingAction action, CancellationToken ct)
    {
        Audit(ActivityKind.AssistantToolInvoked, $"Invoked: {action.ToolName}", action.Details);
        var output = await action.Execute(ct).ConfigureAwait(false);
        return $"Tool `{action.ToolName}` completed.\n\n{output}";
    }

    private void Audit(ActivityKind kind, string title, string detail) =>
        activity.Record(new ActivityEvent
        {
            Category = ActivityCategory.Assistant,
            Kind = kind,
            Title = title,
            Detail = detail,
        });

    private static PendingAction ReadOnly(string tool, string summary, string details, Func<CancellationToken, Task<string>> execute) =>
        new(tool, AssistantPermissionCategory.ReadOnly, summary, details, execute);

    private static PendingAction StateChanging(
        string tool,
        AssistantPermissionCategory category,
        string summary,
        string details,
        Func<CancellationToken, Task<string>> execute) =>
        new(tool, category, summary, details, execute);

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(value.Contains);

    private static string? ExtractAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : text[(index + marker.Length)..].Trim().Trim('.', '"', '\'');
    }

    private static string DescribeRun(RunContainerOptions options) =>
        $"Image: {options.Image}\nName: {options.Name}\nPorts: {string.Join(", ", options.PortMappings)}\nVolumes: {string.Join(", ", options.Volumes)}";

    private static AssistantChatMessage AssistantMessage(AssistantMessageRole role, string text) => new()
    {
        Role = role,
        Text = text,
    };

    private static AssistantTurnResult OneMessage(AssistantMessageRole role, string text) => new()
    {
        Messages = { AssistantMessage(role, text) },
    };

    private sealed record PendingAction(
        string ToolName,
        AssistantPermissionCategory Category,
        string Summary,
        string Details,
        Func<CancellationToken, Task<string>> Execute);
}
