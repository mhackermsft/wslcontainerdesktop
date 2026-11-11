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

namespace WslContainerDesktop.ViewModels;

/// <summary>Reconciles displayed container rows without merging identities across sessions.</summary>
internal static class ContainerSessionRows
{
    internal static bool MatchesCapturedIdentity(ContainerRowViewModel row, WslcContainerActionTarget target) =>
        row.SessionSource?.Session == target.Session && target.Matches(row.Model);

    internal static ContainerRowViewModel? FindForOwner(
        IEnumerable<ContainerRowViewModel> rows, string id, string? name,
        AutomationSessionOwner owner, string executable, string? defaultSessionName)
    {
        if (!owner.IsValid || !string.Equals(owner.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
            return null;
        var session = owner.SessionName ?? defaultSessionName;
        if (string.IsNullOrWhiteSpace(session))
            return null;
        return FindUniqueManaged(rows.Where(row => string.Equals(row.SessionName, session, StringComparison.Ordinal)),
            id, name, _ => true);
    }

    internal static ContainerRowViewModel? FindUniqueManaged(
        IEnumerable<ContainerRowViewModel> rows, string id, string? name,
        Func<ContainerRowViewModel, bool> isManaged)
    {
        var candidates = rows.ToArray();
        if (!string.IsNullOrWhiteSpace(id))
        {
            var matches = candidates.Where(row => !string.IsNullOrWhiteSpace(row.Id) &&
                (row.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                 id.StartsWith(row.Id, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
            return matches.Length == 1 && matches[0].SessionActionsAvailable && isManaged(matches[0]) ? matches[0] : null;
        }
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var named = candidates.Where(row =>
            string.Equals(row.Name, name.TrimStart('/'), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return named.Length == 1 && named[0].SessionActionsAvailable && isManaged(named[0]) ? named[0] : null;
    }

    internal static IReadOnlyList<ContainerRowViewModel> Reconcile(
        IReadOnlyList<ContainerRowViewModel> previous, IReadOnlyList<CombinedSessionRow> incoming,
        bool showAll, out bool ambiguous)
    {
        var selected = incoming.Where(row => row.Container is not null &&
            (showAll || row.Container.State == ContainerState.Running)).ToArray();
        var counts = selected.GroupBy(row => (row.Session, row.Container!.Id))
            .ToDictionary(group => group.Key, group => group.Count());
        var existing = previous.Where(row => row.SessionSource is not null)
            .GroupBy(row => (row.SessionSource!.Session, row.Id))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var result = new List<ContainerRowViewModel>(selected.Length);
        ambiguous = false;
        foreach (var source in selected)
        {
            var container = source.Container!;
            var key = (source.Session, container.Id);
            var duplicate = string.IsNullOrWhiteSpace(container.Id) || counts[key] != 1;
            ambiguous |= duplicate;
            ContainerRowViewModel row;
            if (!duplicate && existing.TryGetValue(key, out var old) && SameIncarnation(old.Model, container))
                row = old;
            else
                row = new ContainerRowViewModel(source);
            row.Update(source, duplicate);
            result.Add(row);
        }
        return result;
    }

    private static bool SameIncarnation(ContainerInfo previous, ContainerInfo current) =>
        string.Equals(previous.Id, current.Id, StringComparison.Ordinal) &&
        string.Equals(previous.Name, current.Name, StringComparison.Ordinal) &&
        previous.CreatedAtKnown && current.CreatedAtKnown && previous.CreatedAt > 0 &&
        previous.CreatedAt == current.CreatedAt;
}
