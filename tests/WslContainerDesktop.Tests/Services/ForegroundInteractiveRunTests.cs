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

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ForegroundInteractiveRunTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void WaitsForTerminalInput_OnlyForInteractiveForeground(bool detached, bool interactive, bool expected) =>
        Assert.Equal(expected, new RunContainerOptions { Detached = detached, Interactive = interactive }.WaitsForTerminalInput());

    [Fact]
    public void Guard_IsNotPersistedWithRunProfiles()
    {
        var json = JsonSerializer.Serialize(new RunContainerOptions { Detached = false, Interactive = true });

        Assert.DoesNotContain(nameof(RunContainerOptions.WaitsForTerminalInput), json);
        Assert.DoesNotContain(nameof(RunContainerOptions.ForegroundInteractiveError), json);
        var roundTrip = JsonSerializer.Deserialize<RunContainerOptions>(json)!;
        Assert.False(roundTrip.Detached);
        Assert.True(roundTrip.Interactive);
    }

    [Theory]
    [InlineData("docker run -it --rm ubuntu bash", "bash", true)]
    [InlineData("docker run -i ubuntu", null, false)]
    [InlineData("docker run --interactive --tty ubuntu sh", "sh", false)]
    [InlineData("podman run -ti alpine", null, false)]
    public void Parser_RunsForegroundInteractiveImportsDetachedWithAWarning(
        string commandLine, string? command, bool removeOnExit)
    {
        var parsed = DockerRunParser.Parse(commandLine);

        var options = Assert.IsType<RunContainerOptions>(parsed.Options);
        Assert.True(options.Detached);
        Assert.True(options.Interactive);
        Assert.False(options.WaitsForTerminalInput());
        Assert.Equal(command, options.Command);
        Assert.Equal(removeOnExit, options.RemoveOnExit);
        var warning = Assert.Single(parsed.Warnings);
        Assert.Contains("'-i' without '-d'", warning);
        Assert.Contains("-d", options.ToArguments());
        Assert.Contains("-i", options.ToArguments());
    }

    [Theory]
    [InlineData("docker run -d -it nginx", true, true)]
    [InlineData("docker run --detach -i nginx", true, true)]
    [InlineData("docker run ubuntu echo hi", false, false)]
    [InlineData("docker run -t ubuntu", false, false)]
    [InlineData("docker run -d nginx", true, false)]
    public void Parser_LeavesEveryOtherCombinationAlone(string commandLine, bool detached, bool interactive)
    {
        var parsed = DockerRunParser.Parse(commandLine);

        var options = Assert.IsType<RunContainerOptions>(parsed.Options);
        // Plain foreground runs (e.g. one-shot `echo`) must stay in the foreground.
        Assert.Equal(detached, options.Detached);
        Assert.Equal(interactive, options.Interactive);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void Parser_KeepsUnrelatedWarningsAlongsideTheInteractiveOne()
    {
        var parsed = DockerRunParser.Parse("docker run -it --privileged --frobnicate ubuntu");

        Assert.Equal(2, parsed.Warnings.Count);
        Assert.Contains(parsed.Warnings, w => w.Contains("--frobnicate"));
        Assert.Contains(parsed.Warnings, w => w.Contains("'-i' without '-d'"));
    }
}
