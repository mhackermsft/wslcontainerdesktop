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

using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeRuntimeTests
{
    [ComposeRuntimeFact]
    [Trait("Category", "ComposeRuntime")]
    public async Task OwnedStartupAliasesRecreationSupervisionAndCleanup()
    {
        await using var lease = new ComposeRuntimeLease();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        await lease.InitializeAsync(ct);
        var first = await lease.CreateNetworkAsync("first", ct);
        var second = await lease.CreateNetworkAsync("second", ct);
        var project = new ComposeProject
        {
            Name = lease.RunId,
            Networks = [new() { Name = first, External = true }, new() { Name = second, External = true }],
        };
        ComposeService Service(string name, string command) => new()
        {
            Name = name,
            Options = new()
            {
                Image = lease.Image, Command = command, CpuLimit = "0.25", MemoryLimit = "64M",
                Network = first, Networks = [first, second],
                NetworkAttachments =
                [
                    new() { Network = first, Aliases = [name + "-first"] },
                    new() { Network = second, Aliases = [name + "-second"] },
                ],
            },
        };
        var job = Service("job", "sh -c \"sleep 1; exit 0\"");
        var server = Service("server", "sh -c \"touch /tmp/wcd-ready; sleep 300\"");
        server.DependsOn = [new() { ServiceName = "job", Condition = DependencyCondition.ServiceCompletedSuccessfully }];
        server.Health = new()
        {
            DesiredHealth = new() { Test = ["CMD", "test", "-f", "/tmp/wcd-ready"] },
            MaxRestarts = 0,
        };
        var client = Service("client", "sleep 300");
        client.DependsOn = [new() { ServiceName = "server", Condition = DependencyCondition.ServiceHealthy }];
        var tail = Service("tail", "sleep 300");
        tail.DependsOn = [new() { ServiceName = "client", Condition = DependencyCondition.ServiceStarted }];
        project.Services = [tail, client, server, job]; // deliberately reverse dependency order
        var store = CreateStore(project);
        var monitor = new StatusMonitor();
        var health = new HealthWatchdog();
        var supervisor = new ComposeProjectSupervisor(lease.Service, store, lease.Settings,
            NullLogger<ComposeProjectSupervisor>.Instance, lease.Capabilities, health, monitor, lease.Suppression);
        async Task ObserveAsync()
        {
            var inventory = await lease.Service.ListContainersAsync(true, ct);
            monitor.Latest = new(inventory);
            var current = inventory.SingleOrDefault(c => c.Name == lease.RunId + "_server");
            if (current?.State != ContainerState.Running) return;
            var probe = new NativeHealthOptions { Test = ["CMD", "test", "-f", "/tmp/wcd-ready"] };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            CommandResult observed;
            do
            {
                observed = await lease.Service.ExecHealthAsync(current.Id, probe, ct);
                if (observed.Success) break;
                await Task.Delay(100, ct);
            } while (DateTimeOffset.UtcNow < deadline);
            ComposeRuntimeLease.Require(observed, "Actual dependency health probe");
            health.Latest = new([new()
            {
                ContainerId = current.Id, ContainerName = current.Name,
                ContainerGeneration = current.StateChangedAt,
                State = ContainerHealthState.Healthy, ObservedAt = DateTimeOffset.UtcNow,
            }]);
            lease.Record("health-observed", new { current.Id, current.Name, current.StateChangedAt });
        }
        // The real supervisor requests refresh after recording startup identity/time.
        // Feed actual CLI observations through the test monitor port, not before that boundary.
        monitor.RefreshRequested = () => ObserveAsync().GetAwaiter().GetResult();
        lease.BeforeStart = name =>
        {
            if (name.EndsWith("_server", StringComparison.Ordinal))
            {
                Assert.Contains(lease.RunId + "_job", lease.Started);
                var completed = lease.Service.ListContainersAsync(true, ct).GetAwaiter().GetResult()
                    .Single(c => c.Name == lease.RunId + "_job");
                Assert.Equal(ContainerState.Stopped, completed.State);
                var result = lease.Service.InspectContainerAsync(completed.Id, ct).GetAwaiter().GetResult();
                ComposeRuntimeLease.Require(result, "Completed dependency inspect");
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0] : document.RootElement;
                var exitState = root.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.Object ? state : root;
                Assert.Equal(0, exitState.GetProperty("ExitCode").GetInt32());
                lease.Record("completed-dependency-verified", new { completed.Id });
            }
            if (name.EndsWith("_client", StringComparison.Ordinal))
                Assert.Single(health.Latest.Containers);
            if (name.EndsWith("_tail", StringComparison.Ordinal))
                Assert.Contains(lease.RunId + "_client", lease.Started);
        };
        var up = await supervisor.UpAsync(project, ct);
        Assert.True(up.AllSucceeded, string.Join("; ", up.Services.Select(s => s.Detail)));
        Assert.Equal(new[] { "job", "server", "client", "tail" }.Select(s => lease.RunId + "_" + s), lease.Started);
        lease.Record("dependency-order-verified", new { lease.Started });
        monitor.RefreshRequested = null;
        lease.BeforeStart = null;

        var serverState = await lease.RequireContainerAsync(lease.RunId + "_server", ct);
        var clientState = await lease.RequireContainerAsync(lease.RunId + "_client", ct);
        foreach (var alias in new[] { "server-first", "server-second" })
            ComposeRuntimeLease.Require(await lease.Service.ExecHealthAsync(clientState.Id,
                new() { Test = ["CMD", "nslookup", alias] }, ct), "Network DNS alias " + alias);
        lease.Record("aliases-verified", new { serverState.Id, clientId = clientState.Id });

        // Observe an actual workload health failure, not an invented health snapshot.
        ComposeRuntimeLease.Require(await lease.Service.ExecHealthAsync(serverState.Id,
            new() { Test = ["CMD", "rm", "/tmp/wcd-ready"] }, ct), "Remove owned readiness marker");
        Assert.False((await lease.Service.ExecHealthAsync(serverState.Id,
            new() { Test = ["CMD", "test", "-f", "/tmp/wcd-ready"] }, ct)).Success);
        lease.Record("unhealthy-observed", new { serverState.Id });
        lease.Suppression.Suppress(lease.RunId + "_server");
        ComposeRuntimeLease.Require(await lease.Service.StopContainerAsync(serverState.Id, ct), "Manual stop");
        ComposeRuntimeLease.Require(await lease.Service.StartContainerAsync(serverState.Id, ct, explicitStart: false), "Non-explicit start");
        Assert.True(lease.Suppression.IsSuppressed(lease.RunId + "_server"));
        ComposeRuntimeLease.Require(await lease.Service.StopContainerAsync(serverState.Id, ct), "Stop before explicit resume");
        ComposeRuntimeLease.Require(await lease.Service.StartContainerAsync(serverState.Id, ct, explicitStart: true), "Explicit resume");
        Assert.False(lease.Suppression.IsSuppressed(lease.RunId + "_server"));
        lease.Record("stop-intent-verified", new { serverState.Id });

        var desired = server.Options.Clone();
        desired.Name = lease.RunId + "_server";
        var orchestrator = new ComposeNetworkOrchestrator(lease.Service, NullLogger.Instance, lease.Suppression);
        await orchestrator.ReconcileAsync(serverState.Id, desired, await lease.Capabilities.GetAsync(ct), ct);
        Assert.Equal(serverState.Id, (await lease.RequireContainerAsync(desired.Name, ct)).Id);
        ComposeRuntimeLease.Require(await lease.Service.RemoveContainerAsync(serverState.Id, true, ct), "Explicit owned recreation");
        desired.Command = "sleep 299";
        var replacement = await orchestrator.CreateAndStartAsync(desired, ct);
        Assert.NotEqual(serverState.Id, replacement);
        Assert.Equal(clientState.Id, (await lease.RequireContainerAsync(lease.RunId + "_client", ct)).Id);
        lease.Record("recreation-verified", new { oldId = serverState.Id, replacement, preservedClient = clientState.Id });

        // Cancellation during post-create startup must trigger ownership-checked rollback.
        using var cancel = new CancellationTokenSource();
        lease.BeforeStart = _ => cancel.Cancel();
        var partial = desired.Clone();
        partial.Name = lease.RunId + "_cancelled";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.CreateAndStartAsync(partial, cancel.Token));
        Assert.DoesNotContain(await lease.Service.ListContainersAsync(true, ct), c => c.Name == partial.Name);
        lease.BeforeStart = null;
        lease.Record("cancelled-start-cleanup-verified", new { partial.Name });
        // The lease's finally removes only verified IDs/labels and compares post-run inventories.
    }

    [Fact]
    public void RuntimeCreationPolicyRejectsUnownedAndHostExposingRequests()
    {
        var options = new RunContainerOptions { Name = "owned_server", Image = "local-id", Network = "owned-net" };
        ComposeRuntimeLease.ValidateOptions(options, "owned", "local-id");
        foreach (var mutate in new Action<RunContainerOptions>[]
        {
            o => o.Name = "unrelated", o => o.Image = "pullable:tag",
            o => o.Volumes.Add("C:\\host:/host"), o => o.PortMappings.Add("8080:80"),
            o => o.Network = "unrelated", o => o.NetworkMode = "host", o => o.AllGpus = true,
        })
        {
            var invalid = options.Clone();
            mutate(invalid);
            Assert.Throws<InvalidOperationException>(() => ComposeRuntimeLease.ValidateOptions(invalid, "owned", "local-id"));
        }

    }

    [Fact]
    public void RuntimeOwnershipRejectsForeignLabelsAndReplacedIds()
    {
        var owned = ContainerNetworkState.Parse("""
            {"Id":"first","Config":{"Labels":{"com.wsldesktop.conformance-run":"run"}},
             "NetworkSettings":{"Networks":{}}}
            """);
        ComposeRuntimeLease.RequireOwnership(owned, "run", "first");
        Assert.Throws<InvalidOperationException>(() => ComposeRuntimeLease.RequireOwnership(owned, "another-run", "first"));
        Assert.Throws<InvalidOperationException>(() => ComposeRuntimeLease.RequireOwnership(owned, "run", "replacement"));
        var unknown = ContainerNetworkState.Parse("""
            {"Id":"first","Config":{},"NetworkSettings":{"Networks":{}}}
            """);
        Assert.Throws<InvalidOperationException>(() => ComposeRuntimeLease.RequireOwnership(unknown, "run", null));
    }

    private static IComposeProjectStore CreateStore(ComposeProject project) =>
        NetworkTestProxy.Create<IComposeProjectStore>((method, args) => method.Name switch
        {
            nameof(IComposeProjectStore.GetAll) => new List<ComposeProject> { project },
            nameof(IComposeProjectStore.Get) => (string)args[0]! == project.Name ? project : null,
            nameof(IComposeProjectStore.Save) when ReferenceEquals(args[0], project) => null,
            _ => throw new InvalidOperationException($"Unapproved runtime store call {method.Name}."),
        });

    [Fact]
    public async Task RuntimeStoreAcceptsRealSupervisorSaveWithoutDiskOrEngine()
    {
        var project = new ComposeProject { Name = "synthetic" };
        var store = CreateStore(project);
        var service = NetworkTestProxy.Create<IWslcService>((method, _) =>
            throw new InvalidOperationException($"Unexpected engine access {method.Name}."));
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) =>
            throw new InvalidOperationException($"Unexpected settings access {method.Name}."));
        var capabilities = NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) =>
            throw new InvalidOperationException($"Unexpected capability access {method.Name}."));
        var supervisor = new ComposeProjectSupervisor(service, store, settings,
            NullLogger<ComposeProjectSupervisor>.Instance, capabilities, new HealthWatchdog(), new StatusMonitor());
        Assert.True((await supervisor.UpAsync(project)).AllSucceeded);
        Assert.Same(project, store.Get(project.Name));
        Assert.Throws<InvalidOperationException>(() => store.Save(new ComposeProject { Name = project.Name }));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[null]")]
    [InlineData("[{\"Name\":\"missing-id\"}]")]
    [InlineData("[{\"Name\":\"n\",\"Id\":\"1\"},{\"Name\":\"n\",\"Id\":\"2\"}]")]
    [InlineData("[{\"Name\":\"n\",\"Id\":\"1\"}] garbage")]
    public void RuntimeNetworkInventoryCannotTurnFailureIntoAbsence(string json)
    {
        Assert.ThrowsAny<JsonException>(() => ComposeRuntimeLease.ParseNetworks(new() { StandardOutput = json }));
        Assert.Throws<InvalidOperationException>(() => ComposeRuntimeLease.ParseNetworks(new() { ExitCode = 1 }));
        Assert.Empty(ComposeRuntimeLease.ParseNetworks(new() { StandardOutput = "[]" }));
    }
}
