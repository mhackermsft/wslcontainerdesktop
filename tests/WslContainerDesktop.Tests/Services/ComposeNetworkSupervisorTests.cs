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
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.False(result.AllSucceeded);
        Assert.Contains("fixture diagnostic", result.Services[0].Detail);
        Assert.Empty(fixture.Engine.Mutations);
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
        public WslcCapabilities Snapshot { get; set; }

        public Fixture(WslcCapabilitySupport support = WslcCapabilitySupport.Supported, Engine? engine = null)
        {
            Engine = engine ?? new();
            Snapshot = Capabilities(support);
            var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) =>
                method.Name == nameof(IWslcCapabilitiesService.GetAsync)
                    ? Task.FromResult(Snapshot)
                    : throw new InvalidOperationException(method.Name));
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
                capabilities);
        }
    }
}
