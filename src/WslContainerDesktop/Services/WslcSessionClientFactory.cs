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

/// <summary>Creates isolated WSLC facades for explicitly named, active sessions.</summary>
public sealed class WslcSessionClientFactory(
    ISettingsService settings,
    IWslcCapabilitiesService capabilities,
    IWslPolicyService policy,
    RestartSuppressionState suppression,
    LegacyPolicyOwnershipStore legacyOwners,
    IWslcService discovery,
    ILogger<WslcService> logger,
    WslcSessionContext sessionContext,
    ContainerPortResolver containerPorts)
{
    internal IWslcService Create(WslcSessionInfo session, string executable, bool managed) =>
        Create(new WslcSessionTarget(session.Name), executable, managed);

    internal IWslcService Create(WslcSessionTarget target, string executable, bool managed) =>
        CreateCore(target, executable, managed, null);

    internal IWslcService CreateGuarded(WslcSessionInfo session, string executable, bool managed) =>
        CreateCore(new WslcSessionTarget(session.Name), executable, managed,
            ct => WslcSessionActionGuard.VerifyAsync(discovery, () => settings.WslcPath, session, executable, ct));

    internal IWslcService CreateForContainerActions(WslcSessionInfo session, string executable, bool managed) =>
        CreateGuarded(session, executable, managed);

    internal IWslcService CreateForSessionControl(WslcSessionTarget target, string executable,
        WslcSessionInfo? generation = null) =>
        CreateCore(target, executable, managed: false,
            generation is null ? null : ct =>
                WslcSessionActionGuard.VerifyAsync(discovery, () => settings.WslcPath, generation, executable, ct),
            allowStopped: true);

    private IWslcService CreateCore(WslcSessionTarget target, string executable, bool managed,
        Func<CancellationToken, Task>? verifier, bool allowStopped = false) =>
        new WslcService(
            new ProcessRunner(settings, allowStopped ? null : sessionContext, fixedSession: target,
                fixedExecutablePath: executable, scopeVerifier: verifier),
            logger, capabilities, settings, managed ? suppression : new RestartSuppressionState(), policy,
            legacyOwners: managed ? legacyOwners : null,
            managedOwner: managed ? AutomationSessionOwner.Capture(executable, OwnerTarget(target)) : null,
            containerPorts: containerPorts);

    private WslcSessionTarget OwnerTarget(WslcSessionTarget target)
    {
        if (target.Name?.StartsWith("wslc-cli-", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(target.Name, WslcSessionActionGuard.ResolveDefaultSessionName(), StringComparison.Ordinal))
            return WslcSessionTarget.Default;
        return target;
    }
}
