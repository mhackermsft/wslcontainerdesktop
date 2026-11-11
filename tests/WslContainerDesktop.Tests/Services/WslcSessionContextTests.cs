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

using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcSessionContextTests
{
    [Fact]
    public void AddSessionSelector_UsesDefaultUnlessNamedSessionIsSelected()
    {
        var context = new WslcSessionContext();

        Assert.Equal(["list", "--format", "json"], context.AddSessionSelector(["list", "--format", "json"]));

        context.Select("SDK Session");
        Assert.Equal(["--session", "SDK Session", "list", "--format", "json"],
            context.AddSessionSelector(["list", "--format", "json"]));

        context.Select(null);
        Assert.Equal(["list"], context.AddSessionSelector(["list"]));
    }

    [Fact]
    public void Capture_KeepsTheOriginalTargetWhenSelectionChanges()
    {
        var context = new WslcSessionContext();
        context.Select("First session");
        var first = context.Capture();
        context.Select(null);
        var defaultTarget = context.Capture();
        context.Select("Second session");

        Assert.Equal(["--session", "First session", "list"], first.AddSessionSelector(["list"]));
        Assert.Equal(["list"], defaultTarget.AddSessionSelector(["list"]));
        Assert.Equal(["--session", "Second session", "list"], context.AddSessionSelector(["list"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Target_RejectsAmbiguousEmptyNamedSessions(string name)
    {
        Assert.Throws<ArgumentException>(() => new WslcSessionTarget(name));
    }

    [Fact]
    public void SelectionLeasesPreventRetargetingUntilEveryLeaseIsReleased()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var unlocked = 0;
        var changes = 0;
        context.SelectionUnlocked += (_, _) => unlocked++;
        context.SelectionLockChanged += (_, _) => changes++;
        var first = context.LockSelection();
        var second = context.LockSelection();
        Assert.Equal(1, changes);

        Assert.True(context.TrySelect("first"));
        Assert.False(context.TrySelect("second"));
        var settingChanged = false;
        Assert.False(context.TryApplyIfUnlocked(() => settingChanged = true));
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, unlocked);
        Assert.False(context.TrySelect("second"));
        Assert.Equal("first", context.SelectedSessionName);
        second.Dispose();
        Assert.Equal(1, unlocked);
        Assert.Equal(2, changes);
        Assert.True(context.TrySelect("second"));
        Assert.True(context.TryApplyIfUnlocked(() => settingChanged = true));
        Assert.True(settingChanged);
        Assert.Equal("second", context.SelectedSessionName);
    }

    [Fact]
    public void VersionedObservationIsRejectedEvenIfSelectionChangesBack()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var snapshot = context.CaptureSnapshot();
        var applied = false;
        context.Select("second");
        context.Select("first");

        Assert.False(context.TryApply(snapshot, () => applied = true));
        Assert.False(applied);
        Assert.True(context.TryApply(context.CaptureSnapshot(), () => applied = true));
        Assert.True(applied);
    }

    [Fact]
    public void EngineSettingChangeInvalidatesEarlierSnapshot()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var before = context.CaptureSnapshot();
        var changed = false;
        var notifications = 0;
        context.EngineSettingChanged += (_, _) => notifications++;

        Assert.True(context.TryApplyIfUnlocked(() => changed = true));

        Assert.True(changed);
        Assert.Equal(1, notifications);
        Assert.NotEqual(before, context.CaptureSnapshot());
        Assert.False(context.TryApply(before, () => throw new InvalidOperationException()));
        using (context.LockSelection())
            Assert.False(context.TryApplyIfUnlocked(() => throw new InvalidOperationException()));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ConditionalLeaseRejectsStaleSnapshotsAndPinsAcceptedSelection()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var old = context.CaptureSnapshot();
        context.Select("second");
        Assert.Null(context.TryLockSelection(old));

        var current = context.CaptureSnapshot();
        using (var lease = context.TryLockSelection(current))
        {
            Assert.NotNull(lease);
            Assert.False(context.TrySelect("another"));
            Assert.False(context.TryApplyIfUnlocked(() => { }));
        }
        Assert.True(context.TryApplyIfUnlocked(() => { }));
        Assert.Null(context.TryLockSelection(current));
        Assert.True(context.TrySelect("another"));
    }

    [Fact]
    public void SelectionChangedOnlyReportsActualAcceptedChanges()
    {
        var context = new WslcSessionContext();
        var observed = new List<string?>();
        context.SelectionChanged += (_, _) => observed.Add(context.SelectedSessionName);

        Assert.True(context.TrySelect(null));
        Assert.True(context.TrySelect("first"));
        Assert.True(context.TrySelect("first"));
        using (context.LockSelection())
            Assert.False(context.TrySelect("second"));
        Assert.True(context.TrySelect("FIRST"));
        Assert.True(context.TrySelect(null));

        Assert.Equal(["first", "FIRST", null], observed);
    }

    [Fact]
    public void DiscoveryRetainsMissingSelectionWithoutConfusingCaseOrDefault()
    {
        var context = new WslcSessionContext();
        context.Select("first");

        var missing = context.ReconcileDiscovery(["FIRST", "other", "other", null, " "]);
        Assert.Equal("first", missing.SelectedName);
        Assert.True(missing.SelectedMissing);
        Assert.Contains("first", missing.Names);
        Assert.Contains("FIRST", missing.Names);
        Assert.Equal("first", context.SelectedSessionName);

        var present = context.ReconcileDiscovery(["first", "FIRST"]);
        Assert.False(present.SelectedMissing);
        Assert.Equal(2, present.Names.Count);

        context.Select(null);
        var defaultSelection = context.ReconcileDiscovery([]);
        Assert.Null(defaultSelection.SelectedName);
        Assert.False(defaultSelection.SelectedMissing);
        Assert.Empty(defaultSelection.Names);
    }

    [Fact]
    public void IntentionalDefaultStopIsEngineScopedAndInvalidatesOldObservations()
    {
        var context = new WslcSessionContext();
        var snapshot = context.CaptureSnapshot();
        var notifications = 0;
        context.DefaultSessionStopChanged += (_, _) => notifications++;

        context.PauseDefaultSession(@"C:\engine\wslc.exe");

        Assert.True(context.IsDefaultSessionStopped(@"c:\ENGINE\wslc.exe", WslcSessionTarget.Default));
#if WINDOWS
        Assert.True(context.IsDefaultSessionStopped(@"C:\engine\wslc.exe",
            new WslcSessionTarget(WslcSessionActionGuard.ResolveDefaultSessionName())));
#endif
        Assert.False(context.IsDefaultSessionStopped(@"C:\engine\wslc.exe", new WslcSessionTarget("another")));
        Assert.False(context.IsDefaultSessionStopped(@"C:\other\wslc.exe", WslcSessionTarget.Default));
        Assert.False(context.TryApply(snapshot, () => throw new InvalidOperationException()));
        Assert.Throws<InvalidOperationException>(() => context.PauseDefaultSession(@"c:\ENGINE\wslc.exe"));

        context.ResumeDefaultSession(@"C:\engine\wslc.exe");

        Assert.False(context.IsDefaultSessionStopped(@"C:\engine\wslc.exe", WslcSessionTarget.Default));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void StoppingDefaultDoesNotInvalidateAnotherSelectedSession()
    {
        var context = new WslcSessionContext();
        context.Select("another");
        var snapshot = context.CaptureSnapshot();
        context.PauseDefaultSession("engine");

        Assert.True(context.TryApply(snapshot, () => { }));

        context.ResumeDefaultSession("engine");
        Assert.True(context.TryApply(snapshot, () => { }));
    }
}
