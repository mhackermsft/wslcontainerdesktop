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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AssistantObservationContractTests
{
    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    [InlineData(WslcCapabilitySupport.Unknown)]
    public async Task SharedContextAndRealQueryUseCurrentSanitizedEvidence(WslcCapabilitySupport support)
    {
        var f = new Fixture { Support = support };
        var h = new AiContractHarness(f.Tools, engineCapabilities: f.Capabilities);
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            Assert.Contains($"NetworkConnect: {support}", h.Provider.Requests[^1][0].Content);
            f.Support = support == WslcCapabilitySupport.Supported ? WslcCapabilitySupport.Unsupported : WslcCapabilitySupport.Supported;
            var result = await invoke(AiContractHarness.Call("engine_capabilities", "{}"), ct);
            Assert.Contains($"NetworkConnect: {f.Support}", result);
            Assert.DoesNotContain("private-", result);
            return result;
        });
        await h.Assistant.SendAsync("capabilities");
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("again");
        Assert.Contains($"NetworkConnect: {f.Support}", h.Provider.Requests[^1][0].Content);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(h.Provider.Requests));
        Assert.Equal(0, f.Inspects);
    }

    [Fact]
    public async Task UnavailableContextAndClusterRemainUnknownNotAbsent()
    {
        var f = new Fixture { CapabilityError = true, ClusterError = true };
        var tools = f.Tools();
        var definitions = await tools.GetDefinitionsAsync(default);
        Assert.Contains(definitions, d => d.Name == "k8s_status");
        Assert.DoesNotContain(definitions, d => d.Name == "cluster_start");
        foreach (var name in new[] { "engine_capabilities", "k8s_status" })
        {
            var output = await f.Execute(name);
            Assert.Contains("unavailable", output);
            Assert.Contains("Unknown", output);
            Assert.DoesNotContain("private-", output);
        }
    }

    [Theory]
    [InlineData(ClusterState.NotInstalled, false)]
    [InlineData(ClusterState.Unknown, false)]
    [InlineData(ClusterState.Stopped, true)]
    [InlineData(ClusterState.Running, true)]
    public async Task ClusterMutationExposureRequiresPositiveInstalledEvidence(ClusterState state, bool expose)
    {
        var f = new Fixture { Cluster = state };
        var definitions = await f.Tools().GetDefinitionsAsync(default);
        Assert.Equal(expose, definitions.Any(d => d.Name == "cluster_start"));
        Assert.Contains(state.ToString(), await f.Execute("k8s_status"));
    }

    [Theory]
    [InlineData(NativeHealthState.Healthy, 0, "Healthy")]
    [InlineData(NativeHealthState.Unhealthy, 0, "Unhealthy")]
    [InlineData(NativeHealthState.Unknown, 0, "Unknown")]
    [InlineData(NativeHealthState.Absent, 0, "Absent")]
    [InlineData(NativeHealthState.Disabled, 0, "Disabled")]
    [InlineData(NativeHealthState.Healthy, 60, "Unknown")]
    public async Task CachedHealthPreservesAgeAndUnknownWithoutAnyEngineRead(NativeHealthState state, int age, string expected)
    {
        var f = new Fixture();
        f.Health = new("engine", DateTimeOffset.UtcNow, true,
            [new("id", "app", ContainerState.Running, state, DateTimeOffset.UtcNow.AddSeconds(-age))]);
        using var json = JsonDocument.Parse(await f.Execute("get_health_observations"));
        var row = json.RootElement.GetProperty("containers")[0];
        Assert.Equal(expected, row.GetProperty("state").GetString());
        Assert.Equal(age > 15 ? "stale" : "observed", row.GetProperty("freshness").GetString());
        Assert.True(row.GetProperty("ageSeconds").GetDouble() >= age);
        Assert.Equal(0, f.Inspects);
        Assert.Equal(0, f.Inventories);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unavailable")]
    [InlineData("changed")]
    public async Task HealthCannotLeakPreviousEngineCertainty(string mode)
    {
        var f = new Fixture();
        if (mode != "missing")
            f.Health = new(mode == "changed" ? "other" : "engine", DateTimeOffset.UtcNow, mode != "unavailable",
                [new("id", "app", ContainerState.Running, NativeHealthState.Healthy, DateTimeOffset.UtcNow)]);
        var output = await f.Execute("get_health_observations");
        Assert.Contains("unavailable", output);
        Assert.DoesNotContain("Healthy", output);
        Assert.Equal(0, f.Inspects);
    }

    [Theory]
    [InlineData(0, 1, "Healthy")]
    [InlineData(60, 1, "Unknown")]
    [InlineData(0, 2, "Unknown")]
    public async Task AppObservationsRequireFreshMatchingInstanceWithoutProbes(int age, ulong generation, string expected)
    {
        var f = new Fixture
        {
            Health = new("engine", DateTimeOffset.UtcNow, true,
                [new("id", "app", ContainerState.Running, NativeHealthState.Absent, DateTimeOffset.UtcNow, 1)]),
            AppHealth = [new() { ContainerId = "id", ContainerName = "app", ContainerGeneration = generation,
                ObservedAt = DateTimeOffset.UtcNow.AddSeconds(-age), State = ContainerHealthState.Healthy,
                Detail = "private-probe-command" }],
        };
        var output = await f.Execute("get_health_observations");
        using var json = JsonDocument.Parse(output);
        Assert.Equal(expected, json.RootElement.GetProperty("appObservations")[0].GetProperty("state").GetString());
        Assert.DoesNotContain("private-", output);
        Assert.Equal(0, f.Inspects);
        Assert.Equal(0, f.Inventories);
    }

    [Theory]
    [InlineData("exact", "Exact")]
    [InlineData("partial", "Partial")]
    [InlineData("unknown", "Unknown")]
    [InlineData("empty", "Unused")]
    [InlineData("estimated", "Estimated")]
    public async Task SharedVolumeResolverIncludesStoppedContainersAndNeverInventsUnused(string mode, string expected)
    {
        var f = new Fixture { VolumeMode = mode };
        using var json = JsonDocument.Parse(await f.Execute("get_volume_usage"));
        Assert.Equal(expected, json.RootElement.GetProperty("volumes")[0].GetProperty("usage").GetString());
        Assert.Equal(mode == "empty" ? 0 : mode == "partial" ? 2 : 1, f.Inspects);
        Assert.True(f.AllContainers);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("changed")]
    public async Task VolumeScanFailureOrChangedEngineCannotClaimUnused(string mode)
    {
        var f = new Fixture { VolumeMode = mode };
        var output = await f.Execute("get_volume_usage");
        Assert.Contains("unavailable", output);
        Assert.DoesNotContain("Unused", output);
        Assert.DoesNotContain("private-", output);
    }

    [Theory]
    [InlineData("engine_capabilities")]
    [InlineData("get_health_observations")]
    [InlineData("get_volume_usage")]
    public async Task ReadOnlySchemasRejectUnexpectedFieldsBeforeReading(string name)
    {
        var f = new Fixture();
        var tools = f.Tools();
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ResolveAsync(
            AiContractHarness.Call(name, """{"confirmed":true}"""), default));
        var resolved = await tools.ResolveAsync(AiContractHarness.Call(name, "{}"), default);
        Assert.Equal(AssistantPermissionCategory.ReadOnly, resolved.Category);
        Assert.Equal(0, f.Inspects);
    }

    private sealed class Fixture : IHealthObservationSource, IAppHealthObservationSource
    {
        public WslcCapabilitySupport Support = WslcCapabilitySupport.Supported;
        public bool CapabilityError, ClusterError, AllContainers;
        public ClusterState Cluster = ClusterState.NotInstalled;
        public HealthObservationSnapshot? Health;
        public IReadOnlyList<ContainerHealthSnapshot> AppHealth = [];
        public string VolumeMode = "exact";
        public string Engine = "engine";
        public int Inspects, Inventories;
        public HealthObservationSnapshot? GetSnapshot() => Health;
        public IReadOnlyList<ContainerHealthSnapshot> GetObservations() => AppHealth;
        public IWslcCapabilitiesService Capabilities => NetworkTestProxy.Create<IWslcCapabilitiesService>((_, _) =>
            CapabilityError ? Task.FromException<WslcCapabilities>(new IOException("private-error")) :
                Task.FromResult(new WslcCapabilities("private-path", "private-version",
                    Enum.GetValues<WslcFeature>().ToDictionary(x => x, _ => new WslcCapability(Support, "private-error")))));
        public AssistantToolset Tools(ISettingsService? settings = null)
        {
            var engine = NetworkTestProxy.Create<IWslcService>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IWslcService.ListVolumesAsync):
                        return Task.FromResult<IReadOnlyList<VolumeInfo>>([new() { Name = "data",
                            IsAnonymous = VolumeMode == "estimated", CreatedAt = DateTimeOffset.FromUnixTimeSeconds(100) }]);
                    case nameof(IWslcService.ListContainersAsync):
                        Inventories++;
                        AllContainers = (bool)args[0]!;
                        if (VolumeMode == "failure")
                            return Task.FromException<IReadOnlyList<ContainerInfo>>(new IOException("private-error"));
                        return Task.FromResult<IReadOnlyList<ContainerInfo>>(VolumeMode == "empty" ? [] :
                            VolumeMode == "partial" ? [new() { Id = "one", Name = "stopped" }, new() { Id = "two" }] :
                            [new() { Id = "one", Name = "stopped", CreatedAt = 100 }]);
                    case nameof(IWslcService.InspectContainerAsync):
                        Inspects++;
                        if (VolumeMode == "changed") Engine = "other";
                        return Task.FromResult(new CommandResult
                        {
                            StandardOutput = VolumeMode is "unknown" or "estimated" || (string)args[0]! == "two" ? "{}" :
                                """{"Mounts":[{"Type":"volume","Name":"data","Destination":"/data"}]}""",
                        });
                    default: throw new InvalidOperationException("Unexpected engine call " + method.Name);
                }
            });
            return new(engine,
                NetworkTestProxy.Create<IKubernetesService>((_, _) => ClusterError
                    ? Task.FromException<ClusterStatus>(new IOException("private-cluster-error"))
                    : Task.FromResult(new ClusterStatus { State = Cluster })),
                NetworkTestProxy.Create<ITemplateCatalog>((_, _) => new List<StackTemplate>()),
                NetworkTestProxy.Create<IComposeProjectStore>((_, _) => throw new InvalidOperationException("No Compose access")),
                null!, settings ?? NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
                {
                    "get_WslcPath" => Engine, "get_Registries" => new List<RegistryEntry>(),
                    _ => throw new InvalidOperationException(method.Name),
                }),
                NetworkTestProxy.Create<IRegistryCatalogService>((_, _) => throw new InvalidOperationException("No registry access")),
                Capabilities, this, this);
        }
        public async Task<string> Execute(string name) =>
            await (await Tools().ResolveAsync(AiContractHarness.Call(name, "{}"), default)).ExecuteAsync(default);
    }
}
