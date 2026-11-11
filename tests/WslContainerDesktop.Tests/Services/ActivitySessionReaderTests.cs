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
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ActivitySessionReaderTests
{
    private static readonly WslcSessionInfo Default = new() { ID = 1, CreatorPid = 10, Name = "default-cli" };
    private static readonly WslcSessionInfo Alpha = new() { ID = 2, CreatorPid = 20, Name = "alpha" };
    private static readonly WslcSessionInfo Upper = new() { ID = 3, CreatorPid = 30, Name = "Alpha" };

    [Fact]
    public async Task ReadsEveryActiveSessionWithoutChangingOrStartingADefault()
    {
        var clients = new ConcurrentBag<string>();
        var reader = Create([Default, Alpha, Upper], choice =>
        {
            clients.Add(choice.Generation!.Name);
            return Events([new EngineEvent { ActorId = choice.Generation.Name }]);
        });

        var batch = await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default);

        Assert.Equal(3, batch.Sessions.Count);
        Assert.Equal(["Alpha", "alpha", "default-cli"], clients.Order(StringComparer.Ordinal));
        Assert.Null(batch.Sessions.Single(result => result.Selection.Generation == Default).Selection.Target.Name);
        Assert.All(batch.Sessions, result => Assert.Equal(result.Selection.Generation!.Name, Assert.Single(result.Events!).ActorId));
    }

    [Fact]
    public async Task NoActiveSessionsDoesNotCreateAClient()
    {
        var reader = Create([], _ => throw new InvalidOperationException("No session should be started."));
        Assert.Empty((await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default)).Sessions);
    }

    [Fact]
    public async Task FailedSessionDoesNotDiscardSuccessfulEvents()
    {
        var reader = Create([Alpha, Upper], choice => choice.Generation == Alpha
            ? Events([new EngineEvent { ActorId = "ok" }])
            : EventsError(new IOException("unavailable")));

        var batch = await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default);

        Assert.Equal("ok", Assert.Single(batch.Sessions.Single(result => result.Selection.Generation == Alpha).Events!).ActorId);
        var failed = batch.Sessions.Single(result => result.Selection.Generation == Upper);
        Assert.Null(failed.Events);
        Assert.Equal("unavailable", failed.Failure!.Message);
    }

    [Fact]
    public async Task ReplacedSessionDoesNotPublishItsRead()
    {
        var checks = 0;
        var reader = Create([Alpha], _ => Events([new EngineEvent()]),
            (_, _) => ++checks == 1 ? Task.CompletedTask : Task.FromException(new InvalidOperationException("changed")));

        var result = Assert.Single((await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default)).Sessions);

        Assert.Null(result.Events);
        Assert.Equal("changed", result.Failure!.Message);
    }

    [Fact]
    public async Task TimeoutKeepsOtherSessionEvents()
    {
        var reader = Create([Alpha, Upper], choice => choice.Generation == Alpha
            ? Events([new EngineEvent { ActorId = "ok" }])
            : EventsNeverComplete(), timeout: TimeSpan.FromMilliseconds(50));

        var batch = await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default);

        Assert.NotNull(batch.Sessions.Single(result => result.Selection.Generation == Alpha).Events);
        Assert.True(batch.Sessions.Single(result => result.Selection.Generation == Upper).TimedOut);
    }

    [Fact]
    public async Task BatchTimeoutMarksQueuedSessionsWithoutLosingCompletedResults()
    {
        var more = new WslcSessionInfo { ID = 4, CreatorPid = 40, Name = "other" };
        var reader = Create([Alpha, Upper, more], choice => choice.Generation == Alpha
            ? Events([new EngineEvent()])
            : EventsNeverComplete(), timeout: TimeSpan.FromSeconds(5), batchTimeout: TimeSpan.FromMilliseconds(80));

        var batch = await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default);

        Assert.NotNull(batch.Sessions.Single(result => result.Selection.Generation == Alpha).Events);
        Assert.All(batch.Sessions.Where(result => result.Selection.Generation != Alpha), result => Assert.True(result.TimedOut));
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        var reader = Create([Alpha], _ => EventsNeverComplete(), timeout: TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), stop.Token));
    }

    [Fact]
    public async Task EnginePathChangeRejectsTheEntireBatch()
    {
        var path = "engine";
        var reader = Create([Alpha], _ =>
        {
            path = "other";
            return Events([new EngineEvent()]);
        }, executable: () => path);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset>(), default));
    }

    [Fact]
    public async Task PreviousCursorIsUsedOnlyForTheSameSessionGeneration()
    {
        DateTimeOffset observed = default;
        var old = Alpha with { ID = 5, CreatorPid = 50 };
        var cursor = DateTimeOffset.UtcNow.AddMinutes(-5);
        var reader = Create([Alpha], _ => NetworkTestProxy.Create<IWslcService>((method, args) =>
        {
            Assert.Equal(nameof(IWslcService.GetEventsAsync), method.Name);
            observed = (DateTimeOffset)args![0]!;
            return Task.FromResult<IReadOnlyList<EngineEvent>>([]);
        }));

        await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset> { [old] = cursor }, default);
        Assert.True(observed < cursor.AddMinutes(-50));
        await reader.ReadAsync(new Dictionary<WslcSessionInfo, DateTimeOffset> { [Alpha] = cursor }, default);
        Assert.Equal(cursor.AddSeconds(-2), observed);
    }

    private static ActivitySessionReader Create(IReadOnlyList<WslcSessionInfo> sessions,
        Func<WslcSessionSelection, IWslcService> client,
        Func<WslcSessionSelection, CancellationToken, Task>? verify = null,
        Func<string>? executable = null, TimeSpan? timeout = null, TimeSpan? batchTimeout = null) =>
        new(_ => Task.FromResult(sessions), executable ?? (() => "engine"), () => "default-cli",
            verify ?? ((_, _) => Task.CompletedTask), client, NullLogger<ActivitySessionReader>.Instance,
            timeout, batchTimeout);

    private static IWslcService Events(IReadOnlyList<EngineEvent> events) =>
        NetworkTestProxy.Create<IWslcService>((method, _) => method.Name == nameof(IWslcService.GetEventsAsync)
            ? Task.FromResult(events) : throw new InvalidOperationException(method.Name));

    private static IWslcService EventsError(Exception error) =>
        NetworkTestProxy.Create<IWslcService>((method, _) => method.Name == nameof(IWslcService.GetEventsAsync)
            ? Task.FromException<IReadOnlyList<EngineEvent>>(error) : throw new InvalidOperationException(method.Name));

    private static IWslcService EventsNeverComplete() =>
        NetworkTestProxy.Create<IWslcService>((method, _) => method.Name == nameof(IWslcService.GetEventsAsync)
            ? new TaskCompletionSource<IReadOnlyList<EngineEvent>>().Task : throw new InvalidOperationException(method.Name));
}
#endif
