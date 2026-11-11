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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

[Collection("UiText")]
public sealed class CombinedSessionViewModelTests
{
    private static readonly WslcSessionInfo First = new() { ID = 1, CreatorPid = 100, Name = "first" };
    private static readonly WslcSessionInfo Second = new() { ID = 2, CreatorPid = 200, Name = "second" };

    [Fact]
    public async Task DashboardRefreshReadsOnlyContainersAndKeepsMetadataUntilFullRefresh()
    {
        var calls = new List<string>();
        var image = new ImageInfo { Id = "image" };
        var client = NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            calls.Add(method.Name);
            return method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    [new ContainerInfo { Id = "current", Name = "current" }]),
                nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([image]),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                _ => throw new InvalidOperationException(method.Name),
            };
        });
        using var vm = new CombinedSessionViewModel(CreateReader(() => [First], _ => client),
            NullLogger<CombinedSessionViewModel>.Instance);
        await vm.RefreshAllAsync();
        var observedAt = vm.OldestRead;
        var imageRows = vm.ImageRows;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        calls.Clear();

        await vm.RefreshDashboardAsync(CancellationToken.None);

        Assert.Equal([nameof(IWslcService.ListContainersAsync)], calls);
        Assert.Same(image, Assert.Single(vm.Inventory.Images).Value);
        Assert.Equal(observedAt, vm.OldestRead);
        Assert.Same(imageRows, vm.ImageRows);
        Assert.DoesNotContain(nameof(CombinedSessionViewModel.ImageRows), notifications);
        calls.Clear();
        await vm.RefreshForNavigationAsync();
        Assert.Empty(calls);

        await vm.RefreshAllAsync();
        Assert.Equal(4, calls.Count);
    }

    [Fact]
    public async Task ReusingMetadataDoesNotRetainRowsFromAnUnavailableSession()
    {
        IReadOnlyList<WslcSessionInfo> active = [First];
        var client = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
            nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([new ImageInfo { Id = "image" }]),
            nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
            nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
            _ => throw new InvalidOperationException(method.Name),
        });
        using var vm = new CombinedSessionViewModel(CreateReader(() => active, _ => client),
            NullLogger<CombinedSessionViewModel>.Instance);
        await vm.RefreshAllAsync();
        var old = Assert.Single(vm.ImageRows);
        active = [];
        await vm.RefreshDashboardAsync(CancellationToken.None);
        Assert.Empty(vm.ImageRows);
        Assert.False(vm.IsCurrentRow(old));
    }

    [Fact]
    public async Task NavigationDoesNotReuseAnIncompleteInventory()
    {
        var reads = 0;
        var client = NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            Interlocked.Increment(ref reads);
            return method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
                nameof(IWslcService.ListImagesAsync) => Task.FromException<IReadOnlyList<ImageInfo>>(new IOException("unavailable")),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                _ => throw new InvalidOperationException(method.Name),
            };
        });
        using var vm = new CombinedSessionViewModel(CreateReader(() => [First], _ => client),
            NullLogger<CombinedSessionViewModel>.Instance);
        await vm.RefreshAllAsync();
        await vm.RefreshForNavigationAsync();
        Assert.Equal(8, reads);
    }

    [Fact]
    public async Task NavigationReloadsWhenAnAvailableSessionInventoryWasInvalidated()
    {
        var reads = 0;
        var reader = CreateReader(() => [First, Second], session =>
            NetworkTestProxy.Create<IWslcService>((method, _) =>
            {
                Interlocked.Increment(ref reads);
                return method.Name switch
                {
                    nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]),
                    nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
                    nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                    nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                    _ => throw new InvalidOperationException(method.Name),
                };
            }));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.RefreshAllAsync();
        Assert.Equal(8, reads);

        vm.InvalidateSession(Second);
        await vm.RefreshForNavigationAsync();

        Assert.Equal(16, reads);
        Assert.Equal(2, vm.Sessions.Count);
        Assert.True(vm.Inventory.IsComplete);
    }

    [Fact]
    public async Task DashboardLoadsFullMetadataForANewSessionGeneration()
    {
        var session = First;
        var calls = new List<string>();
        var reader = CreateReader(() => [session], current =>
        {
            calls.Add(current.Name);
            return Inventory(current.Name);
        });
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.RefreshAllAsync();
        session = First with { ID = 9 };

        await vm.RefreshDashboardAsync(CancellationToken.None);

        Assert.Equal(session, Assert.Single(vm.Inventory.Containers).Session);
        Assert.True(vm.HasImageData);
        Assert.True(vm.Inventory.IsComplete);
    }

    [Fact]
    public async Task FullConsumerJoiningContainerOnlyReadStillGetsFullMetadata()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        var imageReads = 0;
        var client = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => ContainersAsync((CancellationToken)args[1]!),
            nameof(IWslcService.ListImagesAsync) => ImagesAsync(),
            nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
            nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
            _ => throw new InvalidOperationException(method.Name),
        });
        async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(CancellationToken ct)
        {
            if (block)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return [];
        }
        Task<IReadOnlyList<ImageInfo>> ImagesAsync()
        {
            Interlocked.Increment(ref imageReads);
            return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
        }
        using var vm = new CombinedSessionViewModel(CreateReader(() => [First], _ => client),
            NullLogger<CombinedSessionViewModel>.Instance);
        await vm.RefreshAllAsync();
        block = true;
        var narrow = vm.RefreshDashboardAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var full = vm.RefreshAllAsync();
        release.TrySetResult();
        await Task.WhenAll(narrow, full).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, imageReads);
    }

    [Fact]
    public async Task DashboardCompletenessDistinguishesPartialAndUnavailableResources()
    {
        IWslcService Client(WslcSessionInfo session) => NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => session == First
                    ? Task.FromResult<IReadOnlyList<ContainerInfo>>([new ContainerInfo { Id = "first", Name = "web" }])
                    : Task.FromException<IReadOnlyList<ContainerInfo>>(new IOException("unavailable")),
                nameof(IWslcService.ListImagesAsync) => Task.FromException<IReadOnlyList<ImageInfo>>(new IOException("unavailable")),
                nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
                nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
                _ => throw new InvalidOperationException(method.Name),
            });
        var reader = CreateReader(() => [First, Second], Client);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        Assert.False(vm.HasSuccessfulDiscovery);
        Assert.False(vm.HasContainerData);

        await vm.RefreshAllAsync();

        Assert.True(vm.HasSuccessfulDiscovery);
        Assert.True(vm.HasContainerData);
        Assert.False(vm.HasCompleteContainerInventory);
        Assert.False(vm.HasImageData);
        Assert.True(vm.HasVolumeData);
        Assert.Single(vm.Inventory.Containers);
        Assert.Contains("incomplete", vm.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DashboardTreatsSuccessfulEmptyDiscoveryAsZero()
    {
        var reader = CreateReader(() => [], _ => throw new InvalidOperationException("No session should be read."));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);

        await vm.RefreshAllAsync();

        Assert.True(vm.HasSuccessfulDiscovery);
        Assert.True(vm.HasContainerData);
        Assert.True(vm.HasCompleteContainerInventory);
        Assert.True(vm.HasImageData);
        Assert.True(vm.HasVolumeData);
        Assert.Empty(vm.Inventory.Containers);
    }

    [Fact]
    public async Task ResourcePageRefreshLoadsEveryActiveSessionWithoutChangingManagement()
    {
        var management = new WslcSessionContext();
        management.Select("managed");
        IReadOnlyList<WslcSessionInfo> active = [First];
        var reads = new List<string>();
        var reader = CreateReader(() => active, session =>
        {
            reads.Add(session.Name);
            return Inventory(session.Name);
        });
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, management);

        await vm.RefreshAllAsync();
        active = [First, Second];
        await vm.RefreshAllAsync();

        Assert.Equal(["first", "first", "second"], reads);
        Assert.Equal(2, vm.ContainerRows.Count);
        Assert.Equal("managed", management.SelectedSessionName);
    }

    [Fact]
    public async Task MonitorUpdatesOnlyTheLoadedManagementSession()
    {
        var management = new WslcSessionContext();
        management.Select("first");
        var reader = CreateReader(() => [First, Second], session => Inventory(session.Name));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, management);
        await vm.RefreshAllAsync();

        vm.UpdateManagedContainers(First, "wslc", [new ContainerInfo { Id = "updated", Name = "updated" }]);

        Assert.Contains(vm.Inventory.Containers, row => row.Session == First && row.Value.Name == "updated");
        Assert.Contains(vm.Inventory.Containers, row => row.Session == Second && row.Value.Name == "second");
        Assert.Equal("first", management.SelectedSessionName);
    }

    [Fact]
    public async Task RepeatedDeliveryOfSameMonitorSnapshotPreservesRows()
    {
        var management = new WslcSessionContext();
        management.Select("first");
        var reader = CreateReader(() => [First], session => Inventory(session.Name));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, management);
        await vm.RefreshAllAsync();
        IReadOnlyList<ContainerInfo> snapshot = [new ContainerInfo { Id = "updated", Name = "updated" }];

        vm.UpdateManagedContainers(First, "wslc", snapshot);
        var row = Assert.Single(vm.ContainerRows);
        vm.UpdateManagedContainers(First, "wslc", snapshot);

        Assert.Same(row, Assert.Single(vm.ContainerRows));
        Assert.True(vm.IsCurrentRow(row));
    }

    [Theory]
    [InlineData(2, 100)]
    [InlineData(1, 200)]
    public async Task ReplacementSessionSnapshotInvalidatesEveryOldResource(int id, int creatorPid)
    {
        var management = new WslcSessionContext();
        management.Select("first");
        WslcSessionInfo active = First;
        var reader = CreateReader(() => [active], session => Inventory(session.Name));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, management);
        await vm.RefreshAllAsync();
        var old = Assert.Single(vm.ContainerRows);
        active = First with { ID = id, CreatorPid = creatorPid };

        vm.UpdateManagedContainers(active, "wslc", [new ContainerInfo { Id = "replacement", Name = "new" }]);

        Assert.Empty(vm.ContainerRows);
        Assert.Empty(vm.ImageRows);
        Assert.False(vm.IsCurrentRow(old));
        Assert.False(Assert.Single(vm.Sessions).IsAvailable);
        Assert.Contains("changed", vm.WarningText, StringComparison.OrdinalIgnoreCase);
        await vm.RefreshAllAsync();
        Assert.Equal(active, Assert.Single(vm.ContainerRows).Session);
    }

    [Fact]
    public async Task MonitorSnapshotFromAnotherExecutableIsIgnored()
    {
        var management = new WslcSessionContext();
        var reader = CreateReader(() => [First], session => Inventory(session.Name));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, management);
        await vm.RefreshAllAsync();
        var old = Assert.Single(vm.ContainerRows);

        vm.UpdateManagedContainers(First, "other", []);

        Assert.Same(old, Assert.Single(vm.ContainerRows));
    }

    [Fact]
    public async Task CancellingTheLastRefreshConsumerCancelsPendingReadsAndAllowsRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = true;
        var reader = CreateReader(() => [First], session => Inventory(session.Name, async ct =>
        {
            if (block)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }
            return [new ContainerInfo { Id = "current", Name = "current" }];
        }));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        using var lifetime = new CancellationTokenSource();
        var refresh = vm.RefreshAllAsync(lifetime.Token);
        await started.Task;

        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.ContainerRows);
        block = false;
        await vm.RefreshAllAsync();
        Assert.Single(vm.ContainerRows);
    }

    [Fact]
    public async Task CancellingOneConsumerDoesNotCancelAnotherPagesRefresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken readToken = default;
        var reader = CreateReader(() => [First], session => Inventory(session.Name, async ct =>
        {
            readToken = ct;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return [new ContainerInfo { Id = "current", Name = "current" }];
        }));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        using var lifetime = new CancellationTokenSource();
        var dashboard = vm.RefreshAllAsync(lifetime.Token);
        await started.Task;
        var otherPage = vm.RefreshAllAsync();

        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dashboard);
        Assert.False(readToken.IsCancellationRequested);
        Assert.True(vm.IsBusy);
        release.SetResult();
        await otherPage;
        Assert.Single(vm.ContainerRows);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CancelledRefreshWaitingForAnotherReadDoesNotCancelThatReadOrStartItsOwn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveries = 0;
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => DiscoverAsync(),
            _ => throw new InvalidOperationException(method.Name),
        });
        async Task<WslcSystemInfo> DiscoverAsync()
        {
            discoveries++;
            started.TrySetResult();
            await release.Task;
            return new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } };
        }
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => Inventory("first"),
            () => "wslc", NullLogger<WslcSessionInventoryReader>.Instance);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        var existing = vm.DiscoverAsync();
        await started.Task;
        using var lifetime = new CancellationTokenSource();
        var queued = vm.RefreshAllAsync(lifetime.Token);

        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(existing.IsCompleted);
        release.SetResult();
        await existing;
        Assert.Equal(1, discoveries);
        Assert.Single(vm.Sessions);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CancellingRefreshDuringDiscoveryDoesNotReadResources()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WslcSystemInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => BeginDiscovery(),
            _ => throw new InvalidOperationException(method.Name),
        });
        Task<WslcSystemInfo> BeginDiscovery()
        {
            started.TrySetResult();
            return release.Task;
        }
        var reads = 0;
        var reader = new WslcSessionInventoryReader(discovery, (_, _) =>
        {
            reads++;
            return Inventory("first");
        }, () => "wslc", NullLogger<WslcSessionInventoryReader>.Instance);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        using var lifetime = new CancellationTokenSource();
        var refresh = vm.RefreshAllAsync(lifetime.Token);
        await started.Task;

        lifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        release.SetResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } });
        Assert.Equal(0, reads);
        Assert.Empty(vm.Sessions);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ResourceRefreshWaitsForAnInFlightDiscovery()
    {
        var firstDiscovery = new TaskCompletionSource<WslcSystemInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryCalls = 0;
        var reads = 0;
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => ++discoveryCalls == 1
                ? firstDiscovery.Task
                : Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } }),
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = new WslcSessionInventoryReader(discovery, (session, _) =>
        {
            reads++;
            return Inventory(session.Name);
        }, () => "wslc", NullLogger<WslcSessionInventoryReader>.Instance);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);

        var discover = vm.DiscoverAsync();
        var refresh = vm.RefreshAllAsync();
        Assert.False(refresh.IsCompleted);
        firstDiscovery.SetResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } });
        await Task.WhenAll(discover, refresh);

        Assert.Equal(1, reads);
        Assert.Single(vm.ContainerRows);
    }

    [Fact]
    public async Task DiscoveryDoesNotReadResourcesAndFilteringDoesNotChangeManagementScope()
    {
        var management = new WslcSessionContext();
        management.Select("managed");
        var reads = new List<string>();
        var reader = CreateReader(() => [First, Second], session =>
        {
            reads.Add(session.Name);
            return Inventory(session.Name);
        });
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);

        await vm.DiscoverAsync();
        Assert.Empty(reads);
        vm.Sessions[1].IsSelected = false;
        await vm.LoadAsync();

        Assert.Equal(["first"], reads);
        Assert.Equal("managed", management.SelectedSessionName);
        Assert.Equal("first", Assert.Single(vm.Inventory.Containers).Session.Name);
        Assert.Contains("Oldest read", vm.StatusText);
        vm.Sessions[1].IsSelected = true;
        Assert.Single(vm.Inventory.Containers);
        Assert.NotEmpty(vm.WarningText);
        Assert.Equal(["first"], reads);
        await vm.LoadAsync();

        Assert.Equal(["first", "first", "second"], reads);
        Assert.Equal(2, vm.Inventory.Containers.Count);
        Assert.Equal("managed", management.SelectedSessionName);
        Assert.True(vm.HasLoadedSelection);
        vm.ClearSelection();
        Assert.False(vm.HasLoadedSelection);
        vm.SelectAll();
        Assert.True(vm.HasLoadedSelection);
        Assert.Equal(3, reads.Count);
    }

    [Fact]
    public async Task EnginePathChangeInvalidatesCombinedRowsAndDiscovery()
    {
        var path = "engine-one";
        var context = new WslcSessionContext();
        context.Select("managed");
        var reader = CreateReader(() => [First], session => Inventory(session.Name), () => path);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance, context);
        await vm.DiscoverAsync();
        await vm.LoadAsync();
        var row = Assert.Single(vm.ContainerRows);
        Assert.True(vm.IsCurrentRow(row));

        Assert.True(context.TryApplyIfUnlocked(() => path = "engine-two"));

        Assert.Empty(vm.Sessions);
        Assert.Empty(vm.ContainerRows);
        Assert.False(vm.IsCurrentRow(row));
        Assert.Equal("managed", context.SelectedSessionName);
    }

    [Fact]
    public async Task EnginePathChangeBeforeLoadRequiresRediscovery()
    {
        var path = "engine-one";
        var reads = 0;
        var reader = CreateReader(() => [First], session => { reads++; return Inventory(session.Name); }, () => path);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.DiscoverAsync();
        path = "engine-two";

        await vm.LoadAsync();

        Assert.Equal(0, reads);
        Assert.Empty(vm.ContainerRows);
        Assert.Contains("executable changed", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovedFilterRowCannotBeUsedAsCurrent()
    {
        var reader = CreateReader(() => [First], session => Inventory(session.Name));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.DiscoverAsync();
        await vm.LoadAsync();
        var row = Assert.Single(vm.ContainerRows);
        Assert.True(vm.IsCurrentRow(row));

        vm.ClearSelection();

        Assert.False(vm.IsCurrentRow(row));
    }

    [Fact]
    public async Task ReplacedSelectedSessionIsRetainedAsUnavailableRatherThanRetargeted()
    {
        IReadOnlyList<WslcSessionInfo> sessions = [First];
        var reader = CreateReader(() => sessions, session => Inventory(session.Name));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.DiscoverAsync();
        await vm.LoadAsync();

        sessions = [First with { ID = 10 }];
        await vm.DiscoverAsync();

        Assert.Equal(2, vm.Sessions.Count);
        Assert.Contains(vm.Sessions, choice => choice.Session.ID == 1 && choice.IsSelected && !choice.IsAvailable);
        Assert.Contains(vm.Sessions, choice => choice.Session.ID == 10 && !choice.IsSelected && choice.IsAvailable);
        Assert.Empty(vm.Inventory.Containers);
        Assert.NotEmpty(vm.WarningText);
        vm.SelectAll();
        Assert.Contains(vm.Sessions, choice => choice.Session.ID == 1 && !choice.IsSelected);
        Assert.Contains(vm.Sessions, choice => choice.Session.ID == 10 && choice.IsSelected);
    }

    [Fact]
    public async Task SelectionChangesDuringLoadOnlyDisplayTheCurrentFilter()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader(() => [First, Second], session =>
            Inventory(session.Name, async ct =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return [new ContainerInfo { Id = "same", Name = session.Name }];
            }));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.DiscoverAsync();

        var load = vm.LoadAsync();
        await started.Task;
        vm.Sessions[0].IsSelected = false;
        release.SetResult();
        await load;

        Assert.Equal("second", Assert.Single(vm.Inventory.Containers).Session.Name);
    }

    [Fact]
    public async Task CancelledDiscoveryDoesNotPublishLateResults()
    {
        var completion = new TaskCompletionSource<WslcSystemInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => completion.Task,
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => Inventory("first"),
            () => "wslc", NullLogger<WslcSessionInventoryReader>.Instance);
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);

        var discover = vm.DiscoverAsync();
        vm.CancelRefresh();
        completion.SetResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } });
        await discover;

        Assert.Empty(vm.Sessions);
        Assert.Contains("cancelled", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledLoadDiscardsLateResultsAndSkipsFinalDiscovery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<IReadOnlyList<ContainerInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveries = 0;
        var reader = CreateReader(() => { discoveries++; return [First]; }, session =>
            Inventory(session.Name, _ =>
            {
                started.TrySetResult();
                return completion.Task;
            }));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        await vm.DiscoverAsync();

        var load = vm.LoadAsync();
        await started.Task;
        vm.CancelRefresh();
        completion.SetResult([new ContainerInfo { Id = "late", Name = "late" }]);
        await load;

        Assert.Empty(vm.ContainerRows);
        Assert.Equal(2, discoveries);
        Assert.Contains("cancelled", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1", 6, 8080, true)]
    [InlineData("::1", 6, 8080, true)]
    [InlineData("0.0.0.0", 6, 8080, true)]
    [InlineData("127.0.0.1@other.example", 6, 8080, false)]
    [InlineData("example.com/path", 6, 8080, false)]
    [InlineData("127.0.0.1", 17, 8080, false)]
    [InlineData("127.0.0.1", 6, 70000, false)]
    public void EndpointOpeningRequiresAnIpAddressAndTcpPort(string binding, int protocol, int port, bool expected)
    {
        var endpoint = new WslcCombinedInventory.Endpoint(First, new ContainerInfo
            { Name = "web", Id = "123456789abc", CreatedAt = 123, CreatedAtKnown = true, StateValue = (int)ContainerState.Running },
            new PortMapping { BindingAddress = binding, Protocol = protocol, HostPort = port, ContainerPort = 80 });
        var row = CombinedSessionRow.FromEndpoint(endpoint);

        Assert.Equal(expected, row.CanOpenEndpoint);
        Assert.Equal(expected, row.GetEndpointUri() is not null);
    }

    [Fact]
    public void EndpointOpeningRequiresStableRunningContainerIdentity()
    {
        var port = new PortMapping { BindingAddress = "127.0.0.1", Protocol = 6, HostPort = 8080, ContainerPort = 80 };
        var missingIdentity = CombinedSessionRow.FromEndpoint(
            new WslcCombinedInventory.Endpoint(First, new ContainerInfo { StateValue = (int)ContainerState.Running }, port));
        var stopped = CombinedSessionRow.FromEndpoint(new WslcCombinedInventory.Endpoint(First, new ContainerInfo
        {
            Id = "123456789abc", Name = "web", CreatedAt = 123, CreatedAtKnown = true,
            StateValue = (int)ContainerState.Stopped,
        }, port));

        Assert.False(missingIdentity.CanOpenEndpoint);
        Assert.False(stopped.CanOpenEndpoint);
    }

    [Fact]
    public void ResourceActionsRequireAStableSessionGeneration()
    {
        var unknown = First with { CreatorPid = 0 };
        var image = CombinedSessionRow.FromImage(new(unknown,
            new ImageInfo { Id = "image-id", Repository = "example", Tag = "latest" }));
        var volume = CombinedSessionRow.FromVolume(new(unknown, new VolumeInfo { Name = "data" }));
        var network = CombinedSessionRow.FromNetwork(new(unknown, new NetworkInfo { Id = "network-id", Name = "app" }));

        Assert.False(image.CanInspect);
        Assert.False(image.CanRemoveImage);
        Assert.False(volume.CanInspect);
        Assert.False(volume.CanRemoveVolume);
        Assert.False(network.CanInspect);
        Assert.False(network.CanRemoveNetwork);
    }

    [Fact]
    public async Task LanguageSwitchRefreshesContainerStateWithoutReplacingRowsOrSelections()
    {
        UiText.Configure((key, args) => key switch
        {
            "Workload_Text_Running_f4ccae" => UiText.LanguageTag == "zh-Hans" ? "运行中" : "Running",
            "CombinedSession_ActionsFor" => string.Format(
                UiText.LanguageTag == "zh-Hans" ? "对 {1} 中的 {0} 执行操作" : "Actions for {0} in {1}", args),
            _ => key,
        });
        try
        {
            UiText.SetLanguage("en-US");
            var reader = CreateReader(() => [First], session => Inventory(session.Name, _ =>
                Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    [new ContainerInfo { Id = "123", Name = "web", StateValue = (int)ContainerState.Running }])));
            using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
            await vm.DiscoverAsync();
            await vm.LoadAsync();
            var row = Assert.Single(vm.ContainerRows);
            Assert.Equal("Running", row.StateText);

            UiText.SetLanguage("zh-Hans");

            Assert.Same(row, Assert.Single(vm.ContainerRows));
            Assert.True(Assert.Single(vm.Sessions).IsSelected);
            Assert.Equal("运行中", row.StateText);
            Assert.Equal("对 first 中的 web 执行操作", row.ActionsName);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task LanguageSwitchDoesNotCancelAnInFlightInventoryRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var reader = CreateReader(() => [First], session => Inventory(session.Name, async ct =>
        {
            reads++;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return [new ContainerInfo { Id = "123", Name = "web" }];
        }));
        using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
        try
        {
            UiText.SetLanguage("en-US");
            var refresh = vm.RefreshAllAsync();
            await started.Task;
            var choice = Assert.Single(vm.Sessions);
            Assert.True(vm.IsBusy);

            UiText.SetLanguage("zh-Hans");

            Assert.True(vm.IsBusy);
            Assert.Same(choice, Assert.Single(vm.Sessions));
            Assert.True(choice.IsSelected);
            release.SetResult();
            await refresh;
            Assert.False(vm.IsBusy);
            Assert.Equal(1, reads);
            Assert.Equal("web", Assert.Single(vm.ContainerRows).Name);
        }
        finally
        {
            release.TrySetResult();
            UiText.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task LanguageSwitchRefreshesAppAuthoredDiscoveryError()
    {
        UiText.Configure((key, _) => key == "WslcSessionInventory_InvalidSessions"
            ? UiText.LanguageTag == "zh-Hans" ? "会话标识无效" : "Invalid session identities"
            : key);
        try
        {
            UiText.SetLanguage("en-US");
            var duplicate = Second with { ID = First.ID };
            var reader = CreateReader(() => [First, duplicate], session => Inventory(session.Name));
            using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
            await vm.DiscoverAsync();
            Assert.Contains("Invalid session identities", vm.StatusText);

            UiText.SetLanguage("zh-Hans");

            Assert.Contains("会话标识无效", vm.StatusText);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    [Fact]
    public async Task LanguageSwitchRefreshesDiscoveryTimeout()
    {
        UiText.Configure((key, _) => key == "WslcSessionInventory_DiscoveryTimedOut"
            ? UiText.LanguageTag == "zh-Hans" ? "会话发现超时" : "Session discovery timed out"
            : key);
        var pending = new TaskCompletionSource<WslcSystemInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            UiText.SetLanguage("en-US");
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => pending.Task,
                _ => throw new InvalidOperationException(method.Name),
            });
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => Inventory("first"),
                () => "wslc", NullLogger<WslcSessionInventoryReader>.Instance, TimeSpan.FromMilliseconds(50));
            using var vm = new CombinedSessionViewModel(reader, NullLogger<CombinedSessionViewModel>.Instance);
            await vm.DiscoverAsync();
            Assert.Contains("Session discovery timed out", vm.StatusText);

            UiText.SetLanguage("zh-Hans");

            Assert.Contains("会话发现超时", vm.StatusText);
        }
        finally
        {
            pending.TrySetResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [] } });
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    private static WslcSessionInventoryReader CreateReader(
        Func<IReadOnlyList<WslcSessionInfo>> sessions, Func<WslcSessionInfo, IWslcService> create,
        Func<string>? executable = null)
    {
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = sessions() },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        return new WslcSessionInventoryReader(discovery, (session, _) => create(session), executable ?? (() => "wslc"),
            NullLogger<WslcSessionInventoryReader>.Instance);
    }

    private static IWslcService Inventory(string name, Func<CancellationToken, Task<IReadOnlyList<ContainerInfo>>>? containers = null) =>
        NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => containers?.Invoke((CancellationToken)args[1]!) ??
                Task.FromResult<IReadOnlyList<ContainerInfo>>([new ContainerInfo { Id = "same", Name = name }]),
            nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>([]),
            nameof(IWslcService.ListVolumesAsync) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]),
            nameof(IWslcService.ListNetworksAsync) => Task.FromResult<IReadOnlyList<NetworkInfo>>([]),
            _ => throw new InvalidOperationException(method.Name),
        });
}
