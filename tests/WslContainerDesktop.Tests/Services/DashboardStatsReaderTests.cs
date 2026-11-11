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
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class DashboardStatsReaderTests
{
    private static readonly WslcSessionInfo Alpha = new() { ID = 1, CreatorPid = 10, Name = "alpha" };
    private static readonly WslcSessionInfo Beta = new() { ID = 2, CreatorPid = 20, Name = "beta" };
    private const string ShortId = "abcdef012345";
    private const string FullId = "abcdef012345aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task SameContainerIdsRemainScopedToTheirSessions()
    {
        var clients = new ConcurrentBag<string>();
        var reader = Create(() => [Alpha, Beta], session =>
        {
            clients.Add(session.Name);
            return Stats([Sample(session == Alpha ? "10%" : "30%")]);
        });

        var result = await reader.ReadAsync([Running(Alpha), Running(Beta)], "engine");

        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Null(item.Failure));
        Assert.Equal("10%", Assert.Single(result.Single(item => item.Session == Alpha).Containers).Stats!.CpuPercent);
        Assert.Equal("30%", Assert.Single(result.Single(item => item.Session == Beta).Containers).Stats!.CpuPercent);
        Assert.Equal(["alpha", "beta"], clients.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FailureInOneSessionDoesNotEraseAnotherSessionsStats()
    {
        var reader = Create(() => [Alpha, Beta], session => session == Alpha
            ? Stats([Sample("5%")])
            : StatsError(new IOException("engine failed")));

        var result = await reader.ReadAsync([Running(Alpha), Running(Beta)], "engine");

        Assert.NotNull(Assert.Single(result.Single(item => item.Session == Alpha).Containers).Stats);
        var failed = result.Single(item => item.Session == Beta);
        Assert.Equal(DashboardStatsFailure.ReadFailed, failed.Failure);
        Assert.Equal("engine failed", failed.Detail);
        Assert.Null(Assert.Single(failed.Containers).Stats);
    }

    [Fact]
    public async Task UnexpectedCancellationInOneSessionKeepsTheOtherSample()
    {
        var reader = Create(() => [Alpha, Beta], session => session == Alpha
            ? Stats([Sample("5%")])
            : StatsError(new OperationCanceledException("engine cancelled")));

        var result = await reader.ReadAsync([Running(Alpha), Running(Beta)], "engine");

        Assert.NotNull(Assert.Single(result.Single(item => item.Session == Alpha).Containers).Stats);
        Assert.Equal(DashboardStatsFailure.ReadFailed, result.Single(item => item.Session == Beta).Failure);
    }

    [Fact]
    public async Task ReplacedSessionGenerationIsRejectedAfterReading()
    {
        var discoveries = 0;
        var replacement = Beta with { ID = 3, CreatorPid = 30 };
        var reader = Create(() => ++discoveries == 1 ? [Alpha, Beta] : [Alpha, replacement],
            _ => Stats([Sample()]));

        var result = await reader.ReadAsync([Running(Alpha), Running(Beta)], "engine");

        Assert.Null(result.Single(item => item.Session == Alpha).Failure);
        var rejected = result.Single(item => item.Session == Beta);
        Assert.Equal(DashboardStatsFailure.SessionChanged, rejected.Failure);
        Assert.Null(Assert.Single(rejected.Containers).Stats);
    }

    [Fact]
    public async Task MissingAndAmbiguousStatsNeverAppearAsZero()
    {
        var reader = Create(() => [Alpha], _ => Stats([]));
        var missing = Assert.Single(await reader.ReadAsync([Running(Alpha)], "engine"));
        Assert.Equal(DashboardStatsFailure.Missing, missing.Failure);
        Assert.Null(Assert.Single(missing.Containers).Stats);

        var duplicate = Create(() => [Alpha], _ => Stats(
            [Sample(), Sample()]));
        var ambiguous = Assert.Single(await duplicate.ReadAsync([Running(Alpha)], "engine"));
        Assert.Equal(DashboardStatsFailure.IdentityUnknown, ambiguous.Failure);
        Assert.Null(Assert.Single(ambiguous.Containers).Stats);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData("-1%")]
    [InlineData("2%%")]
    public async Task InvalidPercentagesAreReportedRatherThanCountedAsZero(string value)
    {
        var reader = Create(() => [Alpha], _ => Stats([Sample(value)]));

        var result = Assert.Single(await reader.ReadAsync([Running(Alpha)], "engine"));

        Assert.Equal(DashboardStatsFailure.InvalidSample, result.Failure);
        Assert.Null(Assert.Single(result.Containers).Stats);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingPercentageFieldsAreNotReportedAsZero(bool missingCpu)
    {
        var json = missingCpu
            ? $"{{\"ID\":\"{FullId}\",\"MemPerc\":\"5%\"}}"
            : $"{{\"ID\":\"{FullId}\",\"CPUPerc\":\"5%\"}}";
        var sample = System.Text.Json.JsonSerializer.Deserialize<ContainerStats>(json)!;
        var reader = Create(() => [Alpha], _ => Stats([sample]));

        var result = Assert.Single(await reader.ReadAsync([Running(Alpha)], "engine"));

        Assert.Equal(DashboardStatsFailure.InvalidSample, result.Failure);
        Assert.Null(Assert.Single(result.Containers).Stats);
    }

    [Fact]
    public void PercentagesUseTheEngineDecimalFormatUnderAnotherSystemCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(1.5, new ContainerStats { CpuPercent = "1.5%" }.CpuValue);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public async Task UnavailableSessionTimesOutWithoutReturningFakeStats()
    {
        var reader = Create(() => [Alpha], _ => NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.GetStatsAsync)
                ? new TaskCompletionSource<IReadOnlyList<ContainerStats>>().Task
                : throw new InvalidOperationException(method.Name)), TimeSpan.FromMilliseconds(50));

        var result = Assert.Single(await reader.ReadAsync([Running(Alpha)], "engine"));

        Assert.Equal(DashboardStatsFailure.TimedOut, result.Failure);
        Assert.Null(Assert.Single(result.Containers).Stats);
    }

    [Fact]
    public async Task BatchTimeoutKeepsCompletedStatsAndMarksSlowOrQueuedSessions()
    {
        var gamma = new WslcSessionInfo { ID = 3, CreatorPid = 30, Name = "gamma" };
        var delta = new WslcSessionInfo { ID = 4, CreatorPid = 40, Name = "delta" };
        var reader = Create(() => [Alpha, Beta, gamma, delta], session => session == Alpha
            ? Stats([Sample("5%")])
            : NetworkTestProxy.Create<IWslcService>((method, _) =>
                method.Name == nameof(IWslcService.GetStatsAsync)
                    ? new TaskCompletionSource<IReadOnlyList<ContainerStats>>().Task
                    : throw new InvalidOperationException(method.Name)),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        var result = await reader.ReadAsync([Running(Alpha), Running(Beta), Running(gamma), Running(delta)], "engine");

        Assert.NotNull(Assert.Single(result.Single(item => item.Session == Alpha).Containers).Stats);
        Assert.All(result.Where(item => item.Session != Alpha), item =>
        {
            Assert.Equal(DashboardStatsFailure.TimedOut, item.Failure);
            Assert.Null(Assert.Single(item.Containers).Stats);
        });
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToASessionTimeout()
    {
        var reader = Create(() => [Alpha], _ => NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.GetStatsAsync)
                ? new TaskCompletionSource<IReadOnlyList<ContainerStats>>().Task
                : throw new InvalidOperationException(method.Name)), TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync([Running(Alpha)], "engine", cancellation.Token));
    }

    [Fact]
    public async Task NoRunningContainersDoesNotProbeSessions()
    {
        var reader = Create(() => throw new InvalidOperationException("unexpected discovery"),
            _ => throw new InvalidOperationException("unexpected stats read"));
        var stopped = Running(Alpha);
        stopped.Value.StateValue = (int)ContainerState.Stopped;

        Assert.Empty(await reader.ReadAsync([stopped], "engine"));
    }

    private static WslcCombinedInventory.Resource<ContainerInfo> Running(WslcSessionInfo session) =>
        new(session, new ContainerInfo { Id = ShortId, Name = "web", StateValue = (int)ContainerState.Running,
            CreatedAtKnown = true, CreatedAt = 123 });

    private static ContainerStats Sample(string cpu = "0%") =>
        new() { Id = FullId, Name = "web", CpuPercent = cpu, MemPercent = "0%" };

    private static DashboardStatsReader Create(Func<IReadOnlyList<WslcSessionInfo>> sessions,
        Func<WslcSessionInfo, IWslcService> client, TimeSpan? timeout = null, TimeSpan? batchTimeout = null)
    {
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.GetSystemInfoAsync)
                ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = sessions() } })
                : throw new InvalidOperationException(method.Name));
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => discovery, () => "engine",
            NullLogger<WslcSessionInventoryReader>.Instance);
        return new DashboardStatsReader(reader, (session, _) => client(session),
            NullLogger<DashboardStatsReader>.Instance, timeout, batchTimeout);
    }

    private static IWslcService Stats(IReadOnlyList<ContainerStats> stats) =>
        NetworkTestProxy.Create<IWslcService>((method, _) => method.Name == nameof(IWslcService.GetStatsAsync)
            ? Task.FromResult(stats) : throw new InvalidOperationException(method.Name));

    private static IWslcService StatsError(Exception error) =>
        NetworkTestProxy.Create<IWslcService>((method, _) => method.Name == nameof(IWslcService.GetStatsAsync)
            ? Task.FromException<IReadOnlyList<ContainerStats>>(error) : throw new InvalidOperationException(method.Name));
}
#endif
