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

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class VolumeUsageResolverTests
{
    private const string NamedMount = """
        {"Mounts":[{"Type":"volume","Name":"data","Source":"/internal/data","Destination":"/data"}]}
        """;

    [Fact]
    public async Task IncludesAllUsersIncludingStoppedAndAnonymousVolumes()
    {
        var volumes = new[] { new VolumeInfo { Name = "data" }, new VolumeInfo { Name = "anonymous", IsAnonymous = true }, new VolumeInfo { Name = "unused" } };
        var containers = new[] { Container("running", 1), Container("stopped", 0) };
        var warnings = await VolumeUsageResolver.ResolveAsync(volumes, containers, (_, _) => Success("""
            {"Mounts":[{"Type":"volume","Name":"data","Destination":"/data"},
            {"Type":"volume","Source":"anonymous","Destination":"/tmp"}]}
            """));
        Assert.Empty(warnings);
        Assert.All(volumes.Take(2), v =>
        {
            Assert.Equal(VolumeUsageState.Exact, v.UsageState);
            Assert.Equal(new[] { "running", "stopped" }, v.ContainerUsers);
        });
        Assert.Equal(VolumeUsageState.Unused, volumes[2].UsageState);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Mounts":[null]}""")]
    [InlineData("disappeared")]
    public async Task PartialInspectionNeverMeansUnused(string failedJson)
    {
        var volumes = new[] { new VolumeInfo { Name = "data" }, new VolumeInfo { Name = "other" } };
        var warnings = await VolumeUsageResolver.ResolveAsync(volumes, [Container("known"), Container("unknown")],
            (id, _) => id == "known" ? Success(NamedMount) : failedJson == "disappeared"
                ? Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "container no longer exists" })
                : Success(failedJson));
        Assert.NotEmpty(warnings);
        Assert.Equal(VolumeUsageState.Partial, volumes[0].UsageState);
        Assert.Equal("known (other users unknown)", volumes[0].UsedByDisplay);
        Assert.Equal(VolumeUsageState.Unknown, volumes[1].UsageState);
        Assert.Equal("Unknown", volumes[1].UsedByDisplay);
    }

    [Fact]
    public async Task BindAndInternalSourcesCannotMatchNamedVolumes()
    {
        var volumes = new[] { new VolumeInfo { Name = "data" }, new VolumeInfo { Name = "/internal/data" } };
        await VolumeUsageResolver.ResolveAsync(volumes, [Container("test")], (_, _) => Success("""
            {"Mounts":[{"Type":"bind","Name":"data","Source":"data","Destination":"/data"},
            {"Type":"volume","Source":"/internal/data","Destination":"/other"}]}
            """));
        Assert.All(volumes, v =>
        {
            Assert.Empty(v.ContainerUsers);
            Assert.Equal(VolumeUsageState.Unknown, v.UsageState);
        });
    }

    [Fact]
    public async Task TimestampFallbackIsExplicitlyEstimatedAndNeverUnused()
    {
        var volume = new VolumeInfo { Name = "anonymous", IsAnonymous = true, CreatedAt = DateTimeOffset.FromUnixTimeSeconds(100) };
        await VolumeUsageResolver.ResolveAsync([volume], [Container("legacy")], (_, _) => Success("{}"));
        Assert.Equal(VolumeUsageState.Estimated, volume.UsageState);
        Assert.Contains("estimated", volume.UsedByDisplay);
        await VolumeUsageResolver.ResolveAsync([volume], [Container("legacy")], (_, _) => Success("""{"Mounts":[]}"""));
        Assert.Equal(VolumeUsageState.Unused, volume.UsageState);
        Assert.Empty(volume.ContainerUsers);
    }

    [Fact]
    public async Task IncompleteOrInvalidInventoryDoesNotEstablishUnused()
    {
        var volume = new VolumeInfo { Name = "data" };
        await VolumeUsageResolver.ResolveAsync([volume], [], (_, _) => throw new InvalidOperationException(),
            containerInventoryComplete: false);
        Assert.Equal(VolumeUsageState.Unknown, volume.UsageState);
        await VolumeUsageResolver.ResolveAsync([volume], [new ContainerInfo()], (_, _) => throw new InvalidOperationException());
        Assert.Equal(VolumeUsageState.Unknown, volume.UsageState);
    }

    [Fact]
    public async Task LegitimateEmptyInventoryEstablishesUnused()
    {
        var volume = new VolumeInfo { Name = "data" };
        var warnings = await VolumeUsageResolver.ResolveAsync([volume], [],
            (_, _) => throw new InvalidOperationException(), containerInventoryComplete: true);
        Assert.Empty(warnings);
        Assert.Equal(VolumeUsageState.Unused, volume.UsageState);
    }

    [Fact]
    public async Task WorkIsBoundedAndCancellationDoesNotPublishPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        var started = 0;
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var volume = new VolumeInfo { Name = "data" };
        var task = VolumeUsageResolver.ResolveAsync([volume],
            Enumerable.Range(0, 40).Select(i => Container(i.ToString())).ToArray(),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref started) == 4)
                {
                    fourStarted.SetResult();
                }
                await Task.Delay(Timeout.Infinite, ct);
                return new CommandResult();
            }, cancellation.Token);
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, Volatile.Read(ref started));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(VolumeUsageState.Unknown, volume.UsageState);
    }

    private static ContainerInfo Container(string id, int state = 0) =>
        new() { Id = id, Name = id, StateValue = state, CreatedAt = 100 };

    private static Task<CommandResult> Success(string json) =>
        Task.FromResult(new CommandResult { StandardOutput = json });
}
