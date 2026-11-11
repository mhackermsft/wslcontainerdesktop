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

using Microsoft.UI.Dispatching;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class LogStreamerSessionTests
{
    [Fact]
    public void NamedSessionIsAnArgumentNotACommandLineString()
    {
        var info = LogStreamer.CreateStartInfo("engine.exe", new WslcSessionTarget("SDK Session"),
            "container-id", 200, details: true);

        Assert.Equal("engine.exe", info.FileName);
        Assert.Equal(["--session", "SDK Session", "logs", "--details", "--timestamps", "-f",
            "--tail", "200", "container-id"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void DefaultSessionOmitsSelector()
    {
        var info = LogStreamer.CreateStartInfo("engine.exe", WslcSessionTarget.Default,
            "container-id", 50, details: false);

        Assert.Equal(["logs", "--timestamps", "-f", "--tail", "50", "container-id"], info.ArgumentList);
    }

    [Fact]
    public void QueuedLineFromStoppedStreamIsDiscarded()
    {
        var dispatcher = new DispatcherQueue();
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) =>
            throw new InvalidOperationException(method.Name));
        using var streamer = new LogStreamer(settings, dispatcher, new WslcSessionContext());
        var lines = new List<string>();
        streamer.LineReceived += lines.Add;

        streamer.Emit("old", 0);
        streamer.Stop();
        streamer.Emit("current", 1);
        dispatcher.Drain();

        Assert.Equal(["current"], lines);
    }
}
