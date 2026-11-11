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

public sealed class WslcSessionResourceActionsTests
{
    private static readonly WslcSessionInfo Session = new() { ID = 7, CreatorPid = 100, Name = "target" };
    private const string VolumeJson = """[{"Name":"data","CreatedAt":"2026-01-01T00:00:00Z"}]""";

    [Fact]
    public async Task VolumeRemovalRevalidatesItsCreationIdentity()
    {
        var calls = new List<string>();
        var actions = Create(() => [Session], Client(calls, VolumeJson));

        var target = await actions.PrepareVolumeRemovalAsync(Session, "data");
        var result = await actions.RemoveAsync(target);

        Assert.True(result.Success);
        Assert.Equal(["inspect-volume:data", "list-volumes", "inspect-volume:data", "remove-volume:data"], calls);
    }

    [Fact]
    public async Task RecreatedVolumeIsNotRemoved()
    {
        var calls = new List<string>();
        var changed = """[{"Name":"data","CreatedAt":"2026-02-01T00:00:00Z"}]""";
        var actions = Create(() => [Session], Client(calls, changed));
        var target = new WslcResourceActionTarget(Session, WslcResourceActionTarget.ResourceKind.Volume,
            "data", "data", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.RemoveAsync(target));

        Assert.Equal(["list-volumes", "inspect-volume:data"], calls);
    }

    [Fact]
    public async Task MismatchedVolumeNameAndIdAreRejectedBeforeAnyCommand()
    {
        var calls = new List<string>();
        var actions = Create(() => [Session], Client(calls, VolumeJson));
        var target = new WslcResourceActionTarget(Session, WslcResourceActionTarget.ResourceKind.Volume,
            "other", "data", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.RemoveAsync(target));

        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""[{"Name":"data","CreatedAt":"0001-01-01T00:00:00Z"}]""")]
    [InlineData("""[{"Name":"data","CreatedAt":"1970-01-01T00:00:00Z"}]""")]
    [InlineData("""[{"Name":"other","CreatedAt":"2026-01-01T00:00:00Z"}]""")]
    [InlineData("""[{"Name":"data","CreatedAt":"invalid"}]""")]
    [InlineData("""[{"Name":"data","CreatedAt":"2026-01-01T00:00:00Z"},{"Name":"data","CreatedAt":"2026-01-01T00:00:00Z"}]""")]
    public void MissingOrAmbiguousVolumeCreationIdentityIsRejected(string json)
    {
        Assert.Throws<InvalidOperationException>(() => WslcSessionResourceActions.ReadVolumeCreationTime(json, "data"));
    }

    [Fact]
    public async Task ImageRemovalTargetsOnlyTheCapturedSession()
    {
        var calls = new List<string>();
        var actions = Create(() => [Session], Client(calls, VolumeJson));
        var target = WslcResourceActionTarget.Capture(Session,
            new ImageInfo { Id = "same-image", Repository = "example", Tag = "latest" });

        Assert.True((await actions.RemoveAsync(target)).Success);
        Assert.Equal(["list-images", "remove-image:same-image"], calls);
    }

    [Fact]
    public async Task ChangedOrBuiltInNetworkIsNotRemoved()
    {
        var calls = new List<string>();
        var actions = Create(() => [Session], Client(calls, VolumeJson));
        var changed = new WslcResourceActionTarget(Session, WslcResourceActionTarget.ResourceKind.Network, "old-id", "app");
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.RemoveAsync(changed));
        Assert.Equal(["list-networks"], calls);
        calls.Clear();
        var builtin = new WslcResourceActionTarget(Session, WslcResourceActionTarget.ResourceKind.Network, "id", "bridge");
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.RemoveAsync(builtin));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task InspectionIsDiscardedWhenSessionChangesDuringTheRead()
    {
        var count = 0;
        var calls = new List<string>();
        var actions = Create(() => ++count == 1 ? [Session] : [Session with { ID = 8 }], Client(calls, VolumeJson));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.InspectAsync(Session, WslcResourceActionTarget.ResourceKind.Volume, "data"));
        Assert.Equal(["inspect-volume:data"], calls);
    }

    [Fact]
    public async Task InspectionRejectsUnknownSessionCreatorBeforeDiscovery()
    {
        var discovery = 0;
        var calls = new List<string>();
        var actions = Create(() => { discovery++; return [Session]; }, Client(calls, VolumeJson));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.InspectAsync(Session with { CreatorPid = 0 },
                WslcResourceActionTarget.ResourceKind.Volume, "data"));

        Assert.Equal(0, discovery);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    public async Task SharedGuardRejectsIncompleteGenerationEvenWhenDiscoveryMatches(int id, int creatorPid)
    {
        var expected = Session with { ID = id, CreatorPid = creatorPid };
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.GetSystemInfoAsync)
                ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [expected] } })
                : throw new InvalidOperationException(method.Name));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WslcSessionActionGuard.VerifyAsync(discovery, () => "engine", expected, "engine", default));
    }

    private static WslcSessionResourceActions Create(Func<IReadOnlyList<WslcSessionInfo>> sessions, IWslcService client)
    {
        var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
            {
                Server = new WslcServerInfo { Sessions = sessions() },
            }),
            _ => throw new InvalidOperationException(method.Name),
        });
        return new WslcSessionResourceActions(discovery, new WslcSessionContext(),
            (session, path, managed) =>
            {
                Assert.Equal(Session, session);
                Assert.Equal("engine", path);
                Assert.False(managed);
                return client;
            }, () => "engine", () => "wslc-cli-user", () => { });
    }

    private static IWslcService Client(List<string> calls, string volumeJson)
    {
        Task<CommandResult> Record(string method, object?[] args, string stdout = "")
        {
            calls.Add($"{method}:{args[0]}");
            return Task.FromResult(new CommandResult { StandardOutput = stdout });
        }

        return NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.InspectVolumeAsync) => Record("inspect-volume", args, volumeJson),
            nameof(IWslcService.RemoveVolumeAsync) => Record("remove-volume", args),
            nameof(IWslcService.RemoveImageAsync) => RemoveImage(args),
            nameof(IWslcService.RemoveNetworkAsync) => Record("remove-network", args),
            nameof(IWslcService.ListVolumesAsync) => ListVolumes(),
            nameof(IWslcService.ListImagesAsync) => ListImages(),
            nameof(IWslcService.ListNetworksAsync) => ListNetworks(),
            _ => throw new InvalidOperationException(method.Name),
        });

        Task<IReadOnlyList<VolumeInfo>> ListVolumes()
        {
            calls.Add("list-volumes");
            return Task.FromResult<IReadOnlyList<VolumeInfo>>([new VolumeInfo { Name = "data" }]);
        }

        Task<CommandResult> RemoveImage(object?[] args)
        {
            Assert.False((bool)args[1]!);
            return Record("remove-image", args);
        }

        Task<IReadOnlyList<ImageInfo>> ListImages()
        {
            calls.Add("list-images");
            return Task.FromResult<IReadOnlyList<ImageInfo>>(
                [new ImageInfo { Id = "same-image", Repository = "example", Tag = "latest" }]);
        }

        Task<IReadOnlyList<NetworkInfo>> ListNetworks()
        {
            calls.Add("list-networks");
            return Task.FromResult<IReadOnlyList<NetworkInfo>>([new NetworkInfo { Id = "new-id", Name = "app" }]);
        }
    }
}
