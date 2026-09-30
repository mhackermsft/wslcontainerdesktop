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

using WslContainerDesktop.Helpers;
using Xunit;

namespace WslContainerDesktop.Tests.Helpers;

/// <summary>Covers ordering of replayed container logs, whose stdout and stderr arrive on separate pipes.</summary>
public sealed class LogLineOrderTests
{
    [Fact]
    public void OrderByTimestamp_RestoresWriteOrderAcrossStdoutAndStderr()
    {
        // As read from two pipes: the stdout backlog first, then the stderr backlog.
        var arrived = new[]
        {
            "2026-09-30T20:52:51.444044637Z GET /missing 404",
            "2026-09-30T20:52:51.461359250Z GET / 200",
            "2026-09-30T19:35:11.100000000Z start worker process 30",
            "2026-09-30T20:52:51.444029632Z open() \"/missing\" failed",
        };

        var ordered = LogLineOrder.OrderByTimestamp(arrived).Select(LogLineOrder.StripTimestamp).ToList();

        Assert.Equal(
            ["start worker process 30", "open() \"/missing\" failed", "GET /missing 404", "GET / 200"],
            ordered);
    }

    [Fact]
    public void OrderByTimestamp_ComparesTrimmedFractionsNumerically()
    {
        // RFC 3339 "nano" output drops trailing zeros, so ".4Z" must sort before ".44Z".
        var ordered = LogLineOrder.OrderByTimestamp(
        [
            "2026-01-01T00:00:00.44Z b",
            "2026-01-01T00:00:00.4Z a",
            "2026-01-01T00:00:01Z c",
        ]);

        Assert.Equal(["2026-01-01T00:00:00.4Z a", "2026-01-01T00:00:00.44Z b", "2026-01-01T00:00:01Z c"], ordered);
    }

    [Fact]
    public void OrderByTimestamp_KeepsEqualTimesAndUntimedLinesInArrivalOrder()
    {
        var ordered = LogLineOrder.OrderByTimestamp(
        [
            "2026-01-01T00:00:02Z second",
            "continuation of second",
            "2026-01-01T00:00:01Z first",
            "2026-01-01T00:00:02Z third",
        ]);

        Assert.Equal(
            ["2026-01-01T00:00:01Z first", "2026-01-01T00:00:02Z second", "continuation of second", "2026-01-01T00:00:02Z third"],
            ordered);
    }

    [Theory]
    [InlineData("2026-09-30T20:52:51.444044637Z hello world", "hello world")]
    [InlineData("2026-09-30T20:52:51Z hello", "hello")]
    [InlineData("2026-09-30T20:52:51.5+02:00 hello", "hello")]
    [InlineData("2026-09-30T20:52:51Z", "")]
    [InlineData("2026/09/30 20:52:51 [notice] not an engine timestamp", "2026/09/30 20:52:51 [notice] not an engine timestamp")]
    [InlineData("plain line", "plain line")]
    [InlineData("", "")]
    public void StripTimestamp_RemovesOnlyTheEnginePrefix(string line, string expected)
    {
        Assert.Equal(expected, LogLineOrder.StripTimestamp(line));
    }

    [Fact]
    public void TrySplitTimestamp_ParsesOffsetsAndNanoseconds()
    {
        Assert.True(LogLineOrder.TrySplitTimestamp("2026-09-30T22:52:51.1234567+02:00 x", out var withOffset, out _));
        Assert.True(LogLineOrder.TrySplitTimestamp("2026-09-30T20:52:51.123456789Z x", out var utc, out _));

        Assert.Equal(utc.UtcTicks, withOffset.UtcTicks);
    }
}
