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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using System.Collections.ObjectModel;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Verifies Activity mapping for both WSL 3.0.1 and 3.0.2 <c>wslc events</c> shapes.</summary>
public sealed class EngineEventActivityTests
{
    private const string Ts = "2026-10-07T09:30:55.367919946-04:00";

    private static EngineEvent Parse(string line)
    {
        Assert.True(WslcEventParser.TryParseLine(line, out var evt));
        return evt;
    }

    [Theory]
    [InlineData("unhealthy", true)]
    [InlineData("healthy", false)]
    [InlineData("starting", false)]
    public void HealthEventMapsToContainerHealth(string state, bool isError)
    {
        var entry = EngineEventActivity.ToActivityEvent(
            Parse($"{Ts} container health_status: {state} abc123 (image=nginx, name=web)"));

        Assert.Equal(ActivityCategory.Container, entry.Category);
        Assert.Equal(ActivityKind.ContainerHealth, entry.Kind);
        Assert.Equal($"web is {state}", entry.Title);
        Assert.Equal(isError, entry.IsError);
        Assert.Equal("abc123", entry.ContainerId);
    }

    [Fact]
    public void Wsl301StopWithExitCodeStillMapsToStoppedError()
    {
        var entry = EngineEventActivity.ToActivityEvent(
            Parse("2026-09-30T10:12:30.1234567-04:00 container stop id1 (exitCode=137, name=a)"));

        Assert.Equal(ActivityKind.ContainerStopped, entry.Kind);
        Assert.True(entry.IsError);
        Assert.Equal("container stop: a", entry.Title);
    }

    [Fact]
    public void Wsl302BareStopAfterDieIsRedundant()
    {
        var die = EngineEventActivity.ToActivityEvent(Parse($"{Ts} container die id1 (execDuration=19, exitCode=137, name=a)"));
        var stop = Parse($"{Ts} container stop id1 (name=a)");

        Assert.True(EngineEventActivity.IsRedundantStop(stop, [die]));
    }

    [Fact]
    public void Wsl302DieReplacesEarlierBareStop()
    {
        var stop = EngineEventActivity.ToActivityEvent(Parse($"{Ts} container stop id1 (name=a)"));
        var die = Parse($"{Ts} container die id1 (execDuration=19, exitCode=137, name=a)");

        Assert.Same(stop, EngineEventActivity.FindSupersededStop(die, [stop]));
    }

    [Fact]
    public void Wsl301StopWithExitCodeIsNeverRedundantOrSuperseded()
    {
        var die = EngineEventActivity.ToActivityEvent(Parse($"{Ts} container die id1 (exitCode=137, name=a)"));
        var stopWithCode = Parse($"{Ts} container stop id1 (exitCode=137, name=a)");

        Assert.False(EngineEventActivity.IsRedundantStop(stopWithCode, [die]));
        Assert.Null(EngineEventActivity.FindSupersededStop(
            Parse($"{Ts} container die id1 (exitCode=137, name=a)"),
            [EngineEventActivity.ToActivityEvent(stopWithCode)]));
    }

    [Fact]
    public void BareStopIsKeptWithoutMatchingDie()
    {
        var otherContainer = EngineEventActivity.ToActivityEvent(Parse($"{Ts} container die id2 (exitCode=0, name=b)"));
        var muchEarlier = EngineEventActivity.ToActivityEvent(
            Parse("2026-10-07T09:20:00.000000000-04:00 container die id1 (exitCode=0, name=a)"));
        var stop = Parse($"{Ts} container stop id1 (name=a)");

        Assert.False(EngineEventActivity.IsRedundantStop(stop, []));
        Assert.False(EngineEventActivity.IsRedundantStop(stop, [otherContainer, muchEarlier]));
    }

    [Fact]
    public void SelfExitDieMapsToStoppedWithExitCode()
    {
        var entry = EngineEventActivity.ToActivityEvent(Parse($"{Ts} container die id1 (execDuration=0, exitCode=3, name=a)"));

        Assert.Equal(ActivityKind.ContainerStopped, entry.Kind);
        Assert.True(entry.IsError);
    }

    [Fact]
    public void EventOwnershipSurvivesPersistenceAndDeduplicationIsSessionScoped()
    {
        var owner = new AutomationSessionOwner { ExecutablePath = @"C:\wslc.exe", SessionName = "first" };
        var entry = EngineEventActivity.ToActivityEvent(
            Parse($"{Ts} container die id1 (exitCode=1, name=a)"), owner);
        var stored = System.Text.Json.JsonSerializer.Deserialize<ActivityEvent>(
            System.Text.Json.JsonSerializer.Serialize(entry))!;

        Assert.Equal(owner, stored.SessionOwner);
        Assert.True(EngineEventActivity.HasSameOwner(stored, owner with { ExecutablePath = @"c:\WSLC.exe" }));
        Assert.False(EngineEventActivity.HasSameOwner(stored, owner with { SessionName = "FIRST" }));
        Assert.False(EngineEventActivity.HasSameOwner(stored, owner with { ExecutablePath = @"C:\other.exe" }));
        Assert.False(EngineEventActivity.HasSameOwner(stored, null));
        Assert.True(EngineEventActivity.HasSameOwner(new ActivityEvent(), null));
    }

    [Fact]
    public void BatchDeduplicatesWithinEachSessionButKeepsOtherSessionsDistinct()
    {
        var entries = new ObservableCollection<ActivityEvent>();
        var first = Parse($"{Ts} container start id1 (name=a)");
        first.SessionOwner = AutomationSessionOwner.Capture("engine", new("alpha"));
        var second = Parse($"{Ts} container start id1 (name=a)");
        second.SessionOwner = AutomationSessionOwner.Capture("engine", new("Alpha"));

        Assert.True(ActivityEventBatch.Apply(entries, [first, second, first], 500));
        Assert.Equal(2, entries.Count);
        Assert.Equal(["Alpha", "alpha"], entries.Select(entry => entry.SessionOwner!.SessionName).Order(StringComparer.Ordinal));
        Assert.False(ActivityEventBatch.Apply(entries, [first, second], 500));
    }

    [Fact]
    public void BatchReplacesRedundantStopsAndKeepsTheNewestBoundedEvents()
    {
        var entries = new ObservableCollection<ActivityEvent>();
        var stop = Parse($"{Ts} container stop id1 (name=a)");
        var die = Parse($"{Ts} container die id1 (exitCode=137, name=a)");
        var later = Parse("2026-10-07T09:31:00.000000000-04:00 container start id2 (name=b)");
        Assert.True(ActivityEventBatch.Apply(entries, [stop, die, later], 2));
        Assert.Equal(2, entries.Count);
        Assert.Equal("id2", entries[0].ContainerId);
        Assert.Equal("die", entries[1].SourceAction);
        Assert.True(entries[1].IsError);
    }
}
