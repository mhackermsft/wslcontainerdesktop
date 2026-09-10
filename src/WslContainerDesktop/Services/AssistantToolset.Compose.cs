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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed partial class AssistantToolset
{
    private async Task<AssistantResolvedToolCall> ResolveComposeLifecycleAsync(
        AiToolCall call, string projectName, CancellationToken ct)
    {
        var matches = composeStore.GetAll().Where(p =>
            string.Equals(p.Name, projectName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            return ComposeBlocked(call, "Saved Compose project is missing or ambiguous. Select one exact name from list_compose_projects.");
        var project = matches[0];
        var snapshot = JsonSerializer.Serialize(project);
        var operation = call.Name switch
        {
            "start_compose_project" => ComposeLifecycleOperation.Up,
            "stop_compose_project" => ComposeLifecycleOperation.Stop,
            "restart_compose_project" => ComposeLifecycleOperation.Restart,
            "down_compose_project" => ComposeLifecycleOperation.Down,
            _ => throw new InvalidOperationException("Unsupported project operation."),
        };
        var review = await composeSupervisor.PrepareReviewAsync(project,
            new ComposeOperationRequest { Operation = operation }, ct).ConfigureAwait(false);
        return await BindComposeReviewAsync(call, review, () =>
        {
            var current = composeStore.GetAll().Where(p =>
                string.Equals(p.Name, projectName, StringComparison.Ordinal)).ToArray();
            return current.Length == 1 && JsonSerializer.Serialize(current[0]) == snapshot;
        }, ct).ConfigureAwait(false);
    }

    private Task<AssistantResolvedToolCall> ResolveDeployComposeAsync(
        AiToolCall call, JsonElement args, CancellationToken ct) =>
        PrepareComposeAsync(call, StringArg(args, "yaml"), OptionalStringArg(args, "projectName"), null, ct);

    private Task<AssistantResolvedToolCall> ResolveDeployTemplateAsync(
        AiToolCall call, string key, CancellationToken ct)
    {
        var template = templates.Templates.FirstOrDefault(t =>
            string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return Task.FromResult(ComposeBlocked(call, "Template was not found. Select an existing template."));

        // Capture every template field locally, never re-read mutable run options after approval.
        var snapshot = JsonSerializer.Serialize(template);
        bool Unchanged() => templates.Templates.Any(t =>
            t.Id == template.Id && JsonSerializer.Serialize(t) == snapshot);
        if (template.Kind == StackTemplateKind.Compose)
            return PrepareComposeAsync(call, template.ComposeYaml ?? "",
                string.IsNullOrWhiteSpace(template.ComposeProjectName) ? template.Id : template.ComposeProjectName,
                Unchanged, ct);

        if (template.RunOptions is null)
            return Task.FromResult(ComposeBlocked(call, "Template has no run options."));
        var options = template.RunOptions.Clone();
        ValidateRunOptions(options);
        return Task.FromResult(Resolved(call, AssistantPermissionCategory.ComposeTemplate,
            $"Deploy template {template.Name}", JsonSerializer.Serialize(options, JsonOptions),
            token => Unchanged() ? RunContainerAsync(options, token) :
                Task.FromResult(ComposeResult(new()
                {
                    Kind = ComposeReviewOutcomeKind.Stale,
                    Message = "Template changed. Nothing applied; select and review it again.",
                }))));
    }

    private async Task<AssistantResolvedToolCall> PrepareComposeAsync(AiToolCall call, string yaml,
        string? projectName, Func<bool>? sourceUnchanged, CancellationToken ct)
    {
        ComposeProject project;
        try
        {
            project = ParseComposeInput(yaml, projectName);
        }
        catch (Exception ex) when (IsComposeInputFailure(ex))
        {
            // Parser diagnostics may quote arbitrary YAML values, even with innocuous keys.
            return ComposeBlocked(call, "Compose input could not be resolved. Nothing saved or applied; correct the YAML and review again. Technical values withheld.");
        }
        if (project.Services.Count == 0)
            throw new InvalidOperationException("Invalid tool arguments: compose YAML defines no services.");

        var sourceSnapshot = JsonSerializer.Serialize(project);
        var review = await composeSupervisor.PrepareReviewAsync(project, ct: ct).ConfigureAwait(false);
        return await BindComposeReviewAsync(call, review, () =>
            (sourceUnchanged is null || sourceUnchanged()) &&
            JsonSerializer.Serialize(ParseComposeInput(yaml, projectName)) == sourceSnapshot, ct).ConfigureAwait(false);
    }

    private async Task<AssistantResolvedToolCall> BindComposeReviewAsync(
        AiToolCall call, ComposeReviewToken review, Func<bool> sourceUnchanged, CancellationToken ct)
    {
        var preview = review.Preview;
        var details = AiTextSanitizer.Redact($"{preview.Summary}\nExpires: {review.ExpiresAt:O}\n" +
            string.Join("\n\n", preview.Settings.Select(row => $"{row.Summary}\n{row.Detail}")));
        if (details.Length > AiTextSanitizer.EvidenceLimit)
        {
            await composeSupervisor.ApplyReviewedAsync(review, false).ConfigureAwait(false);
            return ComposeBlocked(call, "The complete consequences exceed the assistant approval display budget. Nothing saved or applied. Use the Compose page for a full review; a truncated preview cannot authorize deployment.");
        }
        if (!preview.CanApply)
        {
            var blocked = await composeSupervisor.ApplyReviewedAsync(review, true, ct: ct).ConfigureAwait(false);
            return ComposeBlocked(call, details, ComposeResult(blocked, preview));
        }

        return Resolved(call, AssistantPermissionCategory.ComposeTemplate,
            $"Review Compose project '{preview.Project}'", details, async token =>
            {
                if (token.IsCancellationRequested)
                    return ComposeResult(await composeSupervisor.ApplyReviewedAsync(review, true, ct: token).ConfigureAwait(false));
                ComposeReviewOutcomeKind? sourceRefusal = null;
                try
                {
                    if (!sourceUnchanged())
                        sourceRefusal = ComposeReviewOutcomeKind.Stale;
                }
                catch (Exception ex) when (IsComposeInputFailure(ex))
                {
                    sourceRefusal = ComposeReviewOutcomeKind.Blocked;
                }
                if (sourceRefusal is { } kind)
                {
                    var declined = await composeSupervisor.ApplyReviewedAsync(review, false).ConfigureAwait(false);
                    if (declined.Kind != ComposeReviewOutcomeKind.Cancelled || token.IsCancellationRequested)
                        return ComposeResult(declined);
                    return ComposeResult(new()
                    {
                        Kind = kind,
                        Message = "Compose source, saved project, interpolation or template changed or became unavailable. Nothing applied; resolve the input and review again. Technical values withheld.",
                    });
                }
                var outcome = await composeSupervisor.ApplyReviewedAsync(review, true, ct: token).ConfigureAwait(false);
                return ComposeResult(outcome);
            }) with
        {
            RequiresExplicitApproval = true,
            DeclineAsync = async () => ComposeResult(
                await composeSupervisor.ApplyReviewedAsync(review, false).ConfigureAwait(false)),
        };
    }

    private static ComposeProject ParseComposeInput(string yaml, string? projectName)
    {
        var project = ComposeImporter.ParseProject(RequireValue(yaml, "compose YAML"));
        project.Name = ResolveComposeProjectName(projectName, project.Name);
        project.ApplyProjectNamespacing();
        return project;
    }

    private static bool IsComposeInputFailure(Exception ex) =>
        ex is ComposeConfigurationException or YamlDotNet.Core.YamlException or
            InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException;

    private static AssistantResolvedToolCall ComposeBlocked(AiToolCall call, string details, string? result = null)
    {
        result ??= ComposeResult(new() { Kind = ComposeReviewOutcomeKind.Blocked, Message = details });
        return Resolved(call, AssistantPermissionCategory.ComposeTemplate, "Compose deployment blocked",
            details, _ => Task.FromResult(result)) with { BlockedResult = result };
    }

    private static string ComposeResult(ComposeReviewOutcome outcome, ComposeCompatibilityPreview? preview = null) =>
        AiTextSanitizer.Sanitize(JsonSerializer.Serialize(new
        {
            status = outcome.Kind switch
            {
                ComposeReviewOutcomeKind.Applied => "succeeded",
                ComposeReviewOutcomeKind.PartialFailure => "partial",
                ComposeReviewOutcomeKind.Cancelled => "cancelled",
                _ => "failed",
            },
            kind = outcome.Kind.ToString(),
            allSucceeded = outcome.AllSucceeded,
            message = outcome.Message,
            blockers = preview?.Settings.Where(row => row.Disposition == ComposeSettingDisposition.Blocked)
                .Select(row => new { row.Service, row.Setting, row.EffectiveValue, row.Explanation }),
            outcomes = outcome.Services.Select(service => new
            {
                instance = service.InstanceKey,
                service.Service,
                service.InstanceIndex,
                action = service.Action.ToString(),
                status = service.Outcome?.ToString().ToLowerInvariant() ?? "failed",
                detail = service.Detail,
                warning = service.Warning,
            }),
            retainedResources = outcome.RetainedResources,
            retentionNotice = outcome.Kind is ComposeReviewOutcomeKind.Applied or ComposeReviewOutcomeKind.PartialFailure ||
                outcome.Kind == ComposeReviewOutcomeKind.Cancelled && (outcome.Services.Count > 0 || outcome.RetainedResources.Count > 0)
                ? "Mounted volumes are retained. On partial execution, created networks, volumes or containers may remain; refresh actual state before cleanup. No automatic destructive retry or project-wide rollback."
                : "No new resource mutation was authorized.",
        }));
}
