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
/// Exercises parsing for <c>wslc events</c> lines so labels and free-text details cannot corrupt core fields.
/// </summary>
public sealed class WslcEventParserTests
{
    [Fact]
    public void ParsesRecordedContainerStopWithExitCode()
    {
        Assert.True(WslcEventParser.TryParseLine(
            "2026-09-30T10:12:30.1234567-04:00 container stop 1234567890abcdef (exitCode=137, image=busybox:latest, name=wslcd-test-events)",
            out var evt));

        Assert.Equal("container", evt.Type);
        Assert.Equal("stop", evt.Action);
        Assert.Equal("1234567890abcdef", evt.ActorId);
        Assert.Equal(137, evt.ExitCode);
        Assert.Equal("busybox:latest", evt.Image);
        Assert.Equal("wslcd-test-events", evt.DisplayName);
    }

    [Fact]
    public void ParsesRecordedNetworkLifecycleEvent()
    {
        Assert.True(WslcEventParser.TryParseLine(
            "2026-09-30T10:12:31.0000000-04:00 network connect abcdef123456 (container=wslcd-test-events, name=wslcd-test-net, type=bridge)",
            out var evt));

        Assert.Equal("network", evt.Type);
        Assert.Equal("connect", evt.Action);
        Assert.Equal("wslcd-test-net", evt.Network);
        Assert.Equal("wslcd-test-events", evt.ContainerId);
    }

    [Fact]
    public void AttributeValuesCanContainCommaEqualsAnglesAndParentheses()
    {
        Assert.True(WslcEventParser.TryParseLine(
            "2026-09-30T10:12:32.0000000-04:00 container create id123 (image=repo/app:1, label=a,b <c> (d=e), name=app,with,commas)",
            out var evt));

        Assert.Equal("repo/app:1", evt.Attributes["image"]);
        Assert.Equal("a,b <c> (d=e)", evt.Attributes["label"]);
        Assert.Equal("app,with,commas", evt.Attributes["name"]);
    }

    [Fact]
    public void LabelValueCannotOverrideAnEarlierSortedAttribute()
    {
        // Attributes arrive sorted by key; "name" inside the later label's value is not a boundary.
        Assert.True(WslcEventParser.TryParseLine(
            "2026-09-30T10:12:33.0000000-04:00 container start id123 (image=nginx, name=real, zlabel=x, name=evil, container=fake)",
            out var evt));

        Assert.Equal("real", evt.Attributes["name"]);
        Assert.Equal("x, name=evil, container=fake", evt.Attributes["zlabel"]);
        Assert.False(evt.Attributes.ContainsKey("container"));
    }

    [Fact]
    public void LabelSortedBeforeNameCannotSwallowRealAttributes()
    {
        Assert.True(WslcEventParser.TryParseLine(
            "2026-09-30T10:12:34.0000000-04:00 container stop id123 (com.x=y, name=evil, exitCode=137, image=busybox, name=real)",
            out var evt));

        Assert.Equal("real", evt.Attributes["name"]);
        Assert.Equal("137", evt.Attributes["exitCode"]);
        Assert.Equal("busybox", evt.Attributes["image"]);
        Assert.Equal("y, name=evil", evt.Attributes["com.x"]);
    }

    [Fact]
    public void RealEngineLineWithFreeTextLabelParses()
    {
        Assert.True(WslcEventParser.TryParseLine(
            "2026-09-30T10:18:26.000000000-04:00 container stop 5bdd1b4e0e78 (exitCode=0, image=nginx:alpine, maintainer=NGINX Docker Maintainers <docker-maint@nginx.com>, name=wslcd-evtest)",
            out var evt));

        Assert.Equal("wslcd-evtest", evt.Attributes["name"]);
        Assert.Equal("0", evt.Attributes["exitCode"]);
        Assert.Equal("NGINX Docker Maintainers <docker-maint@nginx.com>", evt.Attributes["maintainer"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an event")]
    [InlineData("2026-09-30T10:12:32.0000000-04:00 container create id123 (not-valid)")]
    public void UnparseableLinesAreSkipped(string line)
    {
        Assert.False(WslcEventParser.TryParseLine(line, out _));
    }
}
