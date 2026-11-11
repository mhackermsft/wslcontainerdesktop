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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class DashboardStatsIdentityTests
{
    private static readonly WslcSessionInfo Session = new() { ID = 1, CreatorPid = 10, Name = "alpha" };

    [Fact]
    public void EquivalentSnapshotsRetainReadings()
    {
        var original = DashboardStatsIdentity.Capture(Session, Container(), @"C:\engine\wslc.exe");
        var refreshed = DashboardStatsIdentity.Capture(Session with { }, Container(), @"c:\ENGINE\wslc.exe");

        Assert.True(original.Matches(refreshed));
        var indexed = new Dictionary<DashboardStatsIdentity, int>(DashboardStatsIdentity.Comparer) { [original] = 1 };
        Assert.Equal(1, indexed[refreshed]);
    }

    [Fact]
    public void ChangedOrUnknownIdentityCannotRetainReadings()
    {
        var original = DashboardStatsIdentity.Capture(Session, Container(), "engine");
        var replacements = new[]
        {
            original with { Session = Session with { CreatorPid = 11 } },
            original with { Session = Session with { Name = "Alpha" } },
            original with { Id = "other" },
            original with { Name = "other" },
            original with { Image = "other" },
            original with { CreatedAt = 101 },
            original with { CreatedAtKnown = false },
            original with { Executable = "other" },
            original with { Id = "" },
            original with { Session = Session with { CreatorPid = 0 } },
        };

        Assert.All(replacements, replacement => Assert.False(original.Matches(replacement)));
        var indexed = new Dictionary<DashboardStatsIdentity, int>(DashboardStatsIdentity.Comparer) { [original] = 1 };
        Assert.All(replacements, replacement => Assert.False(indexed.ContainsKey(replacement)));
        Assert.False(default(DashboardStatsIdentity).Matches(original));
    }

    [Fact]
    public void SnapshotDoesNotFollowMutableContainerChanges()
    {
        var container = Container();
        var original = DashboardStatsIdentity.Capture(Session, container, "engine");
        container.Name = "renamed";

        Assert.False(original.Matches(DashboardStatsIdentity.Capture(Session, container, "engine")));
    }

    private static ContainerInfo Container() => new()
    {
        Id = "123456789abc", Name = "web", Image = "example/app:latest", CreatedAt = 100, CreatedAtKnown = true,
    };
}
