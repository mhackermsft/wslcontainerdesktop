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

public sealed class ContainerPortResolverTests
{
    private const string Inspect = """
        [{"Id":"fe1efb58e1aba1b531846d13c8683296f411edd9b9891f38c95ffecc9c9e1cbb",
          "Name":"/wslcd-ollama","HostConfig":{"NetworkMode":"bridge"},
          "Ports":{"11434/tcp":[{"HostIp":"127.0.0.1","HostPort":"11434"}]},
          "State":{"Status":"exited","Running":false}}]
        """;

    [Fact]
    public async Task StoppedContainer_ResolvesObservedTopLevelInspectPorts()
    {
        var row = Row();
        var resolver = new ContainerPortResolver();
        await resolver.ResolveAsync([row], true, (_, _) => Task.FromResult(new CommandResult { StandardOutput = Inspect }),
            (_, message) => Assert.Fail(message), default);
        Assert.True(row.PortsKnown);
        Assert.Equal(11434, Assert.Single(row.Ports).HostPort);
        Assert.Equal("127.0.0.1", row.Ports[0].BindingAddress);
        Assert.Equal("fe1efb58e1ab", row.Id); // Stable inventory keys; inspect must not change row identity.
    }

    [Theory]
    [InlineData("""{"Id":"fe1efb58e1ab","Ports":{}}""", true)]
    [InlineData("""{"Id":"fe1efb58e1ab","Ports":{"80/tcp":null}}""", true)]
    [InlineData("""{"Id":"fe1efb58e1ab","HostConfig":{"PortBindings":{"80/tcp":[{"HostIp":"0.0.0.0","HostPort":"8080"}]}}}""", true)]
    [InlineData("""{"Id":"fe1efb58e1ab","Ports":[]}""", true)]
    [InlineData("""{"Id":"fe1efb58e1ab"}""", false)]
    [InlineData("""{"Id":"different","Ports":{}}""", false)]
    [InlineData("""{"Id":"fe1efb58e1ab","Ports":{"80/tcp":[{"HostPort":"invalid"}]}}""", false)]
    [InlineData("""[{"Id":"fe1efb58e1ab","Ports":{}},{"Id":"other","Ports":{}}]""", false)]
    [InlineData("invalid json", false)]
    public async Task InspectShapes_KeepUnavailableDistinctFromAbsent(string json, bool known)
    {
        var row = Row();
        var warnings = new List<string>();
        await new ContainerPortResolver().ResolveAsync([row], true,
            (_, _) => Task.FromResult(new CommandResult { StandardOutput = json }),
            (_, message) => warnings.Add(message), default);
        Assert.Equal(known, row.PortsKnown);
        Assert.Equal(!known, warnings.Count > 0);
    }

