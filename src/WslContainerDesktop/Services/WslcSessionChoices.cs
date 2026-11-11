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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>A workload destination and its observed session generation.</summary>
public sealed record WslcSessionSelection(
    WslcSessionTarget Target, string ExecutablePath, WslcSessionInfo? Generation, string DisplayName);

/// <summary>Builds explicit destination choices while keeping the CLI default as one logical session.</summary>
internal static class WslcSessionChoices
{
    internal static IReadOnlyList<WslcSessionSelection> Create(
        IReadOnlyList<WslcSessionInfo> sessions, string executable, string defaultName, string defaultLabel)
    {
        var defaultSession = sessions.SingleOrDefault(session =>
            string.Equals(session.Name, defaultName, StringComparison.Ordinal));
        if (sessions.Any(session => !string.Equals(session.Name, defaultName, StringComparison.Ordinal) &&
                string.Equals(session.Name, defaultName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(UiText.Get("CombinedSession_IdentityUnavailable",
                "The default session identity could not be determined; no action was started."));
        var choices = new List<WslcSessionSelection>
        {
            new(WslcSessionTarget.Default, executable, defaultSession, defaultLabel),
        };
        choices.AddRange(sessions.Where(session => session.Name != defaultName)
            .OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.Name, StringComparer.Ordinal)
            .Select(session => new WslcSessionSelection(new(session.Name), executable, session, session.Name)));
        return choices;
    }

    internal static async Task<WslcSessionSelection?> SelectAsync(
        IReadOnlyList<WslcSessionSelection> choices,
        Func<IReadOnlyList<WslcSessionSelection>, Task<WslcSessionSelection?>> prompt)
    {
        var active = choices.Where(choice => choice.Generation is not null).ToArray();
        if (active.Length == 1)
            return active[0];
        if (active.Length == 0)
            return choices.Single(choice => choice.Target.Name is null);
        return await prompt(choices);
    }
}
