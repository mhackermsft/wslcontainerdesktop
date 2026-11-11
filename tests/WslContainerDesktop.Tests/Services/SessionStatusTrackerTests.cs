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

public sealed class SessionStatusTrackerTests
{
    private static readonly WslcSessionInfo Alpha = new() { ID = 1, CreatorPid = 100, Name = "alpha" };
    private static readonly WslcSessionInfo Beta = new() { ID = 2, CreatorPid = 200, Name = "beta" };
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void DiscoveryConfirmsSessionIsActiveWithoutInventingContainerCounts()
    {
        var tracker = new SessionStatusTracker();
        tracker.Discover([Alpha, Beta], Now);
        var result = tracker.Snapshot(Now);
        Assert.Equal(2, result.Count);
        Assert.All(result, item =>
        {
            Assert.Equal(SessionIndicatorState.Active, item.State);
            Assert.Null(item.Running);
            Assert.Null(item.Total);
        });
        Assert.Equal(SessionIndicatorState.Active, SessionStatusTracker.Overall(result, false));
    }

    [Fact]
    public void ContainerCountsArePerSessionAndEmptySessionCanBeActive()
    {
        var tracker = new SessionStatusTracker();
        tracker.Discover([Alpha, Beta], Now);
        tracker.ObserveContainers(new(Alpha, new([
            new() { StateValue = (int)ContainerState.Running },
            new() { StateValue = (int)ContainerState.Stopped },
        ]), ObservedAt: Now));
        tracker.ObserveContainers(new(Beta, new([]), ObservedAt: Now));
        var result = tracker.Snapshot(Now);
        Assert.Equal(1, result[0].Running);
        Assert.Equal(2, result[0].Total);
        Assert.Equal(0, result[1].Total);
        Assert.All(result, item => Assert.Equal(SessionIndicatorState.Active, item.State));
        Assert.Equal(SessionIndicatorState.Active, SessionStatusTracker.Overall(result, false));
    }

