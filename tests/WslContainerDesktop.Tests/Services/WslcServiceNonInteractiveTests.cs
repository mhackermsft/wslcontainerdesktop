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

/// <summary>Drives the real WslcService against stub engines; nothing here touches a real WSLC session.</summary>
public sealed class WslcServiceNonInteractiveTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private readonly WslcStub _stub = new();

    public void Dispose() => _stub.Dispose();

    public static TheoryData<string, string> Prunes => new()
    {
        { nameof(IWslcService.PruneContainersAsync), "container prune" },
        { nameof(IWslcService.PruneImagesAsync), "image prune" },
        { nameof(IWslcService.PruneVolumesAsync), "volume prune --all" },
        { nameof(IWslcService.PruneNetworksAsync), "network prune" },
    };

    [Theory]
    [MemberData(nameof(Prunes))]
    public async Task Prune_OnAPromptingEngine_PassesForceAndActuallyPrunes(string method, string command)
    {
        var service = Service(_stub.Prompt);

        var result = await Prune(service, method);

        Assert.True(result.Success, result.ErrorText);
        Assert.Contains("PRUNED", result.StandardOutput);
        Assert.Equal([command + " --force"], _stub.Invocations(_stub.Prompt));
    }

    [Fact]
    public async Task Deletes_RunNonInteractively_SoAnUnexpectedPromptFailsFast()
    {
        var service = Service(_stub.Prompt);

        // force: false so the stub (like wslc) has no --force to skip its prompt on.
        var results = new[]
        {
            await service.RemoveContainerAsync("web", force: false).WaitAsync(Guard),
            await service.RemoveImageAsync("nginx:alpine", force: false).WaitAsync(Guard),
            await service.RemoveVolumeAsync("data").WaitAsync(Guard),
            await service.RemoveNetworkAsync("backend").WaitAsync(Guard),
        };

        Assert.All(results, result =>
        {
            Assert.False(result.Success);
            Assert.Contains("DECLINED", result.StandardOutput);
        });
        Assert.Equal(["remove web", "rmi nginx:alpine", "volume remove data", "network remove backend"],
            _stub.Invocations(_stub.Prompt));
    }

    [Fact]
    public async Task Deletes_KeepTheirArgumentsAndSucceedOnANonPromptingEngine()
    {
        var service = Service(_stub.Quiet);

        Assert.True((await service.RemoveContainerAsync("web").WaitAsync(Guard)).Success);
        Assert.True((await service.RemoveImageAsync("nginx:alpine").WaitAsync(Guard)).Success);
        Assert.True((await service.RemoveVolumeAsync("data").WaitAsync(Guard)).Success);
        Assert.True((await service.RemoveNetworkAsync("backend").WaitAsync(Guard)).Success);

        // Default force: true is unchanged for container and image deletes.
        Assert.Equal(["remove --force web", "rmi --force nginx:alpine", "volume remove data", "network remove backend"],
            _stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task Run_InteractiveInTheForeground_IsRefusedWithoutLaunching()
    {
        var service = Service(_stub.Quiet);

        var result = await service.RunContainerAsync(new RunContainerOptions
        {
            Image = "ubuntu", Detached = false, Interactive = true, Command = "bash",
        }).WaitAsync(Guard);

        Assert.False(result.Success);
        Assert.Equal(RunContainerOptions.ForegroundInteractiveError, result.StandardError);
        Assert.Empty(_stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [InlineData(true, true, "run -d -i ubuntu bash")]
    [InlineData(true, false, "run -d ubuntu bash")]
    [InlineData(false, false, "run ubuntu bash")]
    public async Task Run_EveryOtherCombination_IsUnchanged(bool detached, bool interactive, string expected)
    {
        var service = Service(_stub.Quiet);

        var result = await service.RunContainerAsync(new RunContainerOptions
        {
            Image = "ubuntu", Detached = detached, Interactive = interactive, Command = "bash",
        }).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal([expected], _stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task Restart_UsesNativeRestartWithTimeoutAndSignal()
    {
        var service = Service(_stub.Quiet);

        var result = await service.RestartContainerAsync("web", 10, "SIGQUIT").WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal(["restart -t 10 -s SIGQUIT web"], _stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task PullAndPush_AllTags_BuildDockerParityArguments()
    {
        var service = Service(_stub.Quiet);

        // WSLC 3.0.1 rejects a tag with --all-tags for both pull and push, so the tag is stripped.
        Assert.True((await service.PullImageAsync("ghcr.io/owner/app:1", allTags: true).WaitAsync(Guard)).Success);
        Assert.True((await service.PullImageAsync("localhost:5000/team/app@sha256:abc", _ => { }, allTags: true).WaitAsync(Guard)).Success);
        Assert.True((await service.PushImageAsync("ghcr.io/owner/app:1", allTags: true).WaitAsync(Guard)).Success);
        Assert.True((await service.PullImageAsync("localhost:5000/team/app:2").WaitAsync(Guard)).Success);

        Assert.Equal(
            ["pull --all-tags ghcr.io/owner/app", "pull --all-tags localhost:5000/team/app", "push --all-tags ghcr.io/owner/app",
             "pull localhost:5000/team/app:2"],
            _stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task SaveLoadImportExport_UseNativeArguments()
    {
        var service = Service(_stub.Quiet);

        Assert.True((await service.SaveImagesAsync(["app:1", "app:2"], @"C:\temp\images.tar").WaitAsync(Guard)).Success);
        Assert.True((await service.LoadImageAsync(@"C:\temp\images.tar").WaitAsync(Guard)).Success);
        Assert.True((await service.ImportImageAsync(@"C:\temp\rootfs.tar", "localhost/imported:latest").WaitAsync(Guard)).Success);
        Assert.True((await service.ExportContainerAsync("web", @"C:\temp\web.tar").WaitAsync(Guard)).Success);

        Assert.Equal([
            @"save --output C:\temp\images.tar app:1 app:2",
            @"load --input C:\temp\images.tar --quiet",
            @"import C:\temp\rootfs.tar localhost/imported:latest",
            @"export --output C:\temp\web.tar web",
        ], _stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task LogsInspectAndListSize_UseRequestedFlags()
    {
        var service = Service(_stub.Json);

        await service.ListContainersAsync(all: true, includeSize: true).WaitAsync(Guard);
        await service.ListImagesAsync(showAll: true).WaitAsync(Guard);

        var quietService = Service(_stub.Quiet);
        Assert.True((await quietService.GetLogsAsync("web", 42, details: true, timestamps: true).WaitAsync(Guard)).Success);
        Assert.True((await quietService.InspectContainerAsync("web", includeSize: true).WaitAsync(Guard)).Success);

        Assert.Equal(["list --format json --all --size", "images --digests --format json --all"],
            _stub.Invocations(_stub.Json));
        Assert.Equal(["logs --details --timestamps --tail 42 web", "inspect --type container --size web"],
            _stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public void StripTag_KeepsRegistryPorts()
    {
        Assert.Equal("localhost:5000/team/app", WslcService.StripTag("localhost:5000/team/app:1.0"));
        Assert.Equal("ghcr.io/team/app", WslcService.StripTag("ghcr.io/team/app@sha256:abc"));
        Assert.Equal("ubuntu", WslcService.StripTag("ubuntu"));
    }

    [Fact]
    public async Task CreateNetwork_EmitsDriverOptionsLabelsAndInternal()
    {
        var service = Service(_stub.Quiet);

        var result = await service.CreateNetworkAsync(
            "app-net",
            "bridge",
            ["com.example.mode=fast"],
            new Dictionary<string, string> { ["com.example.owner"] = "tests" },
            subnet: "172.28.0.0/24",
            gateway: "172.28.0.1",
            ipRange: "172.28.0.128/25",
            internalNetwork: true).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal(
            ["network create --driver bridge --opt com.example.mode=fast --label com.example.owner=tests --internal --subnet 172.28.0.0/24 --gateway 172.28.0.1 --ip-range 172.28.0.128/25 app-net"],
            _stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [InlineData("-")]
    [InlineData(" - ")]
    public async Task Build_WithStdinDockerfile_IsRefusedWithoutLaunching(string dockerfile)
    {
        var service = Service(_stub.Quiet);

        var result = await service.BuildImageAsync("ctx", "app:1", dockerfile).WaitAsync(Guard);

        Assert.False(result.Success);
        Assert.Equal(WslcService.StdinDockerfileError, result.StandardError);
        Assert.Empty(_stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [InlineData(null, "build -t app:1 ctx")]
    [InlineData("Dockerfile.dev", "build -t app:1 -f Dockerfile.dev ctx")]
    [InlineData("docker/-", "build -t app:1 -f docker/- ctx")]
    public async Task Build_WithADockerfilePath_IsUnchanged(string? dockerfile, string expected)
    {
        var service = Service(_stub.Quiet);

        var result = await service.BuildImageAsync("ctx", "app:1", dockerfile).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal([expected], _stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [InlineData("-", true)]
    [InlineData(" -\t", true)]
    [InlineData("--", false)]
    [InlineData("./-", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsStdinDockerfile_MatchesOnlyTheBareDash(string? dockerfile, bool expected) =>
        Assert.Equal(expected, WslcService.IsStdinDockerfile(dockerfile));

    [Theory]
    [InlineData(nameof(IWslcService.PullImageAsync))]
    [InlineData(nameof(IWslcService.PushImageAsync))]
    [InlineData(nameof(IWslcService.RunContainerAsync))]
    public async Task RegistryAllowlist_BlocksRegistryMutationsBeforeLaunch(string method)
    {
        var policy = new StaticPolicy(new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, ["ghcr.io"]));
        var service = Service(_stub.Quiet, policy: policy);

        var result = method switch
        {
            nameof(IWslcService.PullImageAsync) => await service.PullImageAsync("docker.io/library/nginx"),
            nameof(IWslcService.PushImageAsync) => await service.PushImageAsync("docker.io/library/nginx"),
            nameof(IWslcService.RunContainerAsync) => await service.RunContainerAsync(new RunContainerOptions
            {
                Image = "docker.io/library/nginx",
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        Assert.False(result.Success);
        Assert.Contains("not on your organization's approved registry list", result.ErrorText);
        Assert.Empty(_stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task Build_WhenAllowlistConfigured_IsRefusedBeforeLaunch()
    {
        var policy = new StaticPolicy(new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, ["ghcr.io"]));
        var service = Service(_stub.Quiet, policy: policy);

        var result = await service.BuildImageAsync("ctx", "ghcr.io/owner/app:1", null).WaitAsync(Guard);

        Assert.False(result.Success);
        Assert.Equal(WslRegistryPolicyGuard.BuildBlockedMessage, result.ErrorText);
        Assert.Empty(_stub.Invocations(_stub.Quiet));
    }

    [Fact]
    public async Task RegistryAllowlist_DoesNotBlockLocalImageArchiveOperations()
    {
        var policy = new StaticPolicy(new WslRegistryAllowlist(WslRegistryAllowlistState.Configured, ["ghcr.io"]));
        var service = Service(_stub.Quiet, policy: policy);

        Assert.True((await service.SaveImagesAsync(["docker.io/library/nginx:local"], @"C:\temp\images.tar").WaitAsync(Guard)).Success);
        Assert.True((await service.LoadImageAsync(@"C:\temp\images.tar").WaitAsync(Guard)).Success);
        Assert.True((await service.ImportImageAsync(@"C:\temp\rootfs.tar", "docker.io/library/imported:local").WaitAsync(Guard)).Success);
        Assert.True((await service.TagImageAsync("sha256:abc", "docker.io/library/retagged:local").WaitAsync(Guard)).Success);

        Assert.Equal([
            @"save --output C:\temp\images.tar docker.io/library/nginx:local",
            @"load --input C:\temp\images.tar --quiet",
            @"import C:\temp\rootfs.tar docker.io/library/imported:local",
            "tag sha256:abc docker.io/library/retagged:local",
        ], _stub.Invocations(_stub.Quiet));
    }

    private static Task<CommandResult> Prune(IWslcService service, string method) => (method switch
    {
        nameof(IWslcService.PruneContainersAsync) => service.PruneContainersAsync(),
        nameof(IWslcService.PruneImagesAsync) => service.PruneImagesAsync(),
        nameof(IWslcService.PruneVolumesAsync) => service.PruneVolumesAsync(),
        nameof(IWslcService.PruneNetworksAsync) => service.PruneNetworksAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    }).WaitAsync(Guard);

    private static WslcService Service(string engine, string? settingsPath = null, IWslPolicyService? policy = null)
    {
        var snapshot = new WslcCapabilities(engine, "3.0.1.0", new Dictionary<WslcFeature, WslcCapability>());
        var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) => method.Name switch
        {
            nameof(IWslcCapabilitiesService.GetAsync) => Task.FromResult(snapshot),
            nameof(IWslcCapabilitiesService.Invalidate) => null,
            _ => throw new NotSupportedException(method.Name),
        });
        var path = settingsPath ?? engine;
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
        {
            "get_WslcPath" => path,
            "get_HealthChecks" => new List<HealthCheckConfig>(),
            "add_Changed" or "remove_Changed" => null,
            _ => throw new NotSupportedException(method.Name),
        });
        return new WslcService(new ProcessRunner(settings), NullLogger<WslcService>.Instance, capabilities, settings,
            new RestartSuppressionState(), policy);
    }

    /// <summary>
    /// Supplies a fixed registry allowlist so <c>WslcService</c> command tests can focus on argument generation.
    /// </summary>
    private sealed class StaticPolicy(WslRegistryAllowlist allowlist) : IWslPolicyService
    {
        public WslPolicySnapshot GetPolicy() => new(true, true, allowlist);
    }
}
