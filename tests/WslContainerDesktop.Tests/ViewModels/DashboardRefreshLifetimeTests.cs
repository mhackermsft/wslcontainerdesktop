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
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Tests.Services;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class DashboardRefreshLifetimeTests
{
    [Fact]
    public async Task IndexedRefreshRetainsLargeInventoriesWithoutChangingIdentityRules()
    {
        using var fixture = new Fixture { Block = false };
        fixture.Containers = Enumerable.Range(1, 1000).Select(i => new ContainerInfo
        {
            Id = $"container-{i}", Name = $"web-{i}", Image = "app", CreatedAt = i, CreatedAtKnown = true,
            StateValue = (int)ContainerState.Running,
        }).ToArray();
        await fixture.Dashboard.RefreshAsync();
        var original = fixture.Dashboard.LiveStats.ToArray();
        Assert.Equal(1000, original.Length);
        fixture.Executable = "WSLC";
        await fixture.Combined.RefreshAllAsync();

        Assert.True(fixture.Dashboard.SyncRunningRows());
        Assert.Equal(original, fixture.Dashboard.LiveStats);
        Assert.All(fixture.Dashboard.LiveStats, row => Assert.True(row.CanNavigate));
        fixture.Containers[0].CreatedAtKnown = false;
        await fixture.Combined.RefreshAllAsync();

        Assert.False(fixture.Dashboard.SyncRunningRows());
        Assert.NotSame(original[0], fixture.Dashboard.LiveStats[0]);
        Assert.Single(fixture.Dashboard.LiveStats, row => !row.CanNavigate);
    }

    [Fact]
    public void IndexedStatisticsKeepSessionsSeparateAndRejectDuplicateOrChangedSamples()
    {
        using var fixture = new Fixture();
        var alpha = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "alpha" };
        var beta = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "beta" };
        var container = new ContainerInfo
        {
            Id = "same", Name = "web", Image = "app", CreatedAt = 123, CreatedAtKnown = true,
        };
        var first = new DashboardStatRow { Session = alpha, Container = container, Id = "same" };
        var second = new DashboardStatRow { Session = beta, Container = container, Id = "same" };
        fixture.Dashboard.LiveStats.Add(first);
        fixture.Dashboard.LiveStats.Add(second);
        var sample = new DashboardContainerStats(container, new ContainerStats { CpuPercent = "12%" });
        fixture.Dashboard.ApplyStats([new(alpha, [sample]), new(beta, [sample])], CancellationToken.None);
        Assert.True(first.StatsAvailable);
        Assert.True(second.StatsAvailable);

        fixture.Dashboard.ApplyStats([new(alpha, [sample, sample]), new(beta, [sample])], CancellationToken.None);
        Assert.False(first.StatsAvailable);
        Assert.True(second.StatsAvailable);
        var changed = new ContainerInfo
        {
            Id = "same", Name = "web", Image = "app", CreatedAt = 124, CreatedAtKnown = true,
        };
        fixture.Dashboard.ApplyStats([new(alpha, [sample]), new(beta,
            [new(changed, new ContainerStats { CpuPercent = "99%" })])], CancellationToken.None);
        Assert.True(first.StatsAvailable);
        Assert.False(second.StatsAvailable);
    }

    [Fact]
    public async Task LeavingDashboardCancelsItsInFlightInventory()
    {
        using var fixture = new Fixture();
        fixture.Dashboard.StartStatsPolling();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Dashboard.StopStatsPolling();

        await fixture.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Combined.ContainerRows);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("display-off")]
    public async Task LosingVisibilityCancelsInventoryAndShowingWindowAllowsRefresh(string state)
    {
        using var fixture = new Fixture();
        var refresh = fixture.Dashboard.RefreshAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        switch (state)
        {
            case "hidden": fixture.Visibility.SetWindowState(false, false); break;
            case "minimized": fixture.Visibility.SetWindowState(true, true); break;
            case "display-off": fixture.Visibility.SetDisplayOn(false); break;
        }

        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Combined.IsBusy);
        Assert.Empty(fixture.Combined.ContainerRows);

        fixture.Block = false;
        fixture.Visibility.SetWindowState(true, false);
        fixture.Visibility.SetDisplayOn(true);
        await fixture.Dashboard.RefreshAsync();
        Assert.Single(fixture.Combined.ContainerRows);
    }

    [Fact]
    public async Task HidingDashboardDoesNotCancelAnotherPagesSharedScan()
    {
        using var fixture = new Fixture();
        var refresh = fixture.Dashboard.RefreshAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var otherPage = fixture.Combined.RefreshAllAsync();

        fixture.Visibility.SetWindowState(false, false);

        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Cancelled.Task.IsCompleted);
        Assert.True(fixture.Combined.IsBusy);
        fixture.Release.TrySetResult();
        await otherPage.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Combined.ContainerRows);
    }

    private sealed class Fixture : IDisposable
    {
        public WindowVisibility Visibility { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Idle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; } = true;
        public string Executable { get; set; } = "wslc";
        public IReadOnlyList<ContainerInfo> Containers { get; set; } = [new ContainerInfo { Id = "current", Name = "current" }];
        public CombinedSessionViewModel Combined { get; }
        public DashboardViewModel Dashboard { get; }

        public Fixture()
        {
            Visibility.SetWindowState(true, false);
            var session = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = [session] },
                }),
                nameof(IWslcService.GetVersionAsync) => Task.FromResult(new CommandResult { StandardOutput = "test" }),
                _ => throw new InvalidOperationException(method.Name),
            });
            var client = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => ContainersAsync((CancellationToken)args[1]!),
                nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                _ => throw new InvalidOperationException(method.Name),
            });
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => Executable,
                NullLogger<WslcSessionInventoryReader>.Instance);
            Combined = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
            Combined.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CombinedSessionViewModel.IsBusy) && !Combined.IsBusy &&
                    Started.Task.IsCompleted)
                    Idle.TrySetResult();
            };
            Dashboard = new DashboardViewModel(discovery, Visibility, NullLogger<DashboardViewModel>.Instance,
                Combined, new DashboardStatsReader(reader, (_, _) => client,
                    NullLogger<DashboardStatsReader>.Instance),
                new WslcSessionContainerActions(discovery, new WslcSessionContext(),
                    (_, _, _) => client, () => Executable, () => "default", (_, _) => { }, () => { }));
        }

        private async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(CancellationToken ct)
        {
            if (Block)
            {
                Started.TrySetResult();
                try { await Release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            return Containers;
        }

        public void Dispose()
        {
            Dashboard.StopStatsPolling();
            Combined.Dispose();
        }
    }
}
