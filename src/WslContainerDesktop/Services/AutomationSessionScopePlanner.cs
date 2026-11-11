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

/// <summary>Limits background observations to active sessions with automatic work.</summary>
internal static class AutomationSessionScopePlanner
{
    internal static readonly TimeSpan ObservationMaxAge = TimeSpan.FromSeconds(90);
    internal static IReadOnlyList<WslcSessionInfo> Select(
        IReadOnlyList<WslcSessionInfo> discovered, string defaultName, string selectedName,
        string executable, bool trackAll, IEnumerable<AutomationSessionOwner?> configuredOwners)
    {
        var names = configuredOwners.Where(owner => owner?.IsValid == true &&
                string.Equals(owner.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
            .Select(owner => owner!.SessionName ?? defaultName)
            .ToHashSet(StringComparer.Ordinal);
        return discovered.Where(session =>
                !string.Equals(session.Name, selectedName, StringComparison.Ordinal) &&
                (trackAll || names.Contains(session.Name)))
            .ToArray();
    }
}
