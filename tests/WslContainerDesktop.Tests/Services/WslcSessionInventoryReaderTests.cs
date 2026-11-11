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
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcSessionInventoryReaderTests
{
    private static readonly WslcSessionInfo First = new() { ID = 1, CreatorPid = 100, Name = "first" };
    private static readonly WslcSessionInfo Second = new() { ID = 2, CreatorPid = 200, Name = "second" };

    [Fact]
    public async Task QueuedSessionObservationStartsAtItsActualReadRatherThanTheBatchStart()
    {
        var third = new WslcSessionInfo { ID = 3, CreatorPid = 300, Name = "third" };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var reader = CreateReader(() => [First, Second, third], session =>
            NetworkTestProxy.Create<IWslcService>((method, _) =>
            {
                Assert.Equal(nameof(IWslcService.ListContainersAsync), method.Name);
                return ReadAsync(session);
            }));
        async Task<IReadOnlyList<ContainerInfo>> ReadAsync(WslcSessionInfo session)
        {
            if (session != third)
            {
                if (Interlocked.Increment(ref active) == 2) started.TrySetResult();
                await release.Task;
            }
            return [];
        }

        var reading = reader.ReadContainersAsync([First, Second, third]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var releasedAt = DateTimeOffset.UtcNow;
        release.TrySetResult();
        var result = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Single(item => item.Session == third).ObservedAt >= releasedAt);
        Assert.True(result.Single(item => item.Session == First).ObservedAt <= releasedAt);
    }

    [Fact]
    public async Task ShortStartupTimeoutRetainsSuccessfulPeerInsteadOfDroppingTheWholePair()
    {
        var reader = CreateReader(() => [First, Second], session =>
            Inventory(session.Name, timeoutContainers: session == Second));

        var result = await reader.ReadContainersAsync([First, Second], sessionTimeout: TimeSpan.FromMilliseconds(50));

        Assert.True(result.Single(item => item.Session == First).IsComplete);
        Assert.True(result.Single(item => item.Session == Second).Containers.TimedOut);
    }

    [Fact]
    public async Task BatchedStartupObservationsUseTwoDiscoveriesAndOneListPerSessionWithoutInspects()
    {
        var discoveries = 0;
        var lists = 0;
        var sessions = Enumerable.Range(1, 5).Select(id =>
            new WslcSessionInfo { ID = id, CreatorPid = 100 + id, Name = $"session-{id}" }).ToArray();
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            Assert.Equal(nameof(IWslcService.GetSystemInfoAsync), method.Name);
            Interlocked.Increment(ref discoveries);
            return Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = sessions } });
        });
        var client = NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            Assert.Equal(nameof(IWslcService.ListContainersAsync), method.Name);
            Interlocked.Increment(ref lists);
            return Result(new ContainerInfo { Id = "one", Name = "web" });
        });
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => "wslc",
            NullLogger<WslcSessionInventoryReader>.Instance);

        var results = await reader.ReadContainersAsync(sessions);

        Assert.Equal(2, discoveries);
        Assert.Equal(5, lists);
        Assert.All(results, result => Assert.True(result.IsComplete));
    }

    [Fact]
    public async Task BatchedObservationsReuseTheInitialDiscoverySnapshot()
    {
        var discoveries = 0;
        var sessions = new[] { First, Second };
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            Assert.Equal(nameof(IWslcService.GetSystemInfoAsync), method.Name);
            Interlocked.Increment(ref discoveries);
            return Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = sessions } });
        });
        var client = NetworkTestProxy.Create<IWslcService>((method, _) =>
        {
            Assert.Equal(nameof(IWslcService.ListContainersAsync), method.Name);
            return Result<ContainerInfo>();
        });
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => client, () => "wslc",
            NullLogger<WslcSessionInventoryReader>.Instance);
        var initial = await reader.DiscoverAsync();

        var results = await reader.ReadContainersAsync(sessions, initial, "wslc");

        Assert.Equal(2, discoveries);
        Assert.All(results, result => Assert.True(result.IsComplete));
    }

    [Fact]
    public async Task SameResourceIdsRemainSeparatedByOwningSession()
    {
        var sessions = new[] { First, Second };
        var reader = CreateReader(() => sessions, session => Inventory(session.Name));

        var result = await reader.ReadAsync(sessions);

        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.True(item.IsComplete));
        Assert.Equal(["first", "second"], result.Select(item => item.Session.Name));
        Assert.Equal(["same-id", "same-id"], result.Select(item => Assert.Single(item.Containers.Items!).Id));
        Assert.Equal(["first", "second"], result.Select(item => Assert.Single(item.Containers.Items!).Name));
        Assert.All(result, item => Assert.Equal("bridge", Assert.Single(item.Networks.Items!).Name));
    }

    [Fact]
    public async Task FailedInventoryIsNotReportedAsAnEmptyInventory()
    {
        var reader = CreateReader(() => [First], _ => Inventory("first", failImages: true));

        var result = Assert.Single(await reader.ReadAsync([First]));

        Assert.False(result.IsComplete);
        Assert.Null(result.Images.Items);
        Assert.Equal("image read failed", result.Images.Error);
        Assert.Empty(result.Volumes.Items!);
        Assert.True(result.Volumes.IsComplete);
    }

    [Fact]
    public async Task ReplacedSessionDiscardsEveryPreviouslyReadResource()
    {
        var discoveryCount = 0;
        var replacement = First with { ID = 9 };
        var reader = CreateReader(() => ++discoveryCount == 1 ? [First] : [replacement],
            _ => Inventory("first"));

        var result = Assert.Single(await reader.ReadAsync([First]));

        Assert.True(result.SessionChanged);
        Assert.False(result.IsComplete);
        Assert.Null(result.Containers.Items);
        Assert.Null(result.Images.Items);
        Assert.Null(result.Volumes.Items);
        Assert.Null(result.Networks.Items);
    }

    [Fact]
    public async Task ContainerOnlyObservationDoesNotReadOtherResources()
    {
        var client = NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.ListContainersAsync)
                ? Result(new ContainerInfo { Id = "one", Name = "web" })
                : throw new InvalidOperationException($"Unexpected read: {method.Name}"));
        var reader = CreateReader(() => [First], _ => client);

        var result = Assert.Single(await reader.ReadContainersAsync([First]));

        Assert.True(result.IsComplete);
        Assert.Equal("web", Assert.Single(result.Containers.Items!).Name);
    }

    [Fact]
    public async Task ContainerOnlyObservationDiscardsAReplacedSession()
    {
        var count = 0;
        var reader = CreateReader(() => ++count == 1 ? [First] : [First with { ID = 9 }],
            _ => Inventory("first"));

        var result = Assert.Single(await reader.ReadContainersAsync([First]));

        Assert.True(result.SessionChanged);
        Assert.False(result.IsComplete);
        Assert.Null(result.Containers.Items);
        Assert.NotNull(result.ObservedAt);
    }

    [Fact]
    public async Task ContainerOnlyObservationReportsTimeoutWithoutTreatingItAsEmpty()
    {
        var reader = CreateReader(() => [First], _ => Inventory("first", timeoutContainers: true),
            timeout: TimeSpan.FromMilliseconds(50));

        var result = Assert.Single(await reader.ReadContainersAsync([First]));

        Assert.True(result.Containers.TimedOut);
        Assert.False(result.IsComplete);
        Assert.Null(result.Containers.Items);
    }

    [Fact]
    public async Task MissingSessionIsNotOpenedOrRetargeted()
    {
        var created = 0;
        var reader = CreateReader(() => [], _ =>
        {
            created++;
            return Inventory("unexpected");
        });

        var result = Assert.Single(await reader.ReadAsync([First]));

        Assert.True(result.SessionChanged);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task DuplicateSessionNamesAreRejectedInsteadOfMerged()
    {
        var duplicate = Second with { Name = "first" };
        var reader = CreateReader(() => [First, duplicate], _ => Inventory("unexpected"));

        await Assert.ThrowsAsync<WslcSessionInventoryErrors.InventoryException>(() => reader.DiscoverAsync());
    }

    [Fact]
    public async Task CaseDistinctSessionNamesRemainSeparate()
    {
        var distinct = Second with { Name = "FIRST" };
        var reader = CreateReader(() => [First, distinct], session => Inventory(session.Name));

        var result = await reader.ReadAsync([First, distinct]);

        Assert.Equal(["first", "FIRST"], result.Select(item => item.Session.Name));
        Assert.All(result, item => Assert.True(item.IsComplete));
    }

    [Fact]
    public async Task DuplicateSessionIdsAreRejectedInsteadOfMerged()
    {
        var duplicate = Second with { ID = First.ID };
        var reader = CreateReader(() => [First, duplicate], _ => Inventory("unexpected"));

        await Assert.ThrowsAsync<WslcSessionInventoryErrors.InventoryException>(() => reader.DiscoverAsync());
    }

    [Fact]
    public async Task SessionReadsUseBoundedConcurrency()
    {
        var sessions = Enumerable.Range(1, 6)
            .Select(index => new WslcSessionInfo { ID = index, CreatorPid = index, Name = $"s{index}" })
            .ToArray();
        var active = 0;
        var maximum = 0;
        async Task<IReadOnlyList<ContainerInfo>> Read(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do
            {
                previous = Volatile.Read(ref maximum);
                if (previous >= current)
                    break;
            } while (Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            try
            {
                await Task.Delay(25, ct);
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var reader = CreateReader(() => sessions, _ => NetworkTestProxy.Create<IWslcService>((method, args) =>
            method.Name switch
            {
                nameof(IWslcService.ListContainersAsync) => Read((CancellationToken)args[1]!),
                nameof(IWslcService.ListImagesAsync) => Result<ImageInfo>(),
                nameof(IWslcService.ListVolumesAsync) => Result<VolumeInfo>(),
                nameof(IWslcService.ListNetworksAsync) => Result<NetworkInfo>(),
                _ => throw new InvalidOperationException(method.Name),
            }));

        var result = await reader.ReadAsync(sessions);

        Assert.All(result, item => Assert.True(item.IsComplete));
        Assert.InRange(maximum, 1, 2);
    }

    [Fact]
    public async Task ExecutableChangeDiscardsTheRefresh()
    {
        var path = "old";
        var created = 0;
        var reader = new WslcSessionInventoryReader(
            Discovery(() =>
            {
                path = "new";
                return [First];
            }),
            (session, _) =>
            {
                created++;
                return Inventory(session.Name);
            },
            () => path,
            NullLogger<WslcSessionInventoryReader>.Instance);

        await Assert.ThrowsAsync<WslcSessionInventoryErrors.InventoryException>(() => reader.ReadAsync([First]));
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task TimedOutSessionPreservesCompletedResourceTypes()
    {
        var reader = CreateReader(() => [First], _ => Inventory("first", timeoutContainers: true),
            timeout: TimeSpan.FromMilliseconds(50));

        var result = Assert.Single(await reader.ReadAsync([First]));

        Assert.True(result.Containers.TimedOut);
        Assert.Null(result.Containers.Items);
        Assert.True(result.Images.IsComplete);
        Assert.True(result.Volumes.IsComplete);
        Assert.True(result.Networks.IsComplete);
    }

    [Fact]
    public async Task LateResultAfterTimeoutIsNotPublished()
    {
        async Task<IReadOnlyList<ContainerInfo>> IgnoreTimeout(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return [new ContainerInfo { Id = "late", Name = "late" }];
        }

        var client = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => IgnoreTimeout((CancellationToken)args[1]!),
            nameof(IWslcService.ListImagesAsync) => Result<ImageInfo>(),
            nameof(IWslcService.ListVolumesAsync) => Result<VolumeInfo>(),
            nameof(IWslcService.ListNetworksAsync) => Result<NetworkInfo>(),
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = CreateReader(() => [First], _ => client, timeout: TimeSpan.FromMilliseconds(50));

        var result = Assert.Single(await reader.ReadAsync([First]));

        Assert.True(result.Containers.TimedOut);
        Assert.Null(result.Containers.Items);
        Assert.True(result.Images.IsComplete);
    }

    [Fact]
    public async Task NeverCompletingPartIsBoundedBySessionTimeout()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<ContainerInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => pending.Task,
            nameof(IWslcService.ListImagesAsync) => Result<ImageInfo>(),
            nameof(IWslcService.ListVolumesAsync) => Result<VolumeInfo>(),
            nameof(IWslcService.ListNetworksAsync) => Result<NetworkInfo>(),
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = CreateReader(() => [First], _ => client, timeout: TimeSpan.FromMilliseconds(50));

        var result = Assert.Single(await reader.ReadAsync([First]).WaitAsync(TimeSpan.FromSeconds(5)));
        pending.SetResult([]);

        Assert.True(result.Containers.TimedOut);
        Assert.Null(result.Containers.Items);
        Assert.True(result.Images.IsComplete);
    }

    [Fact]
    public async Task NeverCompletingDiscoveryReportsTimeout()
    {
        var pending = new TaskCompletionSource<WslcSystemInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => pending.Task,
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => Inventory("first"),
            () => "wslc", NullLogger<WslcSessionInventoryReader>.Instance, TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<WslcSessionInventoryErrors.InventoryException>(
            () => reader.DiscoverAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        pending.SetResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } });

        Assert.Equal(WslcSessionInventoryErrors.Kind.DiscoveryTimedOut, error.Reason);
    }

    [Fact]
    public async Task DiscoveryRejectsExecutableChangedDuringRead()
    {
        var path = "engine-one";
        var completion = new TaskCompletionSource<WslcSystemInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => completion.Task,
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = new WslcSessionInventoryReader(discovery, (_, _) => Inventory("first"),
            () => path, NullLogger<WslcSessionInventoryReader>.Instance);
        var operation = reader.DiscoverAsync();
        path = "engine-two";
        completion.SetResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [First] } });

        var error = await Assert.ThrowsAsync<WslcSessionInventoryErrors.InventoryException>(() => operation);

        Assert.Equal(WslcSessionInventoryErrors.Kind.ExecutableChanged, error.Reason);
    }

    [Fact]
    public async Task UnexpectedCancellationIsAnExplicitIncompletePart()
    {
        var client = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => Result<ContainerInfo>(),
            nameof(IWslcService.ListImagesAsync) => Task.FromCanceled<IReadOnlyList<ImageInfo>>(new CancellationToken(true)),
            nameof(IWslcService.ListVolumesAsync) => Result<VolumeInfo>(),
            nameof(IWslcService.ListNetworksAsync) => Result<NetworkInfo>(),
            _ => throw new InvalidOperationException(method.Name),
        });
        var reader = CreateReader(() => [First], _ => client);

        var result = Assert.Single(await reader.ReadAsync([First]));

        Assert.False(result.IsComplete);
        Assert.True(result.Images.UnexpectedCancellation);
        Assert.Null(result.Images.Items);
        Assert.True(result.Volumes.IsComplete);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToPartialSuccess()
    {
        using var cancel = new CancellationTokenSource();
        var reader = CreateReader(() => [First], _ => Inventory("first", timeoutContainers: true));
        var operation = reader.ReadAsync([First], cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static WslcSessionInventoryReader CreateReader(
        Func<IReadOnlyList<WslcSessionInfo>> sessions,
        Func<WslcSessionInfo, IWslcService> create,
        TimeSpan? timeout = null) =>
        new(Discovery(sessions), (session, _) => create(session), () => "wslc",
            NullLogger<WslcSessionInventoryReader>.Instance, timeout);

    private static IWslcService Discovery(Func<IReadOnlyList<WslcSessionInfo>> sessions) =>
        NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = sessions() },
            }),
            _ => throw new InvalidOperationException($"Unexpected discovery call: {method.Name}"),
        });

    private static IWslcService Inventory(string name, bool failImages = false, bool timeoutContainers = false) =>
        NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => timeoutContainers
                ? WaitForCancellation<ContainerInfo>((CancellationToken)args[1]!)
                : Result(new ContainerInfo { Id = "same-id", Name = name }),
            nameof(IWslcService.ListImagesAsync) => failImages
                ? Task.FromException<IReadOnlyList<ImageInfo>>(new InvalidOperationException("image read failed"))
                : Result<ImageInfo>(),
            nameof(IWslcService.ListVolumesAsync) => Result<VolumeInfo>(),
            nameof(IWslcService.ListNetworksAsync) => Result<NetworkInfo>(),
            _ => throw new InvalidOperationException($"Unexpected inventory call: {method.Name}"),
        });

    private static Task<IReadOnlyList<T>> Result<T>(params T[] items) =>
        Task.FromResult<IReadOnlyList<T>>(items);

    private static async Task<IReadOnlyList<T>> WaitForCancellation<T>(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return [];
    }
}
