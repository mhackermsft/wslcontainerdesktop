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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Covers the administrator-prompt watcher used while <c>wsl --update</c> runs, so the
/// "approve the prompt" guidance tracks the prompt and is always cleared when the update ends.
/// </summary>
public sealed class WslUpdateElevationTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task ReportsPromptOpenAndClosedAndRemembersItWasSeen()
    {
        var update = new TaskCompletionSource();
        var probes = 0;
        var transitions = new List<bool>();

        // Closed, then open for a few polls, then closed again before the update finishes.
        bool Probe()
        {
            var n = Interlocked.Increment(ref probes);
            if (n == 8)
            {
                update.TrySetResult();
            }

            return n is >= 3 and <= 5;
        }

        var seen = await WslUpdateElevation.WatchAsync(update.Task, transitions.Add, Probe, FastPoll);

        Assert.True(seen);
        Assert.Equal([true, false], transitions);
    }

    [Fact]
    public async Task ClearsAnOpenPromptWhenTheUpdateEnds()
    {
        var update = new TaskCompletionSource();
        var transitions = new List<bool>();

        bool Probe()
        {
            update.TrySetResult();
            return true;
        }

        var seen = await WslUpdateElevation.WatchAsync(update.Task, transitions.Add, Probe, FastPoll);

        Assert.True(seen);
        Assert.Equal([true, false], transitions);
    }

    [Fact]
    public async Task NoPromptMeansNoTransitions()
    {
        var transitions = new List<bool>();

        var seen = await WslUpdateElevation.WatchAsync(Task.CompletedTask, transitions.Add, () => true, FastPoll);

        Assert.False(seen);
        Assert.Empty(transitions);
    }

    [Fact]
    public async Task AFailedUpdateStillEndsTheWatch()
    {
        var transitions = new List<bool>();
        var update = Task.FromException(new InvalidOperationException("boom"));

        var seen = await WslUpdateElevation.WatchAsync(update, transitions.Add, () => false, FastPoll);

        Assert.False(seen);
        Assert.Empty(transitions);
    }
}
