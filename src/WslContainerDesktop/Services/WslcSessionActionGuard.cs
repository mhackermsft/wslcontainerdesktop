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

using System.Security.Principal;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Shared validation for explicitly scoped session actions.</summary>
internal static class WslcSessionActionGuard
{
    internal static bool IsManaged(WslcSessionContext context, WslcSessionInfo session, Func<string> defaultSessionName)
    {
        var selected = context.SelectedSessionName;
        if (selected is not null)
            return string.Equals(selected, session.Name, StringComparison.Ordinal);
        if (!session.Name.StartsWith("wslc-cli-", StringComparison.OrdinalIgnoreCase))
            return false;
        var defaultName = defaultSessionName();
        if (string.Equals(defaultName, session.Name, StringComparison.Ordinal))
            return true;
        if (string.Equals(defaultName, session.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("CombinedSession_IdentityUnavailable",
                "The default session identity could not be determined; no action was started."));
        return false;
    }

    internal static async Task VerifyAsync(IWslcService discovery, Func<string> executablePath,
        WslcSessionInfo expected, string executable, CancellationToken ct)
    {
        if (expected.ID <= 0 || expected.CreatorPid <= 0 || string.IsNullOrWhiteSpace(expected.Name))
            throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                "The session changed or is no longer available; refresh before taking action."));
        var information = await discovery.GetSystemInfoAsync(ct).ConfigureAwait(false);
        if (!string.Equals(executable, executablePath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("WslcSessionInventory_ExecutableChanged",
                "The WSLC executable changed while session inventory was being read."));
        if (information.Server?.Sessions is null || information.Server.Sessions.Any(session => session is null))
            throw new InvalidOperationException(UiText.Get("WslcSystemInfo_InvalidSessions",
                "WSLC system information did not contain a valid session list."));
        var matches = information.Server.Sessions.Where(session =>
            session.ID == expected.ID || string.Equals(session.Name, expected.Name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].ID != expected.ID || matches[0].CreatorPid != expected.CreatorPid ||
            !string.Equals(matches[0].Name, expected.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(UiText.Get("CombinedSession_ActionSessionChanged",
                "The session changed or is no longer available; refresh before taking action."));
        }
    }

#if WINDOWS
    internal static string ResolveDefaultSessionName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var account = identity.User?.Translate(typeof(NTAccount)).Value;
            if (string.IsNullOrWhiteSpace(account))
                throw new InvalidOperationException();
            var separator = account.LastIndexOf('\\');
            var username = account[(separator + 1)..];
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return $"wslc-cli{(elevated ? "-admin" : string.Empty)}-{username}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IdentityNotMappedException or System.Security.SecurityException)
        {
            throw new InvalidOperationException(UiText.Get("CombinedSession_IdentityUnavailable",
                "The default session identity could not be determined; no action was started."), ex);
        }
    }
#endif
}
