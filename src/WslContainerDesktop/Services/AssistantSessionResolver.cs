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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Resolves assistant operations against a chosen or already-owned session.</summary>
public sealed class AssistantSessionResolver : IAssistantSessionResolver
{
    private readonly IWslcSessionSelectionService _selector;
    private readonly WslcSessionInventoryReader _reader;
    private readonly IComposeProjectStore _projects;
    private readonly ISettingsService _settings;
    private readonly WslcSessionContext _management;
    private readonly ComposeProjectSupervisor _supervisor;
    private readonly ILogger<AssistantSessionResolver> _logger;
    private readonly Func<CancellationToken, Task<WslcSessionSelection?>> _choose;
    private readonly Func<WslcSessionSelection, CancellationToken, Task<ComposeSessionScope>> _composeScope;

#if !WSLCD_TEST_NO_DISPATCHER
    /// <summary>Creates a resolver that dispatches session dialogs and Compose scopes to the UI thread.</summary>
    public AssistantSessionResolver(IWslcSessionSelectionService selector, WslcSessionInventoryReader reader,
        IComposeProjectStore projects, ISettingsService settings, WslcSessionContext management,
        ComposeProjectSupervisor supervisor, SessionAutomationCoordinator automation, DialogService dialogs,
        ILogger<AssistantSessionResolver> logger)
        : this(selector, reader, projects, settings, management, supervisor, logger,
            ct => OnUiAsync(dialogs, () => selector.ChooseActiveAsync(ct)),
            (selection, ct) => OnUiAsync(dialogs, () => automation.GetComposeScopeAsync(selection, ct)))
    {
    }

