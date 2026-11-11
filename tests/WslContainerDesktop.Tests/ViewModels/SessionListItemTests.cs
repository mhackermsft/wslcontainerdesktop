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

namespace WslContainerDesktop.Tests.ViewModels;

public sealed class SessionListItemTests
{
    [Fact]
    public void ActiveRemovalDoesNotLookLikeAnInterruptedRemoval()
    {
        var pending = new ManagedSessionDefinition(Guid.NewGuid(), "work", PendingDeletion: true);
        var active = new SessionListItem("work", pending, null, false, false, null, false, removing: true);
        var interrupted = new SessionListItem("work", pending, null, false, false, null, false);

        Assert.Contains("Removing", active.StatusDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("interrupted", active.StatusDisplay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interrupted", interrupted.StatusDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunningAppSessionCanStopOrBeRemoved()
    {
        var item = new SessionListItem("work", new(Guid.NewGuid(), "work"), Session(),
            true, true, null, false);

        Assert.False(item.CanStart);
        Assert.True(item.CanStop);
        Assert.True(item.CanRemove);
    }

    [Fact]
    public void StoppedAppSessionCanStartWithoutDeletingData()
    {
        var item = new SessionListItem("work", new(Guid.NewGuid(), "work"), null,
            false, false, null, false);

        Assert.True(item.CanStart);
        Assert.False(item.CanStop);
        Assert.True(item.CanRemove);
    }

    [Fact]
    public void UnknownSessionStatusDoesNotOfferStart()
    {
        var item = new SessionListItem("work", new(Guid.NewGuid(), "work"), null,
            false, false, null, false, inventoryUnavailable: true);

        Assert.False(item.CanStart);
        Assert.False(item.CanStop);
        Assert.Contains("unavailable", item.StatusDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalSessionCanStopButCannotBeRemoved()
    {
        var item = new SessionListItem("work", null, Session(), false, false, null, false);

        Assert.False(item.CanStart);
        Assert.True(item.CanStop);
        Assert.False(item.CanRemove);
    }

    [Fact]
    public void StoppedDefaultOffersStartAndExplainsRetainedData()
    {
        var stopped = new SessionListItem("default", null, null, false, false, null, true,
            defaultStoppedByUser: true);
        Assert.True(stopped.CanResumeDefault);
        Assert.False(stopped.CanStop);
        Assert.Equal("Start", stopped.ResumeDefaultLabel);
        Assert.Contains("data kept", stopped.StatusDisplay, StringComparison.OrdinalIgnoreCase);

        var active = new SessionListItem("default", null, Session(), false, false, null, true,
            defaultStoppedByUser: true);
        Assert.True(active.CanResumeDefault);
        Assert.Equal("Resume", active.ResumeDefaultLabel);
        Assert.Contains("paused", active.StatusDisplay, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StopProgressPreventsDuplicateActionsAndReportsOutcome()
    {
        var progress = new SessionStopProgress();
        Assert.True(progress.TryBegin("Default CLI session", defaultSession: true));
        Assert.False(progress.TryBegin("another"));
        Assert.True(progress.IsBusy);
        Assert.False(progress.CanEdit);
        Assert.Contains("Stopping", progress.Message, StringComparison.OrdinalIgnoreCase);

        progress.Complete();
        Assert.True(progress.IsSuccess);
        Assert.True(progress.CanEdit);
        Assert.Contains("data is kept", progress.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Start", progress.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(progress.TryBegin("Default CLI session", true, starting: true));
        progress.Complete("engine unavailable");
        Assert.True(progress.IsFailure);
        Assert.Contains("engine unavailable", progress.Message);
    }

    private static WslcSessionInfo Session() => new() { ID = 1, CreatorPid = 1, Name = "work" };
}
