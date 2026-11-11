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

/// <summary>Creates a generation-guarded Dev Container supervisor for one owning session.</summary>
public sealed class DevContainerSessionCoordinator(
    IWslcSessionSelectionService selector,
    WslcSessionInventoryReader reader,
    ISettingsService settings,
    IDevContainerStore store,
    IDevContainerFeatureResolver features,
    ComposeProjectSupervisor compose,
    IComposeProjectStore composeProjects,
    SessionAutomationCoordinator automation,
    WslcSessionContext management,
    DialogService dialogs,
    ILogger<DevContainerSupervisor> supervisorLogger,
    ILogger<DevContainerSessionCoordinator> logger)
{
    internal async Task<DevContainerSessionScope?> OpenAsync(DevContainerConfig config, bool allowChoice,
        bool startDefault = true)
    {
        IDisposable? managementLease = management.LockSelection();
        try
        {
            SessionOperationTarget? target;
            var owner = config.SessionOwner?.IsValid == true ? config.SessionOwner :
                config.Compose is null ? null : composeProjects.Get(config.Compose.Project.Name)?.SessionOwner;
            if (owner?.IsValid == true)
            {
                target = await ForOwnerAsync(owner);
            }
            else
            {
                if (!allowChoice)
                    throw new InvalidOperationException(UiText.Get("DevContainer_SessionUnassigned",
                        "This Dev Container has no assigned session. Start it and choose a session first."));
                target = await SessionOperationTarget.ChooseAsync(selector, dialogs, logger, startDefault: startDefault);
                if (target is null) return null;
            }
            await target.VerifyAsync();
            ComposeSessionScope? composeScope = null;
            try
            {
                if (config.Compose is not null)
                    composeScope = await automation.GetComposeScopeAsync(target.Selection);
                var context = composeScope?.Context ?? new WslcSessionContext();
                if (composeScope is null) context.Select(target.Selection.Target.Name);
                var runner = new ProcessRunner(settings, management, fixedSession: target.Selection.Target,
                    fixedExecutablePath: target.Selection.ExecutablePath);
                var supervisor = new DevContainerSupervisor(composeScope?.Client ?? target.Client, store, features,
                    composeScope?.Supervisor ?? compose, runner, supervisorLogger, context,
                    ct => target.VerifyAsync(ct));
                var result = new DevContainerSessionScope(target, supervisor, composeScope, managementLease);
                composeScope = null;
                managementLease = null;
                return result;
            }
            finally { composeScope?.Dispose(); }
        }
        finally { managementLease?.Dispose(); }
    }

    internal async Task<SessionOperationTarget> ForOwnerAsync(AutomationSessionOwner owner)
    {
        if (!string.Equals(owner.ExecutablePath, settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("DevContainer_DifferentEngine",
                "This Dev Container belongs to a different engine. Restore that engine before continuing."));
        var name = owner.SessionName ?? WslcSessionActionGuard.ResolveDefaultSessionName();
        var generation = (await reader.DiscoverAsync()).SingleOrDefault(session =>
            string.Equals(session.Name, name, StringComparison.Ordinal));
        if (generation is null || generation.CreatorPid <= 0)
            throw new InvalidOperationException(UiText.Get("DevContainer_OwnerUnavailable",
                "The Dev Container's session is not running. Start it in WSL engine or in the app that created it, then try again."));
        var selection = new WslcSessionSelection(new(owner.SessionName), owner.ExecutablePath, generation,
            owner.SessionName ?? UiText.Get("WslEngine_Session_Default", "Default CLI session"));
        await selector.VerifyAsync(selection);
        return new(selection, selector.CreateClient(selection), selector);
    }
}

internal sealed record DevContainerSessionScope(
    SessionOperationTarget Target, DevContainerSupervisor Supervisor, ComposeSessionScope? ComposeScope,
    IDisposable? ManagementLease) : IDisposable
{
    internal CancellationToken Lifetime => ComposeScope?.Lifetime ?? CancellationToken.None;
    public void Dispose()
    {
        try { ComposeScope?.Dispose(); }
        finally { ManagementLease?.Dispose(); }
    }
}
