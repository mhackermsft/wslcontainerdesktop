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
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeNetworkSupervisorTests
{
    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    public async Task HealthDependenciesUseVerifiedStartupIdentityAndSeedBeforeWaiting(WslcCapabilitySupport networkSupport)
    {
        var fixture = new Fixture(networkSupport);
        fixture.Snapshot = HealthCapabilities(networkSupport);
        var desired = new NativeHealthOptions { Test = ["CMD-SHELL", "true"] };
        fixture.Project.Services[0].Options.Health = desired;
        fixture.Project.Services[0].Health = new()
        {
            DesiredHealth = desired.Clone(), Command = "true", MaxRestarts = 0,
        };
        fixture.Project.Services.Add(new()
        {
            Name = "dependent", Options = new() { Image = "fixture" },
            DependsOn = [new() { ServiceName = "web", Condition = DependencyCondition.ServiceHealthy }],
        });
        fixture.Monitor.RefreshRequested = () =>
        {
            Assert.Contains(fixture.HealthChecks, check => check.ContainerName == "demo_web");
            fixture.Monitor.Latest = new([new()
            {
                Id = "demo_web", Name = "demo_web", StateValue = (int)ContainerState.Running,
            }]);
            fixture.Health.Latest = new([new()
            {
                ContainerId = "demo_web", ContainerName = "demo_web",
                State = ContainerHealthState.Healthy, ObservedAt = DateTimeOffset.UtcNow,
            }]);
        };

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.True(result.AllSucceeded);
        Assert.Equal("demo_web", result.Services[0].ContainerId);
        Assert.Contains("run:demo_dependent", fixture.Engine.Mutations);
        Assert.Equal(desired.Test, fixture.Engine.Containers["demo_web"].Health!.Test);
    }

    [Fact]
    public async Task UnknownCreateHealthCannotRemoveExistingMultiNetworkContainer()
    {
        var fixture = new Fixture();
        fixture.Snapshot = HealthCapabilities(WslcCapabilitySupport.Supported, unknownCreate: true);
        fixture.Project.Services[0].Options.Health = new() { Test = ["CMD-SHELL", "true"] };
        fixture.Engine.Add(Options());

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.HealthChecks);
    }

    private static WslcCapabilities HealthCapabilities(WslcCapabilitySupport networkSupport, bool unknownCreate = false) =>
        new("wslc.exe", "fixture", Enum.GetValues<WslcFeature>().ToDictionary(feature => feature,
            feature => new WslcCapability(feature is WslcFeature.NetworkConnect or WslcFeature.NetworkDisconnect
                ? networkSupport
                : unknownCreate && feature == WslcFeature.CreateHealthCmd
                    ? WslcCapabilitySupport.Unknown : WslcCapabilitySupport.Supported, "fixture diagnostic")));

    [Theory]
    [InlineData(false, WslcCapabilitySupport.Supported, false)]
    [InlineData(false, WslcCapabilitySupport.Supported, true)]
    [InlineData(false, WslcCapabilitySupport.Unsupported, false)]
    [InlineData(false, WslcCapabilitySupport.Unsupported, true)]
    [InlineData(true, WslcCapabilitySupport.Supported, false)]
    [InlineData(true, WslcCapabilitySupport.Supported, true)]
    [InlineData(true, WslcCapabilitySupport.Unsupported, false)]
    [InlineData(true, WslcCapabilitySupport.Unsupported, true)]
    public async Task ProjectStartPreservesStopIntentArrivingDuringPreflight(
        bool restart, WslcCapabilitySupport support, bool previouslyStopped)
    {
        var fixture = new Fixture(support);
        if (previouslyStopped)
            fixture.Suppression.Suppress("demo_web");
        var requestEpoch = fixture.Suppression.Version;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeCapabilities = () =>
        {
            entered.TrySetResult();
            return unblock.Task;
        };
        if (restart)
            fixture.Engine.Add(Options(), allEndpoints: true);

        var start = restart ? fixture.Supervisor.RestartAsync("demo") : fixture.Supervisor.UpAsync(fixture.Project);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Suppression.Suppress("demo_web");
        unblock.SetResult();
        var result = await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.AllSucceeded);
        Assert.True(fixture.Suppression.IsSuppressed("demo_web"));
        if (support == WslcCapabilitySupport.Unsupported)
            Assert.Equal(requestEpoch, fixture.Engine.LastRunMaximumStopVersion);
        else
            Assert.False(fixture.Engine.LastStartWasExplicit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartHealthPreflightPreservesEveryServiceBeforeTeardown(bool invalidHealth)
    {
        var fixture = new Fixture();
        fixture.Snapshot = HealthCapabilities(WslcCapabilitySupport.Supported, unknownCreate: !invalidHealth);
        fixture.Engine.Add(Options(), allEndpoints: true);
        var other = Options("demo_other");
        other.Labels[ComposeProject.ServiceLabel] = "other";
        other.Health = new() { Test = ["CMD-SHELL", "true"], Retries = invalidHealth ? 0 : 3 };
        fixture.Project.Services.Add(new() { Name = "other", Options = other });
        fixture.Engine.Add(other, allEndpoints: true);
        var health = new HealthCheckConfig { ContainerName = "demo_web", Command = "old-probe" };
        var restart = new RestartPolicyConfig { ContainerName = "demo_other", Policy = RestartPolicyKind.Always };
        fixture.HealthChecks.Add(health);
        fixture.RestartPolicies.Add(restart);

        var result = await fixture.Supervisor.RestartAsync("demo");

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.Same(health, Assert.Single(fixture.HealthChecks));
        Assert.Same(restart, Assert.Single(fixture.RestartPolicies));
    }

    [Fact]
    public async Task LegacyOnlyRunsPrimaryAndReturnsTruthfulWarning()
    {
        var fixture = new Fixture(WslcCapabilitySupport.Unsupported);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.True(result.AllSucceeded);
        Assert.Contains("Only network 'a'", Assert.Single(result.Warnings));
        Assert.Equal(["run:demo_web"], fixture.Engine.Mutations);
        Assert.Equal(2, fixture.Project.Services[0].Options.NetworkAttachments.Count);
        Assert.Equal("a", Assert.Single(fixture.Engine.Endpoints["demo_web"]).Network);
    }

    [Fact]
    public async Task UnknownCapabilityCannotReplaceExistingContainer()
    {
        var fixture = new Fixture(WslcCapabilitySupport.Unknown);
        fixture.Engine.Add(Options());
        var health = new HealthCheckConfig { ContainerName = "demo_web", Command = "old-probe" };
        var restart = new RestartPolicyConfig { ContainerName = "demo_web", Policy = RestartPolicyKind.Always };
        fixture.HealthChecks.Add(health);
        fixture.RestartPolicies.Add(restart);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.False(result.AllSucceeded);
        Assert.Contains("fixture diagnostic", result.Services[0].Detail);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Same(health, Assert.Single(fixture.HealthChecks));
        Assert.Same(restart, Assert.Single(fixture.RestartPolicies));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("discovery-failure")]
    [InlineData("invalid-ip")]
    public async Task RestartPreflightsAllServicesBeforeAnyTeardown(string failure)
    {
        var fixture = new Fixture();
        fixture.Engine.Add(Options(), allEndpoints: true);
        fixture.Project.Networks = [new() { Name = "a" }];
        fixture.Engine.Networks["a"] = "demo";
        fixture.RestartPolicies.Add(new() { ContainerName = "demo_web", Policy = RestartPolicyKind.Always });
        if (failure == "unknown")
            fixture.Snapshot = Capabilities(WslcCapabilitySupport.Unknown);
        else if (failure == "discovery-failure")
            fixture.CapabilityError = new IOException("fixture discovery failed");
        else
        {
            var other = Options("demo_other");
            other.NetworkAttachments[1].Ipv4Address = "invalid-ip";
            fixture.Project.Services.Add(new() { Name = "other", Options = other });
        }

        var result = await fixture.Supervisor.RestartAsync("demo");

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Single(fixture.Engine.Containers);
        Assert.True(fixture.Engine.Networks.ContainsKey("a"));
        Assert.Single(fixture.RestartPolicies);
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    public async Task RestartCarriesPreflightDecisionIntoStartup(WslcCapabilitySupport support)
    {
        var fixture = new Fixture(support);
        fixture.Engine.Add(Options(), allEndpoints: true);
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;

        var result = await fixture.Supervisor.RestartAsync("demo");

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, fixture.CapabilityReads);
        Assert.Equal(["stop:demo_web", "remove:demo_web"], fixture.Engine.Mutations.Take(2));
        Assert.Single(fixture.RestartPolicies);
        if (support == WslcCapabilitySupport.Supported)
            Assert.Equal(["create:demo_web", "connect:b", "start:demo_web"], fixture.Engine.Mutations.Skip(2));
        else
        {
            Assert.Equal(["run:demo_web"], fixture.Engine.Mutations.Skip(2));
            Assert.Single(result.Warnings);
        }
    }

    [Fact]
    public async Task ProvisioningFailurePreservesUntouchedSupervision()
    {
        var fixture = new Fixture();
        fixture.Engine.Add(Options(), allEndpoints: true);
        fixture.Project.Networks = [new() { Name = "missing", External = true }];
        fixture.HealthChecks.Add(new() { ContainerName = "demo_web", Command = "old-probe" });
        fixture.RestartPolicies.Add(new() { ContainerName = "demo_web", Policy = RestartPolicyKind.Always });

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.UpAsync(fixture.Project));

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Single(fixture.HealthChecks);
        Assert.Single(fixture.RestartPolicies);
    }

    [Fact]
    public async Task CancellationKeepsCompletedAndUntouchedServicesSupervised()
    {
        using var cts = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        var other = Options("demo_other");
        other.Labels[ComposeProject.ServiceLabel] = "other";
        fixture.Project.Services.Add(new() { Name = "other", Options = other });
        fixture.Engine.Add(other, allEndpoints: true);
        var previous = new HealthCheckConfig { ContainerName = "demo_other", Command = "old-probe" };
        fixture.HealthChecks.Add(previous);
        fixture.Engine.AfterStart = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Supervisor.UpAsync(fixture.Project, cts.Token));

        Assert.Equal("demo_web", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.Same(previous, Assert.Single(fixture.HealthChecks));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_other"));
    }

    [Fact]
    public async Task DownInventoryFailureKeepsUntouchedSupervision()
    {
        var fixture = new Fixture();
        fixture.Engine.Add(Options(), allEndpoints: true);
        fixture.Engine.BeforeList = () => throw new IOException("fixture inventory failed");
        fixture.RestartPolicies.Add(new() { ContainerName = "demo_web", Policy = RestartPolicyKind.Always });

        await Assert.ThrowsAsync<IOException>(() => fixture.Supervisor.DownAsync("demo"));

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Single(fixture.RestartPolicies);
    }

    [Fact]
    public async Task PartialFailureBlocksDependentsAndSeedsOnlySuccessfulServices()
    {
        var engine = new Engine { FailNetwork = "b" };
        var fixture = new Fixture(engine: engine);
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        fixture.Project.Services.Add(new()
        {
            Name = "dependent", Options = new() { Image = "fixture" },
            DependsOn = [new() { ServiceName = "web" }], Restart = RestartPolicyKind.Always,
        });
        fixture.Project.Services.Add(new()
        {
            Name = "independent", Options = new() { Image = "fixture" },
            Restart = RestartPolicyKind.Always,
        });
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.False(result.AllSucceeded);
        Assert.False(result.Services.Single(s => s.Service == "dependent").Success);
        Assert.True(result.Services.Single(s => s.Service == "independent").Success);
        Assert.Equal("demo_independent", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.DoesNotContain(engine.Mutations, m => m.Contains("demo_dependent"));
    }

    [Fact]
    public async Task SameNameUnownedContainerIsNeverRemovedOnUpOrDown()
    {
        var fixture = new Fixture();
        var other = Options();
        other.Labels.Clear();
        fixture.Engine.Add(other);
        var up = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.Contains("not owned", up.Services[0].Detail);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.DownAsync("demo"));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task DownPreservesExternalAndUnownedNetworks()
    {
        var fixture = new Fixture();
        fixture.Project.Networks =
        [
            new() { Name = "external", External = true },
            new() { Name = "same-name" },
            new() { Name = "ours" },
        ];
        fixture.Engine.Networks["external"] = "demo";
        fixture.Engine.Networks["same-name"] = "another-project";
        fixture.Engine.Networks["ours"] = "demo";
        await fixture.Supervisor.DownAsync("demo");
        Assert.Equal(["network-remove:ours"], fixture.Engine.Mutations);
        Assert.True(fixture.Engine.Networks.ContainsKey("external"));
        Assert.True(fixture.Engine.Networks.ContainsKey("same-name"));
    }

    [Fact]
    public async Task MissingExternalNetworkFailsWithoutCreation()
    {
        var fixture = new Fixture();
        fixture.Project.Networks = [new() { Name = "external", External = true }];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.UpAsync(fixture.Project));
        Assert.Contains("external", error.Message);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task ServiceNetworkModeWaitsForReferencedServiceWithoutAddingEndpoints()
    {
        var fixture = new Fixture(WslcCapabilitySupport.Unknown);
        var parsed = ComposeImporter.ParseProject("""
            services:
              sidecar:
                image: fixture
                network_mode: service:web
              web:
                image: fixture
                network_mode: none
            """);
        fixture.Project.Services = parsed.Services;
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.True(result.AllSucceeded);
        Assert.Equal(["run:demo_web", "run:demo_sidecar"], fixture.Engine.Mutations);
        Assert.Equal("container:demo_web", fixture.Engine.Containers["demo_sidecar"].NetworkMode);
        Assert.Empty(fixture.Engine.Endpoints["demo_sidecar"]);
    }

    [Fact]
    public async Task ReconcileAdoptsOwnedEndpointStateWithoutDuplicateConnect()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        fixture.Engine.Add(Options(), allEndpoints: true);
        await fixture.Supervisor.ReconcileAsync();
        await fixture.Supervisor.ReconcileAsync();
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.Supervisor.ReconciliationWarnings);
        Assert.Single(fixture.RestartPolicies);
    }

    [Fact]
    public async Task ReconcileMismatchIsVisibleAndNotEnrolled()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        fixture.Engine.Add(Options(), allEndpoints: true);
        fixture.Engine.Endpoints["demo_web"][1].Aliases.Clear();
        await fixture.Supervisor.ReconcileAsync();
        Assert.Contains("'b'", Assert.Single(fixture.Supervisor.ReconciliationWarnings));
        Assert.Empty(fixture.RestartPolicies);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task CancelledLifecycleWaitDoesNotReleaseAnotherCallLockOrLeakIt()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Engine.BeforeList = () =>
        {
            entered.TrySetResult();
            return unblock.Task;
        };
        var first = fixture.Supervisor.UpAsync(fixture.Project);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var waiting = fixture.Supervisor.UpAsync(fixture.Project, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Empty(fixture.Engine.Mutations);
        unblock.SetResult();
        Assert.True((await first).AllSucceeded);
        fixture.Engine.BeforeList = null;
        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
    }

    internal sealed class Fixture
    {
        public Engine Engine { get; }
        public ComposeProject Project { get; } = new()
        {
            Name = "demo", Services = [new() { Name = "web", Options = Options() }],
        };
        public List<RestartPolicyConfig> RestartPolicies { get; private set; } = new();
        public List<HealthCheckConfig> HealthChecks { get; private set; } = new();
        public ComposeProjectSupervisor Supervisor { get; }
        public HealthWatchdog Health { get; } = new();
        public StatusMonitor Monitor { get; } = new();
        public RestartSuppressionState Suppression { get; } = new();
        public WslcCapabilities Snapshot { get; set; }
        public Exception? CapabilityError { get; set; }
        public Func<Task>? BeforeCapabilities { get; set; }
        public int CapabilityReads { get; private set; }

        public Fixture(WslcCapabilitySupport support = WslcCapabilitySupport.Supported, Engine? engine = null)
        {
            Engine = engine ?? new();
            Snapshot = Capabilities(support);
            var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) =>
            {
                if (method.Name != nameof(IWslcCapabilitiesService.GetAsync))
                    throw new InvalidOperationException(method.Name);
                return ReadCapabilitiesAsync();
            });
            var store = NetworkTestProxy.Create<IComposeProjectStore>((method, _) => method.Name switch
            {
                nameof(IComposeProjectStore.Save) => null,
                nameof(IComposeProjectStore.Get) => Project,
                nameof(IComposeProjectStore.GetAll) => new List<ComposeProject> { Project },
                _ => throw new InvalidOperationException(method.Name),
            });
            var settings = NetworkTestProxy.Create<ISettingsService>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_HealthChecks": return HealthChecks;
                    case "get_RestartPolicies": return RestartPolicies;
                    case "set_HealthChecks": HealthChecks = (List<HealthCheckConfig>)args[0]!; return null;
                    case "set_RestartPolicies": RestartPolicies = (List<RestartPolicyConfig>)args[0]!; return null;
                    case nameof(ISettingsService.Save): return null;
                    default: throw new InvalidOperationException(method.Name);
                }
            });
            Supervisor = new(Engine.Service, store, settings, NullLogger<ComposeProjectSupervisor>.Instance,
                capabilities, Health, Monitor, Suppression);
        }

        private async Task<WslcCapabilities> ReadCapabilitiesAsync()
        {
            CapabilityReads++;
            if (BeforeCapabilities is not null)
                await BeforeCapabilities();
            if (CapabilityError is not null)
                throw CapabilityError;
            return Snapshot;
        }
    }
}
