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

#if WINDOWS
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;
using static WslContainerDesktop.Tests.Services.ComposeNetworkSupervisorTests;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeScalingSupervisorTests
{
    private static Fixture Replicas(int count, WslcCapabilitySupport support = WslcCapabilitySupport.Supported, Engine? engine = null)
    {
        var fixture = new Fixture(support, engine);
        fixture.Project.Services[0].Options.Name = null;
        fixture.Project.Services[0].Options.NetworkAttachments.ForEach(n => n.Ipv4Address = null);
        fixture.Project.Services[0].Replicas = count;
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        return fixture;
    }

    private static void AddDependent(Fixture fixture, DependencyCondition condition = DependencyCondition.ServiceStarted) =>
        fixture.Project.Services.Add(new()
        {
            Name = "dependent", Options = new() { Image = "fixture" },
            DependsOn = [new() { ServiceName = "web", Condition = condition }],
        });

    private static async Task<ComposeUpResult> UpAsync(Fixture fixture, ComposeOperationRequest? request = null)
    {
        var result = await fixture.Supervisor.UpAsync(fixture.Project, request ?? new());
        Assert.True(result.AllSucceeded, string.Join("; ", result.Services.Select(s => $"{s.InstanceKey}: {s.Detail}")));
        return result;
    }

    [Fact]
    public async Task FreshTemplatePreviewAndApplyInheritSavedReplicaIntent()
    {
        var fixture = Replicas(1);
        fixture.Project.ReplicaOverrides["web"] = 2;
        await UpAsync(fixture);
        var fresh = System.Text.Json.JsonSerializer.Deserialize<ComposeProject>(
            System.Text.Json.JsonSerializer.Serialize(fixture.Project))!;
        fresh.ReplicaOverrides.Clear();
        fresh.AppliedStateKnown = false;
        fixture.Engine.Mutations.Clear();

        var preview = await fixture.Supervisor.PlanAsync(fresh);
        var result = await fixture.Supervisor.UpAsync(fresh);

        Assert.Equal(2, preview.Services.Count);
        Assert.True(result.AllSucceeded);
        Assert.All(result.Services, instance => Assert.Equal(ComposeServiceAction.Keep, instance.Action));
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(2, fixture.SavedProject!.ReplicaOverrides["web"]);
        Assert.Empty(fresh.ReplicaOverrides);
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    public async Task ScaleUpThenDownRetainsLowestIdentityAndEverySupportedEndpointAlias(WslcCapabilitySupport support)
    {
        var fixture = Replicas(1, support);
        await UpAsync(fixture);
        var original = fixture.SavedProject!.AppliedServices["web"];
        fixture.Engine.Mutations.Clear();
        fixture.Project.Services[0].Replicas = 3;
        var up = await UpAsync(fixture);
        Assert.Equal(["web", "web#2", "web#3"], up.Services.Select(s => s.InstanceKey));
        Assert.Equal(ComposeServiceAction.Keep, up.Services[0].Action);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m is "stop:demo_web" or "remove:demo_web");
        Assert.Equal(original.Fingerprint, fixture.SavedProject!.AppliedServices["web"].Fingerprint);
        Assert.Equal(original.ContainerId, fixture.SavedProject.AppliedServices["web"].ContainerId);
        Assert.Equal(3, fixture.RestartPolicies.Count);
        foreach (var pair in fixture.Engine.Containers)
        {
            var index = pair.Key == "demo_web" ? 1 : int.Parse(pair.Key[^1..]);
            Assert.Equal("demo", pair.Value.Labels[ComposeProject.ProjectLabel]);
            Assert.Equal("web", pair.Value.Labels[ComposeProject.ServiceLabel]);
            Assert.Equal(index.ToString(), pair.Value.Labels[ComposeProject.InstanceLabel]);
            Assert.Equal(support == WslcCapabilitySupport.Supported ? 2 : 1, fixture.Engine.Endpoints[pair.Key].Count);
            Assert.All(fixture.Engine.Endpoints[pair.Key], endpoint =>
            {
                Assert.Contains("web", endpoint.Aliases);
                Assert.Contains(pair.Key, endpoint.Aliases);
            });
        }
        fixture.Engine.Mutations.Clear();
        fixture.Project.Services[0].Replicas = 1;
        await UpAsync(fixture);
        Assert.Equal("demo_web", Assert.Single(fixture.Engine.Containers).Key);
        Assert.Equal("web", Assert.Single(fixture.SavedProject!.AppliedServices).Key);
        Assert.Equal("demo_web", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m is "stop:demo_web" or "remove:demo_web");
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("volume-remove:") || m.StartsWith("network-remove:"));
    }

    [Fact]
    public async Task LegacyFirstInstanceIsNotRecreatedToRetrofitUniqueAliasesOrOrdinalLabel()
    {
        var fixture = Replicas(1);
        await UpAsync(fixture);
        fixture.Engine.Containers["demo_web"].Labels.Remove(ComposeProject.InstanceLabel);
        fixture.Engine.Endpoints["demo_web"].ForEach(endpoint => endpoint.Aliases.Remove("demo_web"));
        fixture.Project.Services[0].Replicas = 2;
        fixture.Engine.Mutations.Clear();

        await UpAsync(fixture);

        Assert.DoesNotContain(fixture.Engine.Mutations, m => m is "stop:demo_web" or "remove:demo_web" or "create:demo_web");
        Assert.All(fixture.Engine.Endpoints["demo_web"], endpoint => Assert.DoesNotContain("demo_web", endpoint.Aliases));
        Assert.All(fixture.Engine.Endpoints["demo_web_2"], endpoint =>
        {
            Assert.Contains("web", endpoint.Aliases);
            Assert.Contains("demo_web_2", endpoint.Aliases);
        });
    }

    [Fact]
    public async Task ZeroDependencyBlocksImageDrivenReplacementBeforeAnyScaleDownMutation()
    {
        var fixture = Replicas(1);
        AddDependent(fixture);
        await UpAsync(fixture);
        fixture.Project.Services[0].Replicas = 0;
        fixture.Engine.Images[0].Id = "sha256:updated";
        fixture.Engine.Mutations.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        Assert.False(plan.CanApply);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.Contains("zero", result.Services.Single(s => s.Service == "dependent").Detail);
    }

    [Fact]
    public async Task RuntimeOverridesWinWithoutRewritingSavedDesiredCounts()
    {
        var fixture = Replicas(1);
        fixture.Project.ReplicaOverrides["web"] = 2;
        await UpAsync(fixture, new() { Replicas = new Dictionary<string, int> { ["web"] = 3 } });
        Assert.Equal(3, fixture.Engine.Containers.Count);
        Assert.Equal(2, fixture.SavedProject!.ReplicaOverrides["web"]);
        Assert.Equal(1, fixture.SavedProject.Services[0].Replicas);
        await UpAsync(fixture);
        Assert.Equal(2, fixture.Engine.Containers.Count);
    }

    [Fact]
    public async Task RuntimeLabelsAreMintedFromPlanAndObservedOrdinalIsRequiredForRemoval()
    {
        var fixture = Replicas(2);
        fixture.Project.Services[0].Options.Labels[ComposeProject.InstanceLabel] = "999";

        await UpAsync(fixture);

        Assert.Equal("1", fixture.Engine.Containers["demo_web"].Labels[ComposeProject.InstanceLabel]);
        Assert.Equal("2", fixture.Engine.Containers["demo_web_2"].Labels[ComposeProject.InstanceLabel]);
        Assert.All(fixture.SavedProject!.AppliedServices.Values,
            saved => Assert.False(saved.Service.Options.Labels.ContainsKey(ComposeProject.InstanceLabel)));
        Assert.Equal(2, fixture.SavedProject.AppliedServices["web#2"].InstanceIndex);
        fixture.Project.Services[0].Replicas = 0;
        fixture.Engine.Containers["demo_web_2"].Labels[ComposeProject.InstanceLabel] = "1";
        fixture.Engine.Mutations.Clear();

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(2, fixture.Engine.Containers.Count);
    }

    [Fact]
    public async Task RequestCollectionsAreSnapshottedBeforeAsynchronousPreflight()
    {
        var fixture = Replicas(1);
        AddDependent(fixture);
        var counts = new Dictionary<string, int> { ["web"] = 2 };
        var targets = new List<string> { "web" };
        fixture.Engine.BeforeList = () =>
        {
            counts["web"] = 0;
            targets.Clear();
            return Task.CompletedTask;
        };
        await UpAsync(fixture, new() { Services = targets, Replicas = counts });
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.False(fixture.Engine.Containers.ContainsKey("demo_dependent"));
    }

    [Fact]
    public async Task InvalidPendingDesiredCountDoesNotPreventStoppingAppliedInstances()
    {
        var fixture = Replicas(2);
        await UpAsync(fixture);
        fixture.Project.Services[0].Replicas = -1;
        fixture.Project.ReplicaOverrides["web"] = -1;
        var result = await fixture.Supervisor.OperateAsync("demo", new() { Operation = ComposeLifecycleOperation.Stop });
        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.Services.Count);
        Assert.All(fixture.SavedProject!.AppliedServices.Values, p => Assert.True(p.ManuallyStopped));
    }

    [Fact]
    public async Task ScaleToZeroRemovesOnlySelectedInstancesAndPreservesSharedResources()
    {
        var fixture = Replicas(3);
        AddDependent(fixture);
        await UpAsync(fixture);
        fixture.Engine.Mutations.Clear();
        fixture.Engine.ImageInventoryError = new IOException("Image inventory is irrelevant to removal.");
        await UpAsync(fixture, new() { Services = ["web"], Replicas = new Dictionary<string, int> { ["web"] = 0 } });
        Assert.Equal("demo_dependent", Assert.Single(fixture.Engine.Containers).Key);
        Assert.Equal("dependent", Assert.Single(fixture.SavedProject!.AppliedServices).Key);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("dependent"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("port")]
    [InlineData("mode")]
    [InlineData("address")]
    public async Task ScalingConflictsBlockBeforeAnyOtherSelectedServiceIsRecreated(string conflict)
    {
        var fixture = Replicas(1);
        AddDependent(fixture);
        await UpAsync(fixture);
        fixture.Project.Services[1].Options.Command = "changed";
        fixture.Project.Services[0].Replicas = 2;
        switch (conflict)
        {
            case "name": fixture.Project.Services[0].Options.Name = "demo_web"; break;
            case "port": fixture.Project.Services[0].Options.PortMappings = ["8000:80"]; break;
            case "mode": fixture.Project.Services[0].Options.NetworkMode = "host"; break;
            default: fixture.Project.Services[0].Options.NetworkAttachments[0].Ipv4Address = "10.0.0.4"; break;
        }
        fixture.Engine.Mutations.Clear();
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(2, fixture.Engine.Containers.Count);
    }

    [Fact]
    public async Task UnknownCapabilitiesAndForeignOrdinalCannotDestroyRetainedInstances()
    {
        var fixture = Replicas(2);
        await UpAsync(fixture);
        fixture.Engine.Mutations.Clear();
        fixture.Snapshot = Capabilities(WslcCapabilitySupport.Unknown);
        fixture.Project.Services[0].Replicas = 3;
        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        fixture.Snapshot = Capabilities(WslcCapabilitySupport.Supported);
        fixture.Engine.Containers["demo_web_2"].Labels[ComposeProject.InstanceLabel] = "3";
        fixture.Project.Services[0].Replicas = 1;
        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task AnonymousVolumesStayWithTheirOwnReplicaWhileSharedSourcesRemainShared()
    {
        var fixture = Replicas(2);
        fixture.Project.Services[0].Options.Volumes = ["named:/shared", "/cache"];
        await UpAsync(fixture);
        foreach (var name in fixture.Engine.Containers.Keys)
            fixture.Engine.Mounts[name] =
            [
                new { Type = "volume", Name = $"anonymous-{name}", Destination = "/cache", RW = true, IsAnonymous = true },
                new { Type = "volume", Name = "named", Destination = "/shared", RW = true, IsAnonymous = false },
            ];
        fixture.Project.Services[0].Options.Command = "updated";
        await UpAsync(fixture);
        foreach (var pair in fixture.Engine.Containers)
        {
            Assert.Contains($"anonymous-{pair.Key}:/cache", pair.Value.Volumes);
            Assert.Contains("named:/shared", pair.Value.Volumes);
            Assert.DoesNotContain(pair.Value.Volumes, mount => mount.StartsWith("anonymous-") &&
                mount != $"anonymous-{pair.Key}:/cache");
        }
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("volume-remove:"));
    }

    [Fact]
    public async Task AllReplicaMountsArePreflightedBeforeAnyReplacement()
    {
        var fixture = Replicas(2);
        await UpAsync(fixture);
        fixture.Engine.Mounts["demo_web_2"] = null;
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Engine.Mutations.Clear();
        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CompletedDependencyRequiresEveryInstanceToExitSuccessfully(int secondExit, bool expected)
    {
        var fixture = Replicas(2);
        AddDependent(fixture, DependencyCondition.ServiceCompletedSuccessfully);
        fixture.Engine.AfterStart = name =>
        {
            if (!name.StartsWith("demo_web")) return;
            fixture.Engine.States[name] = ContainerState.Stopped;
            fixture.Engine.ExitCodes[name] = name == "demo_web_2" ? secondExit : 0;
        };
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.Equal(expected, result.AllSucceeded);
        Assert.Equal(expected, fixture.Engine.Containers.ContainsKey("demo_dependent"));
        if (!expected) Assert.Equal(ComposeServiceAction.Blocked, result.Services.Last().Action);
    }

    [Fact]
    public async Task FirstSuccessfulExitCannotReleaseDependentWhileAnotherReplicaIsRunning()
    {
        using var cts = new CancellationTokenSource();
        var fixture = Replicas(2);
        AddDependent(fixture, DependencyCondition.ServiceCompletedSuccessfully);
        fixture.Engine.AfterStart = name =>
        {
            if (name != "demo_web") return;
            fixture.Engine.States[name] = ContainerState.Stopped;
            fixture.Engine.ExitCodes[name] = 0;
        };
        fixture.Monitor.RefreshRequested = () =>
        {
            if (fixture.Engine.Containers.ContainsKey("demo_web_2"))
                cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        };

        var result = await fixture.Supervisor.UpAsync(fixture.Project, cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(2, result.Started);
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_web_2"]);
        Assert.False(fixture.Engine.Containers.ContainsKey("demo_dependent"));
        Assert.Equal(2, fixture.SavedProject!.AppliedServices.Count);
    }

    [Fact]
    public async Task HealthyDependencyUsesEachReplicaIdentityAndFreshEvidence()
    {
        var fixture = Replicas(2);
        fixture.Project.Services[0].Health = new() { Command = "true" };
        AddDependent(fixture, DependencyCondition.ServiceHealthy);
        fixture.Monitor.RefreshRequested = () =>
        {
            var names = fixture.Engine.Containers.Keys.Where(n => n.StartsWith("demo_web")).ToList();
            fixture.Monitor.Latest = new(names.Select(n => new ContainerInfo
            {
                Name = n, Id = n, StateValue = (int)ContainerState.Running,
            }).ToArray());
            fixture.Health.Latest = new(names.Select(n => new ContainerHealthSnapshot
            {
                ContainerName = n, ContainerId = n, State = ContainerHealthState.Healthy,
                ObservedAt = DateTimeOffset.UtcNow,
            }).ToArray());
        };
        await UpAsync(fixture);
        Assert.Equal(2, fixture.HealthChecks.Count);
        Assert.Contains("run:demo_dependent", fixture.Engine.Mutations);
    }

    [Fact]
    public async Task OneUnhealthyReplicaCannotReleaseDependentAndCancellationPreservesCompletedInstances()
    {
        using var cts = new CancellationTokenSource();
        var fixture = Replicas(2);
        fixture.Project.Services[0].Health = new() { Command = "true" };
        AddDependent(fixture, DependencyCondition.ServiceHealthy);
        fixture.Monitor.RefreshRequested = () =>
        {
            var names = fixture.Engine.Containers.Keys.Where(n => n.StartsWith("demo_web")).ToList();
            fixture.Monitor.Latest = new(names.Select(n => new ContainerInfo
            {
                Name = n, Id = n, StateValue = (int)ContainerState.Running,
            }).ToArray());
            fixture.Health.Latest = new(names.Select(n => new ContainerHealthSnapshot
            {
                ContainerName = n, ContainerId = n,
                State = n == "demo_web" ? ContainerHealthState.Healthy : ContainerHealthState.Down,
                ObservedAt = DateTimeOffset.UtcNow,
            }).ToArray());
            if (names.Count == 2) cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        };
        var result = await fixture.Supervisor.UpAsync(fixture.Project, cts.Token);
        Assert.True(result.IsCancelled);
        Assert.Equal(2, result.Started);
        Assert.False(fixture.Engine.Containers.ContainsKey("demo_dependent"));
        Assert.Equal(2, fixture.SavedProject!.AppliedServices.Count);
    }

    [Fact]
    public async Task ScalingDependencyAloneDoesNotRestartRetainedDependents()
    {
        var fixture = Replicas(1);
        AddDependent(fixture);
        fixture.Project.Services[1].DependsOn[0].Restart = true;
        await UpAsync(fixture);
        fixture.Project.Services[0].Replicas = 3;
        fixture.Engine.Mutations.Clear();
        await UpAsync(fixture);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("dependent") || m is "stop:demo_web" or "remove:demo_web");
    }

    [Fact]
    public async Task RestartWaitsForEverySelectedCompletedDependencyInstance()
    {
        var fixture = Replicas(2);
        AddDependent(fixture);
        await UpAsync(fixture);
        // Change only the applied dependency gate to model a persisted completed-service project.
        fixture.SavedProject!.AppliedServices["dependent"].Service.DependsOn[0].Condition =
            DependencyCondition.ServiceCompletedSuccessfully;
        fixture.Engine.AfterStart = name =>
        {
            if (!name.StartsWith("demo_web")) return;
            fixture.Engine.States[name] = ContainerState.Stopped;
            fixture.Engine.ExitCodes[name] = name == "demo_web_2" ? 1 : 0;
        };
        fixture.Engine.Mutations.Clear();
        var result = await fixture.Supervisor.OperateAsync("demo", new() { Operation = ComposeLifecycleOperation.Restart });
        Assert.False(result.AllSucceeded);
        Assert.Equal(ComposeServiceAction.Blocked, result.Services.Single(s => s.Service == "dependent").Action);
        Assert.DoesNotContain("stop:demo_dependent", fixture.Engine.Mutations);
    }

    [Fact]
    public async Task PartialCreationFailurePersistsSuccessfulInstancesAndBlocksDependent()
    {
        var fixture = Replicas(3, engine: new Engine { FailRun = "demo_web_2" });
        AddDependent(fixture);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.Started);
        Assert.Equal(["web", "web#3"], fixture.SavedProject!.AppliedServices.Keys);
        Assert.False(result.Services.Single(s => s.InstanceKey == "web#2").Success);
        Assert.False(fixture.Engine.Containers.ContainsKey("demo_dependent"));
        Assert.Equal(2, fixture.RestartPolicies.Count);
    }

    [Fact]
    public async Task CancelledScaleUpReportsCompletedAndUnattemptedInstancesTruthfully()
    {
        using var cts = new CancellationTokenSource();
        var fixture = Replicas(3);
        fixture.Engine.AfterStart = name => { if (name == "demo_web_2") cts.Cancel(); };
        var result = await fixture.Supervisor.UpAsync(fixture.Project, cts.Token);
        Assert.True(result.IsCancelled);
        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.Started);
        Assert.Equal(3, result.Services.Count);
        Assert.False(result.Services.Single(s => s.InstanceKey == "web#3").Success);
        Assert.Equal(["web", "web#2"], fixture.SavedProject!.AppliedServices.Keys);
        Assert.Equal(2, fixture.RestartPolicies.Count);
    }

    [Fact]
    public async Task FailedScaleDownKeepsStoppedSnapshotAndDoesNotAffectRetainedInstance()
    {
        var fixture = Replicas(2);
        await UpAsync(fixture);
        fixture.Engine.FailRemove = "demo_web_2";
        fixture.Project.Services[0].Replicas = 1;
        fixture.Engine.Mutations.Clear();
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.False(result.AllSucceeded);
        Assert.True(fixture.SavedProject!.AppliedServices["web#2"].ManuallyStopped);
        Assert.True(fixture.Suppression.IsSuppressed("demo_web_2"));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m is "stop:demo_web" or "remove:demo_web");
        Assert.Equal("demo_web", Assert.Single(fixture.RestartPolicies).ContainerName);
    }

    [Fact]
    public async Task RestartStopReadoptionAndDownUseAppliedInstancesDespitePendingScaleEdits()
    {
        var fixture = Replicas(3);
        fixture.Project.Services[0].Health = new() { Command = "old-health" };
        await UpAsync(fixture);
        fixture.Project.Services[0].Replicas = 0;
        fixture.Project.Services[0].Options.Image = "unavailable";
        fixture.Project.Services[0].Health!.Command = "pending-health";
        fixture.Engine.Mutations.Clear();
        var restart = await fixture.Supervisor.OperateAsync("demo", new()
        {
            Operation = ComposeLifecycleOperation.Restart, Services = ["web"],
        });
        Assert.True(restart.AllSucceeded);
        Assert.Equal(["web", "web#2", "web#3"], restart.Services.Select(s => s.InstanceKey));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("remove:") || m.StartsWith("pull:"));
        Assert.All(fixture.HealthChecks, h => Assert.Equal("old-health", h.Command));
        var stop = await fixture.Supervisor.OperateAsync("demo", new() { Operation = ComposeLifecycleOperation.Stop });
        Assert.True(stop.AllSucceeded);
        Assert.All(fixture.SavedProject!.AppliedServices.Values, s => Assert.True(s.ManuallyStopped));
        fixture.HealthChecks.Clear();
        fixture.RestartPolicies.Clear();
        await fixture.Supervisor.ReconcileAsync();
        Assert.Empty(fixture.HealthChecks);
        Assert.Empty(fixture.RestartPolicies);
        var down = await fixture.Supervisor.OperateAsync("demo", new() { Operation = ComposeLifecycleOperation.Down });
        Assert.True(down.AllSucceeded);
        Assert.Empty(fixture.Engine.Containers);
        Assert.Empty(fixture.SavedProject.AppliedServices);
    }

    [Fact]
    public async Task ReadoptionEnrollsEveryAppliedReplicaAndRejectsOrdinalMismatch()
    {
        var fixture = Replicas(3);
        fixture.Project.Services[0].Health = new() { Command = "old-health", MaxRestarts = 0 };
        await UpAsync(fixture);
        fixture.Project.Services[0].Replicas = 1;
        fixture.Engine.Containers["demo_web_2"].Labels[ComposeProject.InstanceLabel] = "1";
        fixture.HealthChecks.Clear();
        fixture.RestartPolicies.Clear();
        await fixture.Supervisor.ReconcileAsync();
        Assert.Equal(["demo_web", "demo_web_3"], fixture.HealthChecks.Select(h => h.ContainerName));
        Assert.Equal(["demo_web", "demo_web_3"], fixture.RestartPolicies.Select(h => h.ContainerName));
        Assert.Single(fixture.Supervisor.ReconciliationWarnings);
    }
}
#endif
