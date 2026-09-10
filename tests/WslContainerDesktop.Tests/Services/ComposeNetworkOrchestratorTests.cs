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

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeNetworkOrchestratorTests
{
    internal static WslcCapabilities Capabilities(WslcCapabilitySupport support = WslcCapabilitySupport.Supported) =>
        new("wslc.exe", "fixture", new Dictionary<WslcFeature, WslcCapability>
        {
            [WslcFeature.NetworkConnect] = new(support, "fixture diagnostic"),
            [WslcFeature.NetworkDisconnect] = new(support, "fixture diagnostic"),
        });

    internal static RunContainerOptions Options(string name = "demo_web") => new()
    {
        Name = name, Image = "fixture", Network = "a", Networks = ["a", "b"],
        NetworkAttachments =
        [
            new() { Network = "a", Aliases = ["web"], Ipv4Address = "172.28.0.2" },
            new() { Network = "b", Aliases = ["private", "web"], Ipv4Address = "172.29.0.2" },
        ],
        Labels = { [ComposeProject.ProjectLabel] = "demo", [ComposeProject.ServiceLabel] = "web" },
    };

    [Fact]
    public async Task CreatesConnectsThenStartsExactlyOnce()
    {
        var engine = new Engine();
        var id = await engine.Orchestrator.CreateAndStartAsync(Options(), default);
        Assert.Equal("demo_web", id);
        Assert.Equal(["create:demo_web", "connect:b", "start:demo_web"], engine.Mutations);
        Assert.Equal(["private", "web"], engine.Endpoints["demo_web"][1].Aliases);
        Assert.Equal("172.29.0.2", engine.Endpoints["demo_web"][1].Ipv4Address);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRecreateResumesOnlyTheStopThatPrecededIt(bool laterStop)
    {
        var suppression = new RestartSuppressionState();
        suppression.Suppress("demo_web");
        var engine = new Engine
        {
            BeforeStart = () =>
            {
                if (laterStop)
                    suppression.Suppress("demo_web");
            },
        };
        var orchestrator = new ComposeNetworkOrchestrator(engine.Service, NullLogger.Instance, suppression);
        await orchestrator.CreateAndStartAsync(Options(), default);
        Assert.Equal(laterStop, suppression.IsSuppressed("demo_web"));
        Assert.False(engine.LastStartWasExplicit);
    }

    [Fact]
    public async Task FailedNetworkRecreateDoesNotClearManualStop()
    {
        var suppression = new RestartSuppressionState();
        suppression.Suppress("demo_web");
        var engine = new Engine { FailNetwork = "b" };
        var orchestrator = new ComposeNetworkOrchestrator(engine.Service, NullLogger.Instance, suppression);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.CreateAndStartAsync(Options(), default));
        Assert.True(suppression.IsSuppressed("demo_web"));
    }

    [Fact]
    public async Task FailedSecondEndpointNeverStartsAndRemovesOnlyCreatedContainer()
    {
        var engine = new Engine { FailNetwork = "b" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.Orchestrator.CreateAndStartAsync(Options(), default));
        Assert.Contains("'b'", error.Message);
        Assert.Equal(["create:demo_web", "connect:b", "remove:demo_web"], engine.Mutations);
        Assert.Empty(engine.Containers);
    }

    [Fact]
    public async Task CancellationCleansPartialContainerWithIndependentToken()
    {
        using var cts = new CancellationTokenSource();
        var engine = new Engine { CancelOnConnect = cts };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.Orchestrator.CreateAndStartAsync(Options(), cts.Token));
        Assert.Contains("remove:demo_web", engine.Mutations);
        Assert.Empty(engine.Containers);
    }

    [Fact]
    public async Task FailedCreateCannotDeleteSameNameUnrelatedContainer()
    {
        var engine = new Engine();
        var unrelated = Options();
        unrelated.Labels.Clear();
        engine.Add(unrelated);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.Orchestrator.CreateAndStartAsync(Options(), default));
        Assert.Equal(["create:demo_web"], engine.Mutations);
        Assert.Single(engine.Containers);
    }

    [Fact]
    public async Task ReconcileIsIdempotentAndPreservesUnrelatedEndpoints()
    {
        var engine = new Engine();
        engine.Add(Options(), allEndpoints: true);
        engine.Endpoints["demo_web"].Add(new() { Network = "unrelated" });
        await engine.Orchestrator.ReconcileAsync("demo_web", Options(), Capabilities(), default);
        await engine.Orchestrator.ReconcileAsync("demo_web", Options(), Capabilities(), default);
        Assert.Empty(engine.Mutations);
        Assert.Equal(3, engine.Endpoints["demo_web"].Count);
    }

    [Fact]
    public async Task ReconcileRollsBackConfirmedEndpointsAndReportsUnconfirmedMutation()
    {
        var engine = new Engine { FailNetwork = "c", MutateBeforeFailure = true };
        var options = Options();
        options.Networks.Add("c");
        engine.Add(options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.Orchestrator.ReconcileAsync("demo_web", options, Capabilities(), default));
        Assert.Contains("'c'", error.Message);
        Assert.Contains("cleanup ownership is unresolved", error.Message);
        Assert.Equal(["connect:b", "connect:c", "disconnect:b"], engine.Mutations);
        Assert.Equal(["a", "c"], engine.Endpoints["demo_web"].Select(e => e.Network));
    }

    [Fact]
    public async Task ReconcileRollsBackConfirmedEndpointsAfterNonMutatingFailure()
    {
        var engine = new Engine { FailNetwork = "c" };
        var options = Options();
        options.Networks.Add("c");
        engine.Add(options);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.Orchestrator.ReconcileAsync("demo_web", options, Capabilities(), default));
        Assert.Equal(["connect:b", "connect:c", "disconnect:b"], engine.Mutations);
        Assert.Equal("a", Assert.Single(engine.Endpoints["demo_web"]).Network);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcilePreservesRacingExternalEndpointOnFailureOrCancellation(bool cancel)
    {
        using var cts = new CancellationTokenSource();
        var engine = new Engine { FailNetwork = "c" };
        var options = Options();
        options.Networks.Add("c");
        engine.Add(options);
        engine.BeforeConnect = (network, id) =>
        {
            if (network != "c")
                return;
            engine.Endpoints[id].Add(new() { Network = network, Aliases = ["external-owner"] });
            if (cancel)
                cts.Cancel();
        };

        Exception error = cancel
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.Orchestrator.ReconcileAsync("demo_web", options, Capabilities(), cts.Token))
            : await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.Orchestrator.ReconcileAsync("demo_web", options, Capabilities(), cts.Token));

        Assert.Contains("cleanup ownership is unresolved", error.Message);
        Assert.Equal(["connect:b", "connect:c", "disconnect:b"], engine.Mutations);
        Assert.Equal(["external-owner"], engine.Endpoints["demo_web"].Single(e => e.Network == "c").Aliases);
    }

    [Fact]
    public async Task AliasMismatchFailsBeforeAnyMutation()
    {
        var engine = new Engine();
        engine.Add(Options(), allEndpoints: true);
        engine.Endpoints["demo_web"][1].Aliases.Clear();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.Orchestrator.ReconcileAsync("demo_web", Options(), Capabilities(), default));
        Assert.Contains("'b'", error.Message);
        Assert.Empty(engine.Mutations);
    }

    [Fact]
    public void LegacyFallbackIsExplicitAndUnknownDoesNotDowngrade()
    {
        var options = Options();
        Assert.False(ComposeNetworkOrchestrator.SelectNative(options,
            Capabilities(WslcCapabilitySupport.Unsupported), out var warning));
        Assert.Contains("Only network 'a'", warning);
        Assert.Contains("'b'", warning);
        Assert.Equal(2, options.NetworkAttachments.Count);
        var error = Assert.Throws<InvalidOperationException>(() =>
            ComposeNetworkOrchestrator.SelectNative(options, Capabilities(WslcCapabilitySupport.Unknown), out _));
        Assert.Contains("fixture diagnostic", error.Message);
        options.NetworkMode = "none";
        Assert.False(ComposeNetworkOrchestrator.SelectNative(options, Capabilities(WslcCapabilitySupport.Unknown), out _));
    }

    internal sealed class Engine
    {
        public Dictionary<string, RunContainerOptions> Containers { get; } = new();
        public Dictionary<string, List<NetworkAttachment>> Endpoints { get; } = new();
        public Dictionary<string, string?> Networks { get; } = new();
        public Dictionary<string, string?> Volumes { get; } = new();
        public Dictionary<string, List<object>?> Mounts { get; } = new();
        public Dictionary<string, ContainerState> States { get; } = new();
        public Dictionary<string, int> ExitCodes { get; } = new();
        public Dictionary<string, string> ContainerIds { get; } = new();
        public Dictionary<string, string> InspectionErrors { get; } = new();
        public Dictionary<string, List<PortMapping>> PublishedPorts { get; } = new();
        public Dictionary<string, string> NetworkInspectionOverrides { get; } = new();
        public List<ImageInfo> Images { get; } = [new() { Id = "sha256:fixture-v1", Repository = "fixture", Tag = "latest" }];
        public List<string> Mutations { get; } = new();
        public List<string> Reads { get; } = new();
        public string? FailNetwork { get; init; }
        public string? FailRun { get; init; }
        public bool FailRunAfterCreate { get; set; }
        public string? FailStop { get; set; }
        public string? FailStart { get; set; }
        public string? FailRemove { get; set; }
        public bool FailPull { get; set; }
        public bool FailBuild { get; set; }
        public string? FailCreateVolume { get; set; }
        public string? FailRemoveVolume { get; set; }
        public string? VolumeInspectionError { get; set; }
        public Exception? ImageInventoryError { get; set; }
        public string NextImageId { get; set; } = "sha256:fixture-v2";
        public bool MutateBeforeFailure { get; init; }
        public CancellationTokenSource? CancelOnConnect { get; init; }
        public Func<Task>? BeforeList { get; set; }
        public Action<string, string>? BeforeConnect { get; set; }
        public Action<string>? AfterStart { get; set; }
        public Action<string>? AfterRun { get; set; }
        public Action<string>? AfterStop { get; set; }
        public Action? BeforeStart { get; init; }
        public bool LastStartWasExplicit { get; private set; }
        public long? LastRunMaximumStopVersion { get; private set; }
        public IWslcService Service { get; }
        public ComposeNetworkOrchestrator Orchestrator => new(Service, NullLogger.Instance);

        public Engine()
        {
            Service = NetworkTestProxy.Create<IWslcService>((method, args) =>
            {
                var ct = args.OfType<CancellationToken>().LastOrDefault();
                ct.ThrowIfCancellationRequested();
                switch (method.Name)
                {
                    case nameof(IWslcService.ListContainersAsync):
                        return ListAsync();
                    case nameof(IWslcService.ListImagesAsync):
                        Reads.Add("images");
                        return ImageInventoryError is { } error
                            ? Task.FromException<IReadOnlyList<ImageInfo>>(error)
                            : Task.FromResult<IReadOnlyList<ImageInfo>>(Images.ToList());
                    case nameof(IWslcService.PullImageAsync):
                    case nameof(IWslcService.BuildImageAsync):
                    {
                        var build = method.Name == nameof(IWslcService.BuildImageAsync);
                        var reference = (string)args[build ? 1 : 0]!;
                        Mutations.Add($"{(build ? "build" : "pull")}:{reference}");
                        if (build ? FailBuild : FailPull)
                            return Result(false, error: "fixture image preparation failed");
                        var separator = reference.LastIndexOf(':');
                        var repository = separator > reference.LastIndexOf('/') ? reference[..separator] : reference;
                        var tag = separator > reference.LastIndexOf('/') ? reference[(separator + 1)..] : "latest";
                        Images.RemoveAll(i => i.Repository == repository && i.Tag == tag);
                        Images.Add(new() { Id = NextImageId, Repository = repository, Tag = tag });
                        return Result(true);
                    }
                    case nameof(IWslcService.CreateContainerAsync):
                    case nameof(IWslcService.RunContainerAsync):
                    {
                        var options = (RunContainerOptions)args[0]!;
                        var create = method.Name == nameof(IWslcService.CreateContainerAsync);
                        if (!create)
                            LastRunMaximumStopVersion = (long)args[2]!;
                        Mutations.Add($"{(create ? "create" : "run")}:{options.Name}");
                        if (Containers.ContainsKey(options.Name!) || options.Name == FailRun)
                        {
                            return Result(false, error: "creation failed");
                        }
                        Add(options);
                        States[options.Name!] = create ? ContainerState.Created : ContainerState.Running;
                        if (!create)
                            AfterRun?.Invoke(options.Name!);
                        if (!create && FailRunAfterCreate)
                            return Result(false, error: "fixture run failed after creating the container");
                        return Result(true, options.Name!);
                    }
                    case nameof(IWslcService.InspectContainerAsync):
                    {
                        var name = ResolveName((string)args[0]!);
                        Reads.Add("inspect:" + name);
                        if (InspectionErrors.TryGetValue(name, out var inspectionError))
                            return Result(false, error: inspectionError);
                        if (!Containers.TryGetValue(name, out var options))
                        {
                            return Result(false, error: "WSLC_E_CONTAINER_NOT_FOUND");
                        }
                        return Result(true, JsonSerializer.Serialize(new[]
                        {
                            new
                            {
                                Id = ContainerIds[name], Config = new { options.Labels },
                                State = new { ExitCode = ExitCodes.GetValueOrDefault(name) },
                                Mounts = Mounts[name],
                                NetworkSettings = new
                                {
                                    Networks = Endpoints[name].ToDictionary(e => e.Network,
                                        e => new { e.Aliases, IPAddress = "", IPAMConfig = new { IPv4Address = e.Ipv4Address } }),
                                },
                            },
                        }));
                    }
                    case nameof(IWslcService.ConnectNetworkAsync):
                    {
                        var endpoint = (NetworkAttachment)args[0]!;
                        var id = ResolveName((string)args[1]!);
                        Mutations.Add("connect:" + endpoint.Network);
                        BeforeConnect?.Invoke(endpoint.Network, id);
                        CancelOnConnect?.Cancel();
                        ct.ThrowIfCancellationRequested();
                        if (endpoint.Network != FailNetwork || MutateBeforeFailure)
                        {
                            Endpoints[id].Add(endpoint.Clone());
                        }
                        return Result(endpoint.Network != FailNetwork, error: "fixture connection failure");
                    }
                    case nameof(IWslcService.DisconnectNetworkAsync):
                        Mutations.Add("disconnect:" + args[0]);
                        Endpoints[ResolveName((string)args[1]!)].RemoveAll(n => n.Network == (string)args[0]!);
                        return Result(true);
                    case nameof(IWslcService.StartContainerAsync):
                        LastStartWasExplicit = (bool)args[2]!;
                        BeforeStart?.Invoke();
                        Mutations.Add("start:" + args[0]);
                        var startedName = ResolveName((string)args[0]!);
                        if (startedName == FailStart)
                            return Result(false, error: "fixture start failed");
                        States[startedName] = ContainerState.Running;
                        AfterStart?.Invoke(startedName);
                        return Result(true);
                    case nameof(IWslcService.StopContainerAsync):
                        Mutations.Add("stop:" + args[0]);
                        var stoppedName = ResolveName((string)args[0]!);
                        if (stoppedName == FailStop)
                            return Result(false, error: "fixture stop failed");
                        States[stoppedName] = ContainerState.Stopped;
                        AfterStop?.Invoke(stoppedName);
                        return Result(true);
                    case nameof(IWslcService.RemoveContainerAsync):
                        Mutations.Add("remove:" + args[0]);
                        var removedName = ResolveName((string)args[0]!);
                        if (removedName == FailRemove)
                            return Result(false, error: "fixture remove failed");
                        Containers.Remove(removedName);
                        Endpoints.Remove(removedName);
                        States.Remove(removedName);
                        ExitCodes.Remove(removedName);
                        ContainerIds.Remove(removedName);
                        Mounts.Remove(removedName);
                        return Result(true);
                    case nameof(IWslcService.InspectVolumeAsync):
                        Reads.Add("volume:" + args[0]);
                        if (VolumeInspectionError is { } volumeError)
                            return Result(false, error: volumeError);
                        return Volumes.TryGetValue((string)args[0]!, out var volumeOwner)
                            ? Result(true, JsonSerializer.Serialize(new
                            {
                                Name = (string)args[0]!,
                                Labels = new Dictionary<string, string?> { [ComposeProject.ProjectLabel] = volumeOwner },
                            }))
                            : Result(false, error: "WSLC_E_VOLUME_NOT_FOUND");
                    case nameof(IWslcService.CreateVolumeAsync):
                        Mutations.Add("volume-create:" + args[0]);
                        if ((string)args[0]! == FailCreateVolume)
                            return Result(false, error: "fixture volume creation failed");
                        Volumes[(string)args[0]!] = ((IReadOnlyDictionary<string, string>)args[3]!)[ComposeProject.ProjectLabel];
                        return Result(true);
                    case nameof(IWslcService.RemoveVolumeAsync):
                        Mutations.Add("volume-remove:" + args[0]);
                        if ((string)args[0]! == FailRemoveVolume)
                            return Result(false, error: "fixture volume removal failed");
                        Volumes.Remove((string)args[0]!);
                        return Result(true);
                    case nameof(IWslcService.InspectNetworkAsync):
                        Reads.Add("network:" + args[0]);
                        if (NetworkInspectionOverrides.TryGetValue((string)args[0]!, out var networkJson))
                            return Result(true, networkJson);
                        return Networks.TryGetValue((string)args[0]!, out var owner)
                            ? Result(true, JsonSerializer.Serialize(new { Labels = new Dictionary<string, string?> { [ComposeProject.ProjectLabel] = owner } }))
                            : Result(false, error: "WSLC_E_NETWORK_NOT_FOUND");
                    case nameof(IWslcService.CreateNetworkAsync):
                        Mutations.Add("network-create:" + args[0]);
                        Networks[(string)args[0]!] = ((IReadOnlyDictionary<string, string>)args[3]!)[ComposeProject.ProjectLabel];
                        return Result(true);
                    case nameof(IWslcService.RemoveNetworkAsync):
                        Mutations.Add("network-remove:" + args[0]);
                        Networks.Remove((string)args[0]!);
                        return Result(true);
                    default:
                        throw new InvalidOperationException("Unexpected engine call: " + method.Name);
                }
            });
        }

        public void Add(RunContainerOptions options, bool allEndpoints = false)
        {
            Containers[options.Name!] = options.Clone();
            ContainerIds[options.Name!] = options.Name!;
            States[options.Name!] = ContainerState.Running;
            Mounts[options.Name!] = [];
            Endpoints[options.Name!] = options.GetNetworkAttachments().Take(allEndpoints ? int.MaxValue : 1).ToList();
        }

        private async Task<IReadOnlyList<ContainerInfo>> ListAsync()
        {
            Reads.Add("containers");
            if (BeforeList is not null)
            {
                await BeforeList();
            }
            return Containers.Keys.Select(n => new ContainerInfo
            {
                Id = ContainerIds[n], Name = n, StateValue = (int)States[n],
                PortsKnown = PublishedPorts.ContainsKey(n),
                Ports = PublishedPorts.GetValueOrDefault(n) ?? [],
            }).ToList();
        }

        private string ResolveName(string id) =>
            ContainerIds.FirstOrDefault(pair => pair.Value == id).Key ?? id;

        private static Task<CommandResult> Result(bool success, string output = "", string error = "") =>
            Task.FromResult(new CommandResult { ExitCode = success ? 0 : 1, StandardOutput = output, StandardError = error });
    }
}