    [Fact]
    public void ExpirationClearsCountsAndDoesNotConvertFailureToHealthy()
    {
        var tracker = new SessionStatusTracker();
        tracker.Observe(new("alpha", Alpha, SessionIndicatorState.Failed, Now));
        var expired = Assert.Single(tracker.Snapshot(Now + SessionStatusTracker.MaximumAge + TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionIndicatorState.Unknown, expired.State);
        Assert.Null(expired.Running);
        Assert.Null(expired.Total);
    }

    [Fact]
    public void PausedSessionDoesNotTurnActiveAggregateRedOrExpireIntoFailure()
    {
        var tracker = new SessionStatusTracker();
        tracker.Observe(new("alpha", Alpha, SessionIndicatorState.Paused, Now.AddDays(-1)));
        tracker.Observe(new("beta", Beta, SessionIndicatorState.Active, Now));
        var result = tracker.Snapshot(Now);
        Assert.Equal(SessionIndicatorState.Paused, result[0].State);
        Assert.Equal(SessionIndicatorState.Active, SessionStatusTracker.Overall(result, false));
    }

    [Fact]
    public void AggregateNeverHidesAnUncertainOrFailedSession()
    {
        foreach (var state in new[] { SessionIndicatorState.Failed, SessionIndicatorState.Attention,
                     SessionIndicatorState.Unknown, SessionIndicatorState.Paused })
            Assert.Equal(state == SessionIndicatorState.Paused ? SessionIndicatorState.Active : state,
                SessionStatusTracker.Overall([
                    new("alpha", Alpha, SessionIndicatorState.Active, Now),
                    new("beta", Beta, state, Now),
                ], false));
        Assert.Equal(SessionIndicatorState.Attention, SessionStatusTracker.Overall([], true));
    }

    [Fact]
    public void ReplacedGenerationDiscardsPreviousCountsAndRejectsLateDelivery()
    {
        var tracker = new SessionStatusTracker();
        tracker.Observe(new("alpha", Alpha, SessionIndicatorState.Active, Now, 3, 5));
        var replacement = Alpha with { CreatorPid = 101 };
        tracker.Discover([replacement], Now.AddSeconds(1));
        tracker.Observe(new("alpha", Alpha, SessionIndicatorState.Active, Now, 3, 5));
        var result = Assert.Single(tracker.Snapshot(Now.AddSeconds(1)));
        Assert.Equal(replacement, result.Generation);
        Assert.Equal(SessionIndicatorState.Active, result.State);
        Assert.Null(result.Total);
    }

    [Fact]
    public void OlderDiscoveryCannotRemoveNewerObservationAndRemovedSessionCannotReturnFromOldRead()
    {
        var tracker = new SessionStatusTracker();
        tracker.Observe(new("beta", Beta, SessionIndicatorState.Active, Now.AddSeconds(2), 1, 1));
        tracker.Discover([Alpha], Now);
        Assert.Equal(2, tracker.Snapshot(Now.AddSeconds(2)).Count);
        tracker.Discover([Alpha], Now.AddSeconds(3));
        tracker.Observe(new("beta", Beta, SessionIndicatorState.Active, Now.AddSeconds(2), 1, 1));
        Assert.Single(tracker.Snapshot(Now.AddSeconds(3)));
        tracker.Discover([Alpha, Beta], Now);
        Assert.Single(tracker.Snapshot(Now.AddSeconds(3)));
    }

    [Fact]
    public void NamesDifferingOnlyInCaseRemainDistinctAndEngineResetClearsEverything()
    {
        var tracker = new SessionStatusTracker();
        tracker.Discover([Alpha, Beta with { Name = "Alpha" }], Now);
        Assert.Equal(2, tracker.Snapshot(Now).Count);
        tracker.Clear();
        Assert.Empty(tracker.Snapshot(Now));
    }

    [Fact]
    public void CompletedRemovalIsImmediateAndOldReadsCannotResurrectIt()
    {
        var tracker = new SessionStatusTracker();
        tracker.Observe(new("alpha", Alpha, SessionIndicatorState.Active, Now));
        tracker.Observe(new("beta", Beta, SessionIndicatorState.Active, Now));
        tracker.Remove("beta", Now.AddSeconds(2));
        Assert.Equal("alpha", Assert.Single(tracker.Snapshot(Now.AddSeconds(2))).Name);
        tracker.Discover([Alpha, Beta], Now.AddSeconds(1));
        tracker.Observe(new("beta", Beta, SessionIndicatorState.Attention, Now.AddSeconds(1)));
        Assert.Single(tracker.Snapshot(Now.AddSeconds(2)));
        tracker.Discover([Alpha], Now.AddSeconds(3));
        tracker.Observe(new("beta", Beta, SessionIndicatorState.Active, Now.AddSeconds(1)));
        Assert.Single(tracker.Snapshot(Now.AddSeconds(3)));
        tracker.Discover([Alpha, Beta with { ID = 3 }], Now.AddSeconds(4));
        Assert.Equal(2, tracker.Snapshot(Now.AddSeconds(4)).Count);
    }

    [Fact]
    public void RemovalDoesNotHideNewerReplacementOrUnrelatedFailures()
    {
        var tracker = new SessionStatusTracker();
        tracker.Observe(new("beta", Beta with { ID = 3 }, SessionIndicatorState.Active, Now.AddSeconds(3)));
        tracker.Remove("beta", Now.AddSeconds(2));
        Assert.Equal(3, Assert.Single(tracker.Snapshot(Now.AddSeconds(3))).Generation!.ID);
        Assert.Equal(SessionIndicatorState.Active, SessionStatusTracker.Overall([
            new("alpha", Alpha, SessionIndicatorState.Removing, null),
            new("beta", Beta, SessionIndicatorState.Active, Now)], false));
        Assert.Equal(SessionIndicatorState.Attention, SessionStatusTracker.Overall([
            new("alpha", Alpha, SessionIndicatorState.Removing, null),
            new("beta", Beta, SessionIndicatorState.Attention, Now)], false));
    }

    [Fact]
    public void FailedReadNeverBecomesSuccessfulEmptyInventory()
    {
        var tracker = new SessionStatusTracker();
        tracker.ObserveContainers(new(Alpha, new(null, TimedOut: true), ObservedAt: Now));
        var result = Assert.Single(tracker.Snapshot(Now));
        Assert.Equal(SessionIndicatorState.Attention, result.State);
        Assert.Null(result.Total);
        tracker.ObserveContainers(new(Alpha, new([]), SessionChanged: true, ObservedAt: Now.AddSeconds(1)));
        Assert.Equal(SessionIndicatorState.Unknown, Assert.Single(tracker.Snapshot(Now.AddSeconds(1))).State);
    }
}
