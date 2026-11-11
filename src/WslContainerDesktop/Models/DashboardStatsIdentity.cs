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

namespace WslContainerDesktop.Models;

/// <summary>Immutable identity required to retain a dashboard statistics sample.</summary>
internal readonly record struct DashboardStatsIdentity(
    WslcSessionInfo Session, string Id, string Name, string Image, long CreatedAt,
    bool CreatedAtKnown, string Executable)
{
    internal static IEqualityComparer<DashboardStatsIdentity> Comparer { get; } = new IdentityComparer();

    internal static DashboardStatsIdentity Capture(WslcSessionInfo session, ContainerInfo container, string executable) =>
        new(session, container.Id, container.Name, container.Image, container.CreatedAt, container.CreatedAtKnown, executable);

    internal bool IsValid => Session is not null && Session.ID > 0 && Session.CreatorPid > 0 &&
        !string.IsNullOrWhiteSpace(Session.Name) && !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Name) && CreatedAtKnown && CreatedAt > 0 &&
        !string.IsNullOrWhiteSpace(Executable);

    internal bool Matches(DashboardStatsIdentity other) =>
        IsValid && other.IsValid && Comparer.Equals(this, other);

    private sealed class IdentityComparer : IEqualityComparer<DashboardStatsIdentity>
    {
        public bool Equals(DashboardStatsIdentity x, DashboardStatsIdentity y) =>
            x.Session == y.Session && x.Id == y.Id && x.Name == y.Name && x.Image == y.Image &&
            x.CreatedAt == y.CreatedAt && x.CreatedAtKnown == y.CreatedAtKnown &&
            string.Equals(x.Executable, y.Executable, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DashboardStatsIdentity value) =>
            HashCode.Combine(value.Session, value.Id, value.Name, value.Image, value.CreatedAt,
                value.CreatedAtKnown, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Executable ?? string.Empty));
    }
}
