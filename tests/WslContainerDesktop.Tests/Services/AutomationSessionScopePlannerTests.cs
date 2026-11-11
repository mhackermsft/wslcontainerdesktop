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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AutomationSessionScopePlannerTests
{
    [Fact]
    public void DisabledAutostartObservesOnlyConfiguredOwners()
    {
        var sessions = new[] { Session("default", 1), Session("work", 2), Session("Work", 3), Session("unused", 4) };
        var owners = new[]
        {
            AutomationSessionOwner.Capture("ENGINE", new("work")),
            AutomationSessionOwner.Capture("other", new("unused")),
            AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default),
        };

        var result = AutomationSessionScopePlanner.Select(sessions, "default", "Work", "engine", false, owners);

        Assert.Equal(["default", "work"], result.Select(session => session.Name));
    }

    [Fact]
    public void EnabledAutostartObservesEveryOtherActiveSession()
    {
        var sessions = new[] { Session("default", 1), Session("work", 2), Session("unused", 3) };

        var result = AutomationSessionScopePlanner.Select(sessions, "default", "default", "engine", true, []);

        Assert.Equal(["work", "unused"], result.Select(session => session.Name));
    }

    private static WslcSessionInfo Session(string name, int id) => new() { ID = id, CreatorPid = id, Name = name };

    [Fact]
    public void StartupOnlySessionsDoNotEnterTheSupervisedHealthSet()
    {
        var sessions = new[] { Session("default", 1), Session("health", 2), Session("startup-only", 3) };
        var owners = new[] { AutomationSessionOwner.Capture("engine", new("health")) };
        var all = AutomationSessionScopePlanner.Select(sessions, "default", "default", "engine", true, owners);
        var supervised = AutomationSessionScopePlanner.Select(sessions, "default", "default", "engine", false, owners);

        Assert.Equal(["health"], supervised.Select(session => session.Name));
        Assert.Equal(["startup-only"], all.Except(supervised).Select(session => session.Name));
    }
}
