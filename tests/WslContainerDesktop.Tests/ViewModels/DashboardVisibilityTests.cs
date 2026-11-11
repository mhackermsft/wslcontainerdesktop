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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class DashboardVisibilityTests
{
    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(100, 5)]
    public void SamplingFrequencyScalesWithReportingSessions(int sessions, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), DashboardStatsReader.PollingDelay(sessions));

    [Theory]
    [InlineData("hidden", false)]
    [InlineData("minimized", false)]
    [InlineData("display-off", false)]
    [InlineData("hidden", true)]
    [InlineData("minimized", true)]
    [InlineData("display-off", true)]
    public async Task VisibilityLossCancelsActiveAndQueuedReadsAndResumesWithoutAnError(string state, bool gpu)
    {
        using var fixture = new Fixture(gpu);
        fixture.Dashboard.StartStatsPolling();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Run(() =>
        {
            switch (state)
            {
                case "hidden": fixture.Visibility.SetWindowState(false, false); break;
                case "minimized": fixture.Visibility.SetWindowState(true, true); break;
                case "display-off": fixture.Visibility.SetDisplayOn(false); break;
            }
        });

        await fixture.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var statsReads = fixture.StatsReads;
        var discoveries = fixture.Discoveries;
        var inventoryReads = fixture.InventoryReads;
        await Task.Delay(TimeSpan.FromMilliseconds(2200));
        Assert.Equal(fixture.Concurrency, fixture.Reads);
        Assert.Equal(statsReads, fixture.StatsReads);
        Assert.Equal(discoveries, fixture.Discoveries);
        Assert.Equal(inventoryReads, fixture.InventoryReads);
        Assert.DoesNotContain("stopped:", fixture.Dashboard.StatsWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed", fixture.Dashboard.StatsWarning, StringComparison.OrdinalIgnoreCase);
        if (gpu)
            Assert.All(fixture.Dashboard.LiveStats, row => Assert.False(row.GpuChecked));

        fixture.Visibility.SetWindowState(true, false);
        fixture.Visibility.SetDisplayOn(true);

        await fixture.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Discoveries > discoveries);
        Assert.True(fixture.InventoryReads > inventoryReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingPageCancelsActiveAndQueuedStatisticsOrGpuReads(bool gpu)
    {
        using var fixture = new Fixture(gpu);
        fixture.Dashboard.StartStatsPolling();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Dashboard.StopStatsPolling();

        await fixture.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(fixture.Concurrency, fixture.Reads);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _gpu;
        private int _reads;
        private int _cancellations;
        private int _statsReads;
        private int _discoveries;
        private int _inventoryReads;
        public int Reads => Volatile.Read(ref _reads);
        public int StatsReads => Volatile.Read(ref _statsReads);
        public int Discoveries => Volatile.Read(ref _discoveries);
        public int InventoryReads => Volatile.Read(ref _inventoryReads);
        public int Concurrency => _gpu ? 4 : 2;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WindowVisibility Visibility { get; } = new();
        public DashboardViewModel Dashboard { get; }
        private readonly CombinedSessionViewModel _combined;

        public Fixture(bool gpu)
        {
            _gpu = gpu;
            Visibility.SetWindowState(true, false);
            var sessions = Enumerable.Range(1, gpu ? 1 : 3).Select(i =>
                new WslcSessionInfo { ID = i, CreatorPid = 100 + i, Name = $"session-{i}" }).ToArray();
            var containers = Enumerable.Range(1, gpu ? 5 : 1).Select(i => new ContainerInfo
            {
                Id = $"123456789ab{i}", Name = $"web-{i}", CreatedAtKnown = true, CreatedAt = 123,
                StateValue = (int)ContainerState.Running,
            }).ToArray();
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => DiscoverAsync(),
                nameof(IWslcService.GetVersionAsync) => Task.FromResult(new CommandResult()),
                _ => throw new InvalidOperationException(method.Name),
            });
            Task<WslcSystemInfo> DiscoverAsync()
            {
                Interlocked.Increment(ref _discoveries);
                return Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = sessions } });
            }
            var client = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => ContainersAsync(),
                nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                nameof(IWslcService.GetStatsAsync) => StatsAsync((CancellationToken)args[0]!),
                nameof(IWslcService.GetGpuInfoAsync) => GpuAsync((CancellationToken)args[1]!),
                _ => throw new InvalidOperationException(method.Name),
            });
            Task<IReadOnlyList<ContainerInfo>> ContainersAsync()
            {
                Interlocked.Increment(ref _inventoryReads);
                return Task.FromResult<IReadOnlyList<ContainerInfo>>(containers);
            }
            async Task<IReadOnlyList<ContainerStats>> StatsAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref _statsReads);
                if (!gpu) await ReadAsync(ct);
                return containers.Select(container => new ContainerStats
                {
                    Id = container.Id, CpuPercent = "0%", MemPercent = "1%",
                }).ToArray();
            }
            async Task<(bool HasGpu, string? GpuName)> GpuAsync(CancellationToken ct)
            {
                await ReadAsync(ct);
                return (false, null);
            }
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => "wslc",
                NullLogger<WslcSessionInventoryReader>.Instance);
            _combined = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
            var actions = new WslcSessionContainerActions(discovery, new WslcSessionContext(),
                (_, _, _) => client, () => "wslc", () => "default", (_, _) => { }, () => { });
            Dashboard = new DashboardViewModel(discovery, Visibility, NullLogger<DashboardViewModel>.Instance,
                _combined, new DashboardStatsReader(reader, (_, _) => client,
                    NullLogger<DashboardStatsReader>.Instance), actions);
        }

        private async Task ReadAsync(CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _reads);
            if (count == Concurrency) Started.TrySetResult();
            if (count > Concurrency) Resumed.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (Interlocked.Increment(ref _cancellations) == Concurrency) Cancelled.TrySetResult();
                throw;
            }
        }

        public void Dispose()
        {
            Dashboard.StopStatsPolling();
            _combined.Dispose();
        }
    }
}
