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

using WslContainerDesktop.Services;
using WslContainerDesktop.Helpers;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// UI-only polling (live stats, Kubernetes lists and footer) waits on this, so the app does no such
/// work while it sits in the tray, is minimized, or the display is off.
/// </summary>
public sealed class WindowVisibilityTests
{
    [Theory]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("display-off")]
    public async Task UiReadCancelsActiveAndQueuedWorkWhenVisibilityChangesOnAnotherThread(string state)
    {
        var visibility = new WindowVisibility();
        visibility.SetWindowState(true, false);
        using var lifetime = new CancellationTokenSource();
        using var read = new VisibleRead(visibility, lifetime.Token);
        var active = Task.Delay(Timeout.InfiniteTimeSpan, read.Token);
        using var gate = new SemaphoreSlim(0);
        var queued = gate.WaitAsync(read.Token);
        await Task.Run(() =>
        {
            if (state == "display-off") visibility.SetDisplayOn(false);
            else visibility.SetWindowState(state != "hidden", state == "minimized");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(lifetime.IsCancellationRequested);
        visibility.SetWindowState(true, false);
        visibility.SetDisplayOn(true);
        using var resumed = new VisibleRead(visibility, lifetime.Token);
        Assert.False(resumed.IsCancellationRequested);
        lifetime.Cancel();
        Assert.True(resumed.IsCancellationRequested);
    }

    [Fact]
    public void StartsNotViewableBecauseTheAppCanStartInTheTray()
    {
        var visibility = new WindowVisibility();

        Assert.False(visibility.IsViewable);
        Assert.False(visibility.WhenViewableAsync().IsCompleted);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void ViewableOnlyWhenShownNotMinimizedAndTheDisplayIsOn(bool shown, bool minimized, bool displayOn, bool expected)
    {
        var visibility = new WindowVisibility();
        visibility.SetWindowState(shown, minimized);
        visibility.SetDisplayOn(displayOn);

        Assert.Equal(expected, visibility.IsViewable);
    }

    [Fact]
    public async Task WaitersResumeWhenTheWindowIsShown()
    {
        var visibility = new WindowVisibility();
        var waiting = visibility.WhenViewableAsync();

        visibility.SetWindowState(shown: true, minimized: false);

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(visibility.WhenViewableAsync().IsCompleted);
    }

    [Fact]
    public void WaitingStartsAgainWhenTheDisplayTurnsOff()
    {
        var visibility = new WindowVisibility();
        visibility.SetWindowState(true, false);
        Assert.True(visibility.WhenViewableAsync().IsCompleted);

        visibility.SetDisplayOn(false);

        Assert.False(visibility.WhenViewableAsync().IsCompleted);
    }

    [Fact]
    public async Task WaitingHonorsCancellation()
    {
        var visibility = new WindowVisibility();
        using var cts = new CancellationTokenSource();
        var waiting = visibility.WhenViewableAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenViewabilityChanges()
    {
        var visibility = new WindowVisibility();
        var changes = 0;
        visibility.Changed += (_, _) => changes++;

        visibility.SetWindowState(true, false);
        visibility.SetWindowState(true, false);
        visibility.SetDisplayOn(true);
        visibility.SetWindowState(true, true);
        visibility.SetWindowState(true, false);

        Assert.Equal(3, changes);
    }
}
