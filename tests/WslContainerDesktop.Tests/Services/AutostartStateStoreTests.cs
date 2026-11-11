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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AutostartStateStoreTests
{
    [Fact]
    public void AssigningLegacyHistoryPreservesOtherEntriesAndPersistsTheOwner()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, """
            [{"Id":"one","Name":"web","FutureField":"keep"},{"Id":"two","Name":"db"}]
            """);
        var store = fixture.Reload();
        var entries = store.Snapshot();
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));

        store.AssignLegacy(entries[0], owner);
        var restored = fixture.Reload();
        var state = restored.Snapshot();

        Assert.True(restored.Tracks(owner));
        Assert.Equal(owner, state.Single(entry => entry.Id == "one").SessionOwner);
        Assert.Null(state.Single(entry => entry.Id == "two").SessionOwner);
        Assert.Equal("keep", state.Single(entry => entry.Id == "one").AdditionalData!["FutureField"].GetString());
    }

    [Fact]
    public void TrackingOneSessionDoesNotEraseOtherSessionsOrUnassignedHistory()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, """[{"Id":"legacy","Name":"old"}]""");
        var store = fixture.Reload();
        var first = AutomationSessionOwner.Capture("engine", new("first"));
        var second = AutomationSessionOwner.Capture("engine", new("FIRST"));
        store.EnableTracking(first);
        store.EnableTracking(second);
        store.ReplaceOwned(first, [new AutostartEntry { Id = "one", Name = "web", SessionOwner = first }]);
        store.ReplaceOwned(second, [new AutostartEntry { Id = "two", Name = "web", SessionOwner = second }]);

        store.ReplaceOwned(first, []);

        var reloaded = fixture.Reload();
        Assert.True(reloaded.Tracks(first));
        Assert.True(reloaded.Tracks(second));
        Assert.Contains(reloaded.Snapshot(), entry => entry.Id == "legacy" && entry.SessionOwner is null);
        Assert.Contains(reloaded.Snapshot(), entry => entry.Id == "two" && entry.SessionOwner == second);
        Assert.DoesNotContain(reloaded.Snapshot(), entry => entry.Id == "one");
    }

    [Fact]
    public void CorruptHistoryIsNotOverwritten()
    {
        using var fixture = new Fixture();
        const string corrupt = "{not-json";
        File.WriteAllText(fixture.Path, corrupt);
        var store = fixture.Reload();

        Assert.True(store.HasLoadError);
        Assert.Throws<InvalidOperationException>(() =>
            store.EnableTracking(AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default)));
        Assert.Equal(corrupt, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void FailedWriteDoesNotActivateTracking()
    {
        using var fixture = new Fixture();
        var blocker = System.IO.Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(blocker, "keep");
        var store = new AutostartStateStore(NullLogger<AutostartStateStore>.Instance,
            System.IO.Path.Combine(blocker, "state.json"));
        var owner = AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default);

        Assert.ThrowsAny<IOException>(() => store.EnableTracking(owner));
        Assert.False(store.Tracks(owner));
        Assert.Equal("keep", File.ReadAllText(blocker));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wcd-autostart-state-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "state.json");
        public Fixture() => Directory.CreateDirectory(Root);
        public AutostartStateStore Reload() => new(NullLogger<AutostartStateStore>.Instance, Path);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
