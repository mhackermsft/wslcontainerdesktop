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

    public static TheoryData<string, WslcFeature, string> Prunes => new()
    {
        { nameof(IWslcService.PruneContainersAsync), WslcFeature.ContainerPruneForce, "container prune" },
        { nameof(IWslcService.PruneImagesAsync), WslcFeature.ImagePruneForce, "image prune" },
        { nameof(IWslcService.PruneVolumesAsync), WslcFeature.VolumePruneForce, "volume prune --all" },
        { nameof(IWslcService.PruneNetworksAsync), WslcFeature.NetworkPruneForce, "network prune" },
    };

    [Theory]
    [MemberData(nameof(Prunes))]
    public async Task Prune_OnAPromptingEngine_PassesForceAndActuallyPrunes(string method, WslcFeature feature, string command)
    {
        // The issue #120 engine: without --force this stub waits for [y/N] like WSLC 2.9.12+.
        var service = Service(_stub.Prompt, feature, WslcCapabilitySupport.Supported);

        var result = await Prune(service, method);

        Assert.True(result.Success, result.ErrorText);
        Assert.Contains("PRUNED", result.StandardOutput);
        Assert.Equal([command + " --force"], _stub.Invocations(_stub.Prompt));
    }

    [Theory]
    [MemberData(nameof(Prunes))]
    public async Task Prune_OnALegacyEngine_RunsTheUnchangedCommand(string method, WslcFeature feature, string command)
    {
        var service = Service(_stub.Quiet, feature, WslcCapabilitySupport.Unsupported);

        var result = await Prune(service, method);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal([command], _stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [MemberData(nameof(Prunes))]
    public async Task Prune_WithUnknownSupport_LaunchesNothingAndExplainsWhy(string method, WslcFeature feature, string command)
    {
        _ = command;
        var service = Service(_stub.Quiet, feature, WslcCapabilitySupport.Unknown, "help probe timed out");

        var result = await Prune(service, method);

        Assert.False(result.Success);
        Assert.Contains("Nothing was pruned", result.StandardError);
        Assert.Contains("help probe timed out", result.StandardError);
        Assert.Empty(_stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [MemberData(nameof(Prunes))]
    public async Task Prune_WhenTheEnginePromptsAnyway_FailsInsteadOfHangingOrFakingSuccess(
        string method, WslcFeature feature, string command)
    {
        // An engine whose help omits --force but still prompts: stdin must be closed, and the
        // declined prompt (exit 0) must not be reported as a successful prune.
        var service = Service(_stub.Prompt, feature, WslcCapabilitySupport.Unsupported);

        var result = await Prune(service, method);

        Assert.False(result.Success);
        Assert.Contains("DECLINED", result.StandardOutput);
        Assert.Contains("asked for confirmation", result.StandardError);
        Assert.Equal([command], _stub.Invocations(_stub.Prompt));
    }

    [Fact]
    public async Task Prune_UsesTheCapabilitySnapshotsEngine_NotAStaleSettingsPath()
    {
        // The --force decision was made for the snapshot's engine; running another engine could mismatch.
        var service = Service(_stub.Quiet, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Fail);

        var result = await service.PruneContainersAsync().WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal(["container prune --force"], _stub.Invocations(_stub.Quiet));
        Assert.Empty(_stub.Invocations(_stub.Fail));
    }

    [Fact]
    public async Task Deletes_RunNonInteractively_SoAnUnexpectedPromptFailsFast()
    {
        var service = Service(_stub.Prompt, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Prompt);

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
        var service = Service(_stub.Quiet, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Quiet);

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
        var service = Service(_stub.Quiet, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Quiet);

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
        var service = Service(_stub.Quiet, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Quiet);

        var result = await service.RunContainerAsync(new RunContainerOptions
        {
            Image = "ubuntu", Detached = detached, Interactive = interactive, Command = "bash",
        }).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal([expected], _stub.Invocations(_stub.Quiet));
    }

    [Theory]
    [InlineData("-")]
    [InlineData(" - ")]
    public async Task Build_WithStdinDockerfile_IsRefusedWithoutLaunching(string dockerfile)
    {
        var service = Service(_stub.Quiet, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Quiet);

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
        var service = Service(_stub.Quiet, WslcFeature.ContainerPruneForce, WslcCapabilitySupport.Supported,
            settingsPath: _stub.Quiet);

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

    private static Task<CommandResult> Prune(IWslcService service, string method) => (method switch
    {
        nameof(IWslcService.PruneContainersAsync) => service.PruneContainersAsync(),
        nameof(IWslcService.PruneImagesAsync) => service.PruneImagesAsync(),
        nameof(IWslcService.PruneVolumesAsync) => service.PruneVolumesAsync(),
        nameof(IWslcService.PruneNetworksAsync) => service.PruneNetworksAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    }).WaitAsync(Guard);

    private static WslcService Service(string engine, WslcFeature feature, WslcCapabilitySupport support,
        string? diagnostic = null, string? settingsPath = null)
    {
        var features = new Dictionary<WslcFeature, WslcCapability>
        {
            [feature] = new(support, support == WslcCapabilitySupport.Supported ? null : diagnostic ?? $"{feature} {support}"),
        };
        var snapshot = new WslcCapabilities(engine, "2.9.12.0", features);
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
            new RestartSuppressionState());
    }
}