    [Fact]
    public async Task Cache_ExpiresAndInvalidatesOnStateCreationNameAndRemoval()
    {
        var resolver = new ContainerPortResolver();
        var calls = 0;
        var now = DateTimeOffset.UtcNow;
        Task<CommandResult> Read(string id, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new CommandResult { StandardOutput = Inspect });
        }
        async Task Resolve(ContainerInfo row, int seconds = 0) =>
            await resolver.ResolveAsync([row], true, Read, (_, m) => Assert.Fail(m), default, now.AddSeconds(seconds));
        await Resolve(Row());
        var cached = Row();
        await Resolve(cached, 1);
        Assert.True(cached.PortsKnown);
        Assert.Equal(1, calls);
        await Resolve(Row(), 301);
        Assert.Equal(2, calls);
        var running = Row();
        running.StateValue = 2;
        await Resolve(running, 302);
        Assert.Equal(3, calls);
        var recreated = Row();
        recreated.CreatedAt = 42;
        await Resolve(recreated, 303);
        Assert.Equal(4, calls);
        var renamed = Row();
        renamed.Name = "renamed";
        await Resolve(renamed, 304);
        Assert.Equal(5, calls);
        await resolver.ResolveAsync([], true, Read, (_, m) => Assert.Fail(m), default, now.AddSeconds(305));
        await Resolve(Row(), 306);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Budget_IsBoundedAndMakesProgressAcrossPolls()
    {
        var resolver = new ContainerPortResolver();
        var calls = 0;
        Task<CommandResult> Read(string id, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new CommandResult { StandardOutput = $$$"""{"Id":"{{{id}}}","Ports":{}}""" });
        }
        List<ContainerInfo> Rows() => Enumerable.Range(0, 10).Select(i => new ContainerInfo { Id = i.ToString() }).ToList();
        await resolver.ResolveAsync(Rows(), true, Read, (_, m) => Assert.Fail(m), default);
        Assert.Equal(4, calls);
        await resolver.ResolveAsync(Rows(), true, Read, (_, m) => Assert.Fail(m), default);
        Assert.Equal(8, calls);
        var last = Rows();
        await resolver.ResolveAsync(last, true, Read, (_, m) => Assert.Fail(m), default);
        Assert.Equal(10, calls);
        Assert.All(last, row => Assert.True(row.PortsKnown));
    }

    [Fact]
    public async Task FailedInspect_IsUnknownAndRetriesAfterNegativeTtl()
    {
        var resolver = new ContainerPortResolver();
        var calls = 0;
        var warnings = 0;
        var now = DateTimeOffset.UtcNow;
        Task<CommandResult> Read(string id, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new CommandResult { ExitCode = 1 });
        }
        foreach (var seconds in new[] { 0, 1, 31 })
        {
            var row = Row();
            await resolver.ResolveAsync([row], true, Read, (_, _) => warnings++, default, now.AddSeconds(seconds));
            Assert.False(row.PortsKnown);
        }
        Assert.Equal(2, calls);
        Assert.Equal(2, warnings);
    }

    [Fact]
    public async Task FilteredInventory_DoesNotPruneStoppedContainerCache()
    {
        var resolver = new ContainerPortResolver();
        var calls = 0;
        Task<CommandResult> Read(string id, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new CommandResult { StandardOutput = Inspect });
        }
        await resolver.ResolveAsync([Row()], true, Read, (_, m) => Assert.Fail(m), default);
        await resolver.ResolveAsync([], false, Read, (_, m) => Assert.Fail(m), default);
        await resolver.ResolveAsync([Row()], true, Read, (_, m) => Assert.Fail(m), default);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareCacheAndDoNotFanOut()
    {
        var resolver = new ContainerPortResolver();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<CommandResult> Read(string id, CancellationToken ct)
        {
            calls++;
            entered.SetResult();
            await release.Task;
            return new CommandResult { StandardOutput = Inspect };
        }
        var first = resolver.ResolveAsync([Row()], true, Read, (_, m) => Assert.Fail(m), default);
        await entered.Task;
        var second = resolver.ResolveAsync([Row()], true, Read, (_, m) => Assert.Fail(m), default);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledInspect_PropagatesAndReleasesGate()
    {
        var resolver = new ContainerPortResolver();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync([Row()], true,
            (_, _) => Task.FromCanceled<CommandResult>(new CancellationToken(true)), (_, m) => Assert.Fail(m), default));
        var row = Row();
        await resolver.ResolveAsync([row], true, (_, _) => Task.FromResult(new CommandResult { StandardOutput = Inspect }),
            (_, m) => Assert.Fail(m), default);
        Assert.True(row.PortsKnown);
    }

    [Theory]
    [InlineData("fe1efb58e1ab", "fe1efb58e1ab1234567890", true)]
    [InlineData("fe1efb", "fe1efb58e1ab1234567890", false)]
    [InlineData("fe1efb58e1ab", "different", false)]
    public void InspectIdentity_RejectsUnrelatedOrOverlyShortPrefixes(string requested, string inspected, bool matches) =>
        Assert.Equal(matches, ContainerPortResolver.MatchesId(requested, inspected));

    private static ContainerInfo Row() => new() { Id = "fe1efb58e1ab", Name = "wslcd-ollama", StateValue = 3 };
}
