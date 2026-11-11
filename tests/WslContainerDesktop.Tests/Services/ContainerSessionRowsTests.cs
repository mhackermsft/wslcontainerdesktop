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
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ContainerSessionRowsTests
{
    [Fact]
    public void DashboardNavigationRejectsReplacedSessionAndContainerGenerations()
    {
        var session = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var row = Assert.Single(ContainerSessionRows.Reconcile([], [Row(session, 10)], true, out _));
        var target = WslcContainerActionTarget.Capture(session, row.Model);
        Assert.True(ContainerSessionRows.MatchesCapturedIdentity(row, target));
        Assert.False(ContainerSessionRows.MatchesCapturedIdentity(row, target with { Session = session with { CreatorPid = 200 } }));
        Assert.False(ContainerSessionRows.MatchesCapturedIdentity(row, target with { CreatedAt = 11 }));
        Assert.False(ContainerSessionRows.MatchesCapturedIdentity(row, target with { Image = "other" }));
    }

    [Fact]
    public void SameIdsInDifferentSessionsRemainDistinctAcrossRefresh()
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var initial = ContainerSessionRows.Reconcile([], [Row(first, 10), Row(second, 10)], true, out var ambiguous);
        var refreshed = ContainerSessionRows.Reconcile(initial, [Row(first, 10), Row(second, 10)], true, out var nextAmbiguous);

        Assert.False(ambiguous);
        Assert.False(nextAmbiguous);
        Assert.NotSame(initial[0], initial[1]);
        Assert.Same(initial[0], refreshed[0]);
        Assert.Same(initial[1], refreshed[1]);
        Assert.Equal(["first", "second"], refreshed.Select(row => row.SessionName));
    }

    [Fact]
    public void RecreatedOrAmbiguousContainersNeverReuseAnActionableRow()
    {
        var session = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var initial = ContainerSessionRows.Reconcile([], [Row(session, 10)], true, out _);
        var recreated = ContainerSessionRows.Reconcile(initial, [Row(session, 11)], true, out _);
        var duplicate = ContainerSessionRows.Reconcile(recreated, [Row(session, 11), Row(session, 11)], true, out var ambiguous);

        Assert.NotSame(initial[0], recreated[0]);
        Assert.True(ambiguous);
        Assert.All(duplicate, row => Assert.False(row.SessionActionsAvailable));
        Assert.NotSame(duplicate[0], duplicate[1]);
    }

    [Fact]
    public void NavigationRefusesMatchingIdsInDifferentSessions()
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var rows = ContainerSessionRows.Reconcile([], [Row(first, 10), Row(second, 10)], true, out _);

        Assert.Null(ContainerSessionRows.FindUniqueManaged(rows, "123456", "web",
            row => row.SessionName == "second"));
        Assert.Same(rows[1], ContainerSessionRows.FindUniqueManaged([rows[1]], "123456", "web",
            row => row.SessionName == "second"));
        Assert.Null(ContainerSessionRows.FindUniqueManaged([rows[0]], "123456", "web",
            row => row.SessionName == "second"));
        Assert.Null(ContainerSessionRows.FindUniqueManaged([rows[1]], "different-id", "web",
            row => row.SessionName == "second"));
    }

    [Fact]
    public void NotificationNavigationUsesTheRecordedOwnerRatherThanAnotherMatchingSession()
    {
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "second" };
        var rows = ContainerSessionRows.Reconcile([], [Row(first, 10), Row(second, 10)], true, out _);
        var owner = new AutomationSessionOwner { ExecutablePath = "engine", SessionName = "second" };

        Assert.Same(rows[1], ContainerSessionRows.FindForOwner(rows, "123456", null, owner, "ENGINE", null));
        Assert.Null(ContainerSessionRows.FindForOwner(rows, "123456", null, owner, "another-engine", null));
        Assert.Null(ContainerSessionRows.FindForOwner(rows, "123456", null, owner with { SessionName = "SECOND" }, "engine", null));
        Assert.Same(rows[0], ContainerSessionRows.FindForOwner(rows, "123456", null,
            owner with { SessionName = null }, "engine", "first"));
        Assert.Null(ContainerSessionRows.FindForOwner(rows, "123456", null,
            owner with { SessionName = null }, "engine", null));
    }

    private static CombinedSessionRow Row(WslcSessionInfo session, long created) =>
        CombinedSessionRow.FromContainer(new WslcCombinedInventory.Resource<ContainerInfo>(session, new ContainerInfo
        {
            Id = "123456789abc", Name = "web", Image = "example", CreatedAt = created, CreatedAtKnown = true,
            StateValue = (int)ContainerState.Running,
        }));
}
