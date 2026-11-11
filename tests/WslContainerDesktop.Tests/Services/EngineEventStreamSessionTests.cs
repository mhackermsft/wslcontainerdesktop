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

#if WINDOWS
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class EngineEventStreamSessionTests
{
    [Fact]
    public async Task IntentionalDefaultStopPausesReconnectWithoutStoppingAnotherSession()
    {
        using var stub = new WslcStub();
        var settings = DispatchProxy.Create<ISettingsService, WslcCapabilitiesServiceTests.SettingsProxy>();
        settings.WslcPath = stub.Quiet;
        var requirements = NetworkTestProxy.Create<IWslRequirementService>((method, _) => method.Name switch
        {
            "get_Current" => WslRequirementStatus.Ok("synthetic"),
            "add_Changed" or "remove_Changed" => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        var context = new WslcSessionContext();
        using var stream = new EngineEventStream(settings, requirements, action =>
        {
            action();
            return true;
        }, NullLogger<EngineEventStream>.Instance, context);

        stream.Start();
        await WaitForAsync(() => stub.Invocations(stub.Quiet).Length > 0);
        context.PauseDefaultSession(stub.Quiet);
        await Task.Delay(500);
        var stoppedCount = stub.Invocations(stub.Quiet).Length;
        await Task.Delay(2500);
        Assert.Equal(stoppedCount, stub.Invocations(stub.Quiet).Length);

        context.Select("another");
        stream.Restart();
        await WaitForAsync(() => stub.Invocations(stub.Quiet).Any(value => value == "--session another events"));
        context.Select(null);
        stream.Restart();
        await Task.Delay(500);
        stoppedCount = stub.Invocations(stub.Quiet).Length;
        await Task.Delay(2500);
        Assert.Equal(stoppedCount, stub.Invocations(stub.Quiet).Length);
        context.ResumeDefaultSession(stub.Quiet);
        await WaitForAsync(() => stub.Invocations(stub.Quiet).Length > stoppedCount);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!condition() && DateTimeOffset.UtcNow < until)
            await Task.Delay(100);
        Assert.True(condition());
    }

    [Fact]
    public void ReconnectWithinSameSessionDoesNotDiscardQueuedEvents()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var queued = new List<Action>();
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
        {
            "get_WslcPath" => "wslc",
            "add_Changed" or "remove_Changed" => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        var requirements = NetworkTestProxy.Create<IWslRequirementService>((method, _) => method.Name switch
        {
            "add_Changed" or "remove_Changed" => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        using var stream = new EngineEventStream(settings, requirements, action =>
        {
            queued.Add(action);
            return true;
        }, NullLogger<EngineEventStream>.Instance, context);
        var events = 0;
        EngineEvent? delivered = null;
        stream.EventReceived += (_, evt) => { events++; delivered = evt; };
        var accept = typeof(EngineEventStream).GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loop = typeof(EngineEventStream).GetField("_loop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        loop.SetValue(stream, Task.CompletedTask);
        accept.Invoke(stream, ["2026-09-30T10:12:30.1234567-04:00 container stop 1234567890abcdef (name=web)", 0L]);

        stream.Restart();
        queued[0]();

        Assert.Equal(1, events);
        Assert.Single(stream.RecentEvents);
        Assert.Equal("first", delivered!.SessionOwner!.SessionName);
        Assert.Equal("wslc", delivered.SessionOwner.ExecutablePath);
    }

    [Fact]
    public void SessionChangeDiscardsOldEventsAndDoesNotReuseAnotherSessionsCursor()
    {
        var context = new WslcSessionContext();
        context.Select("first");
        var queued = new List<Action>();
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
        {
            "get_WslcPath" => "wslc",
            "add_Changed" or "remove_Changed" => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        var requirements = NetworkTestProxy.Create<IWslRequirementService>((method, _) => method.Name switch
        {
            "add_Changed" or "remove_Changed" => null,
            _ => throw new InvalidOperationException(method.Name),
        });
        using var stream = new EngineEventStream(settings, requirements, action =>
        {
            queued.Add(action);
            return true;
        }, NullLogger<EngineEventStream>.Instance, context);
        var events = 0;
        EngineEvent? delivered = null;
        stream.EventReceived += (_, evt) => { events++; delivered = evt; };
        var create = typeof(EngineEventStream).GetMethod("CreateProcess", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var accept = typeof(EngineEventStream).GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = ((Process Process, long Generation))create.Invoke(stream, null)!;
        const string line = "2026-09-30T10:12:30.1234567-04:00 container stop 1234567890abcdef (name=web)";
        accept.Invoke(stream, [line, first.Generation]);
        Assert.Single(stream.RecentEvents);
        var reconnect = ((Process Process, long Generation))create.Invoke(stream, null)!;
        Assert.Contains("--since", reconnect.Process.StartInfo.ArgumentList);

        context.Select("second");
        var second = ((Process Process, long Generation))create.Invoke(stream, null)!;
        Assert.Empty(stream.RecentEvents);
        Assert.Equal(["--session", "second", "events"], second.Process.StartInfo.ArgumentList);
        foreach (var callback in queued.ToArray())
            callback();
        Assert.Equal(0, events);
        accept.Invoke(stream, [line, first.Generation]);
        Assert.Empty(stream.RecentEvents);
        accept.Invoke(stream, [line, second.Generation]);
        queued[^1]();
        Assert.Equal(1, events);
        Assert.Equal("second", delivered!.SessionOwner!.SessionName);
        first.Process.Dispose();
        reconnect.Process.Dispose();
        second.Process.Dispose();
    }
}
#endif
