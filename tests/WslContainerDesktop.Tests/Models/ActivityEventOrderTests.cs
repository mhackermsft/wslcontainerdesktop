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

namespace WslContainerDesktop.Tests.Models;

/// <summary>
/// The Activity timeline is newest-first by event time. Events can arrive late (the page back-fills
/// the last hour; the live stream replays after reconnecting), and inserting them at the top put a
/// 3:43 PM event above 4:05 PM ones.
/// </summary>
public sealed class ActivityEventOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    private static List<ActivityEvent> NewestFirst(params int[] minutes) =>
        minutes.Select(m => new ActivityEvent { Title = $"m{m}", Timestamp = T0.AddMinutes(m) }).ToList();

    [Fact]
    public void LiveEventGoesToTheTop() =>
        Assert.Equal(0, ActivityEvent.NewestFirstInsertIndex(NewestFirst(5, 3, 1), T0.AddMinutes(6)));

    [Fact]
    public void LateOlderEventGoesBelowNewerOnes() =>
        Assert.Equal(2, ActivityEvent.NewestFirstInsertIndex(NewestFirst(5, 3, 1), T0.AddMinutes(2)));

    [Fact]
    public void OldestEventGoesToTheEnd() =>
        Assert.Equal(3, ActivityEvent.NewestFirstInsertIndex(NewestFirst(5, 3, 1), T0));

    [Fact]
    public void EqualTimeGoesAboveExistingEntryOfThatTime() =>
        Assert.Equal(1, ActivityEvent.NewestFirstInsertIndex(NewestFirst(5, 3, 1), T0.AddMinutes(3)));

    [Fact]
    public void EmptyListInsertsAtZero() =>
        Assert.Equal(0, ActivityEvent.NewestFirstInsertIndex([], T0));
}