    private static Task<T> OnUiAsync<T>(DialogService dialogs, Func<Task<T>> action)
    {
        var dispatcher = dialogs.Dispatcher ?? throw new InvalidOperationException(UiText.Get("Assistant_SessionUiUnavailable",
            "The session chooser is unavailable. Open the app and try again."));
        if (dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            throw new InvalidOperationException(UiText.Get("Assistant_SessionUiUnavailable",
                "The session chooser is unavailable. Open the app and try again."));
        return completion.Task;
    }
#endif

    internal AssistantSessionResolver(IWslcSessionSelectionService selector, WslcSessionInventoryReader reader,
        IComposeProjectStore projects, ISettingsService settings, WslcSessionContext management,
        ComposeProjectSupervisor supervisor, ILogger<AssistantSessionResolver> logger,
        Func<CancellationToken, Task<WslcSessionSelection?>> choose,
        Func<WslcSessionSelection, CancellationToken, Task<ComposeSessionScope>> composeScope)
    {
        _selector = selector;
        _reader = reader;
        _projects = projects;
        _settings = settings;
        _management = management;
        _supervisor = supervisor;
        _logger = logger;
        _choose = choose;
        _composeScope = composeScope;
    }

    /// <inheritdoc/>
    public async Task<AssistantResolvedToolCall> ResolveAsync(AssistantToolset tools, AiToolCall call, CancellationToken ct)
    {
        if (!_settings.AiAssistantAllowDestructive && IsDestructive(call.Name))
            return new(call, AssistantPermissionCategory.Destructive, call.Name, "", _ => Task.FromResult(""))
            {
                BlockedResult = UiText.Get("Assistant_DestructiveDisabled",
                    "Destructive assistant actions are turned off. Nothing was changed."),
            };

        var project = IsProjectLifecycle(call.Name) ? FindProject(call) : null;
        if (IsProjectLifecycle(call.Name) && project is null)
            return Blocked(call, UiText.Get("Assistant_ProjectUnavailable",
                "The saved Compose project is missing or ambiguous. Nothing was changed."));
        if (project?.SessionOwner?.IsValid != true && IsProjectLifecycle(call.Name) &&
            call.Name != "start_compose_project")
            return Blocked(call, UiText.Get("Automation_ProjectNeedsSession",
                "This saved Compose project needs a session assignment before it can be managed."));

        var destination = project?.SessionOwner?.IsValid == true
            ? await FindOwnerAsync(project.SessionOwner, ct)
            : await _choose(ct);
        if (destination is null)
            return Blocked(call, UiText.Get("Assistant_SessionChoiceCancelled",
                "Session choice cancelled. No action was started; do not retry automatically."));
        if (destination.Generation is null)
            return Blocked(call, UiText.Get("Assistant_NoActiveSession",
                "The chosen session is not running. Start it in WSL engine, then request the action again."));
        if (_management.IsDefaultSessionStopped(destination.ExecutablePath, destination.Target))
            return Blocked(call, UiText.Get("ManagedSession_DefaultStartRequired",
                "The default CLI session was stopped in this app. Open WSL engine and select Start for that session."));

        await _selector.VerifyAsync(destination, ct);
        ComposeSessionScope? scope = null;
        try
        {
            if (NeedsComposeScope(call.Name))
                scope = await _composeScope(destination, ct);
            var context = scope?.Context ?? new WslcSessionContext();
            if (scope is null) context.Select(destination.Target.Name);
            var managementSnapshot = _management.CaptureSnapshot();
            var managementHealth = destination.Target == managementSnapshot.Target;
            var scopedTools = tools.ForSession(scope?.Client ?? _selector.CreateClient(destination), context,
                scope?.Supervisor ?? _supervisor, managementHealth, destination);
            var resolved = await scopedTools.ResolveAsync(call, ct);
            await _selector.VerifyAsync(destination, ct);
            var retainedScope = scope;
            scope = null;
            var retired = 0;
            return resolved with
            {
                ExecuteAsync = async token =>
                {
                    if (retainedScope?.Lifetime.IsCancellationRequested == true)
                        return SessionChanged();
                    try { await _selector.VerifyAsync(destination, token); }
                    catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex) && ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "The assistant's chosen session changed before execution.");
                        return SessionChanged();
                    }
                    using var healthLease = call.Name == "get_health_observations" && managementHealth
                        ? _management.TryLockSelection(managementSnapshot) : null;
                    if (call.Name == "get_health_observations" && managementHealth && healthLease is null)
                        return SessionChanged();
                    using var linked = retainedScope is null ? null :
                        CancellationTokenSource.CreateLinkedTokenSource(token, retainedScope.Lifetime);
                    return await resolved.ExecuteAsync(linked?.Token ?? token);
                },
                DeclineAsync = retainedScope is null && resolved.DeclineAsync is null ? null : async () =>
                {
                    if (Interlocked.Exchange(ref retired, 1) != 0)
                        return UiText.Get("Assistant_SessionChoiceCancelled",
                            "Session choice cancelled. No action was started; do not retry automatically.");
                    try
                    {
                        return resolved.DeclineAsync is null
                            ? UiText.Get("Assistant_SessionChoiceCancelled",
                                "Session choice cancelled. No action was started; do not retry automatically.")
                            : await resolved.DeclineAsync();
                    }
                    finally { retainedScope?.Dispose(); }
                },
            };
        }
        finally { scope?.Dispose(); }
    }

    private ComposeProject? FindProject(AiToolCall call)
    {
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        var name = args.RootElement.GetProperty("projectName").GetString();
        var matches = _projects.GetAll().Where(project => string.Equals(project.Name, name, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task<WslcSessionSelection> FindOwnerAsync(AutomationSessionOwner owner, CancellationToken ct)
    {
        if (!string.Equals(owner.ExecutablePath, _settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("Automation_ProjectDifferentSession",
                "This Compose project belongs to a different session or engine. Select its assigned session before continuing."));
        var name = owner.SessionName ?? WslcSessionActionGuard.ResolveDefaultSessionName();
        var generation = (await _reader.DiscoverAsync(ct)).SingleOrDefault(session =>
            string.Equals(session.Name, name, StringComparison.Ordinal));
        if (generation is null)
            throw new InvalidOperationException(UiText.Get("Automation_SessionUnavailable",
                "That session is no longer available. Refresh the session list and try again."));
        return new(new(owner.SessionName), owner.ExecutablePath, generation,
            owner.SessionName ?? UiText.Get("WslEngine_Session_Default", "Default CLI session"));
    }

    private static AssistantResolvedToolCall Blocked(AiToolCall call, string message) =>
        new(call, AssistantPermissionCategory.ReadOnly, message, "", _ => Task.FromResult(message)) { BlockedResult = message };

    private static string SessionChanged() => UiText.Get("Assistant_SessionChanged",
        "The session changed after approval; no action was started. Review again.");

    private static bool IsProjectLifecycle(string name) => name is
        "start_compose_project" or "stop_compose_project" or "restart_compose_project" or "down_compose_project";
    private static bool NeedsComposeScope(string name) => IsProjectLifecycle(name) || name is "deploy_compose" or "deploy_template";
    private static bool IsDestructive(string name) => name is
        "remove_container" or "remove_all_containers" or "remove_volume" or "remove_network" or "down_compose_project";
}
