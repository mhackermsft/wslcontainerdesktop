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
using Xunit;
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;
using static WslContainerDesktop.Tests.Services.ComposeNetworkSupervisorTests;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeReconciliationSupervisorTests
{
    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    public async Task RepeatedUpKeepsRunningContainersAndDoesNotBuildOrPullAgain(WslcCapabilitySupport support)
    {
        var fixture = new Fixture(support);
        fixture.Project.Services[0].Build = new() { Context = Directory.GetCurrentDirectory() };
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        var fingerprint = fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ConfigHashLabel];
        fixture.Engine.Mutations.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        Assert.Equal(ComposeServiceAction.Keep, Assert.Single(plan.Services).Action);
        Assert.Equal(ComposeServiceChange.Unchanged, plan.Services[0].Change);
        await UpSuccessfullyAsync(fixture);

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(fingerprint, fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ConfigHashLabel]);
        Assert.Equal("sha256:fixture-v1", fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ImageIdLabel]);
        Assert.Single(fixture.RestartPolicies);
        Assert.Equal(fingerprint, fixture.SavedProject!.AppliedServices["web"].Fingerprint);
    }

    [Fact]
    public async Task OrderingOnlyChangesToMapsAndAliasesKeepTheAppliedInstance()
    {
        var fixture = new Fixture();
        var options = fixture.Project.Services[0].Options;
        options.EnvironmentVariables = ["FIRST=1", "SECOND=2"];
        options.Labels["first"] = "1";
        options.Labels["second"] = "2";
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();
        options.EnvironmentVariables.Reverse();
        options.Labels = options.Labels.Reverse().ToDictionary(p => p.Key, p => p.Value);
        options.NetworkAttachments[1].Aliases.Reverse();

        await UpSuccessfullyAsync(fixture);

        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task ConfigurationChangeExplainsAndRecreatesOnlyTheChangedService()
    {
        var fixture = new Fixture();
        AddService(fixture, "worker");
        await UpSuccessfullyAsync(fixture);
        var oldFingerprint = fixture.SavedProject!.AppliedServices["web"].Fingerprint;
        fixture.Engine.Mutations.Clear();
        fixture.Project.Services[0].Options.EnvironmentVariables.Add("MODE=new");

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        var changed = plan.Services.Single(p => p.Service.Name == "web");
        Assert.Equal(ComposeServiceChange.Changed, changed.Change);
        Assert.Equal(ComposeServiceAction.Recreate, changed.Action);
        Assert.False(string.IsNullOrWhiteSpace(changed.Reason));
        Assert.Empty(fixture.Engine.Mutations);
        await UpSuccessfullyAsync(fixture);

        Assert.Contains("stop:demo_web", fixture.Engine.Mutations);
        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
        Assert.Contains("create:demo_web", fixture.Engine.Mutations);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_worker", StringComparison.Ordinal));
        Assert.NotEqual(oldFingerprint, fixture.SavedProject!.AppliedServices["web"].Fingerprint);
        Assert.Contains("MODE=new", fixture.Engine.Containers["demo_web"].EnvironmentVariables);
    }

    [Fact]
    public async Task LocalImageIdentityChangeRecreatesEvenWithUnchangedConfiguration()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();
        fixture.Engine.Images[0].Id = "sha256:updated";

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        Assert.Equal(ComposeServiceAction.Recreate, Assert.Single(plan.Services).Action);
        Assert.Contains("image", plan.Services[0].Reason, StringComparison.OrdinalIgnoreCase);
        await UpSuccessfullyAsync(fixture);

        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("pull:", StringComparison.Ordinal));
        Assert.Equal("sha256:updated", fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ImageIdLabel]);
    }

    [Fact]
    public async Task StoppedUnchangedServiceStartsWithoutRemovalOrEndpointMutation()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.States["demo_web"] = ContainerState.Stopped;
        fixture.Suppression.Suppress("demo_web");
        fixture.Engine.Mutations.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        Assert.Equal(ComposeServiceAction.Start, Assert.Single(plan.Services).Action);
        await UpSuccessfullyAsync(fixture);

        Assert.Equal(["start:demo_web"], fixture.Engine.Mutations);
        Assert.False(fixture.Engine.LastStartWasExplicit);
        Assert.False(fixture.Suppression.IsSuppressed("demo_web"));
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_web"]);
    }

    [Fact]
    public async Task ForceRecreateIsExplicitAndScopedToRequestedServices()
    {
        var fixture = new Fixture();
        AddService(fixture, "worker");
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();

        var result = await fixture.Supervisor.UpAsync(fixture.Project,
            new ComposeOperationRequest { Services = ["worker"], ForceRecreate = true });

        Assert.True(result.AllSucceeded);
        Assert.Equal(["stop:demo_worker", "remove:demo_worker", "run:demo_worker"], fixture.Engine.Mutations);
        Assert.Equal(ComposeServiceAction.Recreate, Assert.Single(result.Services).Action);
    }

    [Fact]
    public async Task RestartUsesAppliedSnapshotAndDoesNotApplyPendingDesiredChanges()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        var appliedFingerprint = fixture.SavedProject!.AppliedServices["web"].Fingerprint;
        fixture.Project.Services[0].Options.Command = "pending-command";
        fixture.Project.Services[0].Options.Image = "not-present";
        fixture.Project.Services[0].Options.NetworkAttachments[1].Ipv4Address = "invalid-desired-ip";
        fixture.Project.Services[0].Restart = RestartPolicyKind.No;
        fixture.Engine.Mutations.Clear();
        fixture.Engine.Reads.Clear();

        var result = await fixture.Supervisor.OperateAsync("demo",
            new() { Operation = ComposeLifecycleOperation.Restart, Services = ["web"] });

        Assert.True(result.AllSucceeded);
        Assert.Equal(["stop:demo_web", "start:demo_web"], fixture.Engine.Mutations);
        Assert.DoesNotContain("images", fixture.Engine.Reads);
        Assert.Null(fixture.Engine.Containers["demo_web"].Command);
        Assert.Equal("sha256:fixture-v1", fixture.Engine.Containers["demo_web"].Image);
        Assert.Equal(appliedFingerprint, fixture.SavedProject!.AppliedServices["web"].Fingerprint);
        Assert.Equal(RestartPolicyKind.Always, Assert.Single(fixture.RestartPolicies).Policy);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public async Task TargetedStopAndDownDoNotTraverseDependenciesOrRemoveResources(ComposeLifecycleOperation operation)
    {
        var fixture = new Fixture();
        var worker = AddService(fixture, "worker");
        worker.DependsOn = [new() { ServiceName = "web" }];
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        fixture.Project.Networks = [new() { Name = "a" }];
        fixture.Engine.Networks["a"] = "demo";
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();

        var result = await fixture.Supervisor.OperateAsync("demo", new() { Operation = operation, Services = ["worker"] });

        Assert.True(result.AllSucceeded);
        Assert.Equal(operation == ComposeLifecycleOperation.Stop
            ? ["stop:demo_worker"] : new[] { "stop:demo_worker", "remove:demo_worker" }, fixture.Engine.Mutations);
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_web"]);
        Assert.Equal("demo_web", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.True(fixture.Engine.Networks.ContainsKey("a"));
        Assert.True(fixture.Suppression.IsSuppressed("demo_worker"));
        if (operation == ComposeLifecycleOperation.Stop)
            Assert.True(fixture.SavedProject!.AppliedServices["worker"].ManuallyStopped);
        else
            Assert.False(fixture.SavedProject!.AppliedServices.ContainsKey("worker"));
    }

    [Fact]
    public async Task ExplicitInactiveProfileTargetIncludesRequiredDependenciesButNotProfilePeers()
    {
        var fixture = new Fixture();
        var target = AddService(fixture, "target");
        target.Profiles = ["tools"];
        target.DependsOn = [new() { ServiceName = "web" }];
        AddService(fixture, "peer").Profiles = ["tools"];
        AddService(fixture, "unrelated");

        var result = await fixture.Supervisor.UpAsync(fixture.Project, new ComposeOperationRequest { Services = ["target"] });

        Assert.True(result.AllSucceeded);
        Assert.Equal(["web", "target"], result.Services.Select(s => s.Service));
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("peer") || m.Contains("unrelated"));
    }

    [Fact]
    public async Task DefaultUpDoesNotActivateProfileOnlyServicesOrTheirUnusedResources()
    {
        var fixture = new Fixture();
        var inactive = AddService(fixture, "tools");
        inactive.Profiles = ["tools"];
        inactive.Options.Network = "missing";
        fixture.Project.Networks = [new() { Name = "missing", External = true }];

        await UpSuccessfullyAsync(fixture);

        Assert.Single(fixture.Engine.Containers);
        Assert.DoesNotContain("network:missing", fixture.Engine.Reads);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(7, false)]
    public async Task CompletedSuccessfullyDependencyChecksExitStatusBeforeStartingConsumer(int exitCode, bool success)
    {
        var fixture = new Fixture();
        var consumer = AddService(fixture, "consumer");
        consumer.DependsOn = [new() { ServiceName = "web", Condition = DependencyCondition.ServiceCompletedSuccessfully }];
        fixture.Engine.AfterStart = name =>
        {
            fixture.Engine.States[name] = ContainerState.Stopped;
            fixture.Engine.ExitCodes[name] = exitCode;
        };

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.Equal(success, result.AllSucceeded);
        Assert.Equal(success, result.Services.Single(s => s.Service == "consumer").Success);
        Assert.Equal(success, fixture.Engine.Containers.ContainsKey("demo_consumer"));
    }

    [Fact]
    public async Task SuccessfulCompletedInitIsReusedOnRepeatedUpWithoutRerunningIt()
    {
        var fixture = new Fixture();
        var consumer = AddService(fixture, "consumer");
        consumer.DependsOn = [new() { ServiceName = "web", Condition = DependencyCondition.ServiceCompletedSuccessfully }];
        fixture.Engine.AfterStart = name => fixture.Engine.States[name] = ContainerState.Stopped;
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.States["demo_consumer"] = ContainerState.Stopped;
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Equal(["start:demo_consumer"], fixture.Engine.Mutations);
        Assert.Equal(ContainerState.Stopped, fixture.Engine.States["demo_web"]);
    }

    [Fact]
    public async Task OptionalMissingDependencyDoesNotBlockTheSelectedService()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].DependsOn = [new() { ServiceName = "absent", Required = false }];

        await UpSuccessfullyAsync(fixture);

        Assert.Single(fixture.Engine.Containers);
    }

    [Fact]
    public async Task OptionalFailedDependencyDoesNotBlockIndependentStartup()
    {
        var fixture = new Fixture(engine: new Engine { FailRun = "demo_optional" });
        var optional = AddService(fixture, "optional");
        fixture.Project.Services[0].DependsOn = [new() { ServiceName = optional.Name, Required = false }];

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.True(result.Services.Single(s => s.Service == "web").Success);
        Assert.False(result.Services.Single(s => s.Service == "optional").Success);
    }

    [Fact]
    public async Task RestartTrueDependencyCascadesWithoutRecreatingOrdinaryDependents()
    {
        var fixture = new Fixture();
        var restartPeer = AddService(fixture, "restart_peer");
        restartPeer.DependsOn = [new() { ServiceName = "web", Restart = true }];
        var keepPeer = AddService(fixture, "keep_peer");
        keepPeer.DependsOn = [new() { ServiceName = "web" }];
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
        Assert.Equal(["stop:demo_restart_peer", "start:demo_restart_peer"],
            fixture.Engine.Mutations.Where(m => m.Contains("demo_restart_peer")));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_keep_peer"));
        Assert.True(fixture.Engine.Mutations.IndexOf("start:demo_web") <
                    fixture.Engine.Mutations.IndexOf("stop:demo_restart_peer"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NamespaceSharingSidecarRecreatesWithItsProviderButRemainsUntouchedWhenProviderFails(
        bool targeted, bool providerFails)
    {
        var fixture = new Fixture();
        fixture.Project.Services.Clear();
        var provider = AddService(fixture, "db");
        var sidecar = AddService(fixture, "sidecar");
        sidecar.Options.NetworkMode = "service:db";
        sidecar.Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        var originalSidecarFingerprint = fixture.SavedProject!.AppliedServices["sidecar"].Fingerprint;
        provider.Options.Command = "updated-provider";
        fixture.Engine.FailRunAfterCreate = providerFails;
        fixture.Engine.Mutations.Clear();
        var request = new ComposeOperationRequest { Services = targeted ? ["sidecar"] : [] };

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project, request);
        Assert.Equal(ComposeServiceAction.Recreate, plan.Services.Single(p => p.Service.Name == "db").Action);
        Assert.Equal(ComposeServiceAction.Recreate, plan.Services.Single(p => p.Service.Name == "sidecar").Action);
        Assert.Empty(fixture.Engine.Mutations);
        var result = await fixture.Supervisor.UpAsync(fixture.Project, request);

        Assert.Equal(!providerFails, result.AllSucceeded);
        var sidecarResult = result.Services.Single(s => s.Service == "sidecar");
        Assert.Equal(!providerFails, sidecarResult.Success);
        if (providerFails)
        {
            Assert.Equal(ComposeServiceAction.Blocked, sidecarResult.Action);
            Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_sidecar"));
            Assert.Equal(originalSidecarFingerprint, fixture.SavedProject!.AppliedServices["sidecar"].Fingerprint);
            Assert.Equal("demo_sidecar", Assert.Single(fixture.RestartPolicies).ContainerName);
        }
        else
        {
            Assert.Equal(ComposeServiceAction.Recreate, sidecarResult.Action);
            Assert.Equal(["stop:demo_sidecar", "remove:demo_sidecar", "run:demo_sidecar"],
                fixture.Engine.Mutations.Where(m => m.Contains("demo_sidecar")));
            Assert.True(fixture.Engine.Mutations.IndexOf("run:demo_db") <
                        fixture.Engine.Mutations.IndexOf("stop:demo_sidecar"));
            Assert.Equal("container:demo_db", fixture.Engine.Containers["demo_sidecar"].NetworkMode);
        }
    }

    [Theory]
    [InlineData("pull")]
    [InlineData("build")]
    [InlineData("images")]
    [InlineData("network")]
    [InlineData("file")]
    [InlineData("ownership")]
    public async Task EverySelectedServiceIsPreflightedBeforeAnyContainerIsTornDown(string failure)
    {
        var fixture = new Fixture();
        var worker = AddService(fixture, "worker");
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Options.Command = "requires-recreate";
        worker.Options.Command = "also-requires-recreate";
        fixture.Engine.Mutations.Clear();
        switch (failure)
        {
            case "pull":
                worker.Options.Image = "missing-image";
                fixture.Engine.FailPull = true;
                break;
            case "build":
                worker.Build = new() { Context = Directory.GetCurrentDirectory() };
                fixture.Engine.FailBuild = true;
                break;
            case "images":
                fixture.Engine.ImageInventoryError = new IOException("image inventory unavailable");
                break;
            case "network":
                fixture.Project.Networks = [new() { Name = "missing-network", External = true }];
                worker.Options.Network = "missing-network";
                break;
            case "file":
                worker.Secrets = [new() { Source = "missing-secret", Target = "/run/secrets/missing" }];
                break;
            case "ownership":
                fixture.Engine.Containers["demo_worker"].Labels[ComposeProject.ProjectLabel] = "foreign";
                break;
        }

        var error = await Record.ExceptionAsync(async () =>
        {
            var result = await fixture.Supervisor.UpAsync(fixture.Project, new ComposeOperationRequest { Build = failure == "build" });
            Assert.False(result.AllSucceeded);
        });

        if (error is not null)
            Assert.True(error is InvalidOperationException or IOException, error.ToString());
        Assert.DoesNotContain(fixture.Engine.Mutations, m =>
            m.StartsWith("stop:") || m.StartsWith("remove:") || m.StartsWith("run:") || m.StartsWith("create:"));
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.Equal(2, fixture.RestartPolicies.Count);
    }

    [Fact]
    public async Task ImageMissingPullsBeforeCreatingAndRepeatedUpDoesNotPull()
    {
        var fixture = new Fixture();
        fixture.Engine.Images.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Equal("pull:fixture", fixture.Engine.Mutations[0]);
        Assert.Equal(fixture.Engine.NextImageId, fixture.SavedProject!.AppliedServices["web"].ImageId);
        fixture.Engine.Mutations.Clear();
        await UpSuccessfullyAsync(fixture);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task ExplicitBuildRunsBeforeTeardownButPreviewNeverBuilds()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Build = new() { Context = Directory.GetCurrentDirectory() };
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();
        var request = new ComposeOperationRequest { Build = true };

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project, request);
        Assert.Equal(ComposeServiceAction.Recreate, Assert.Single(plan.Services).Action);
        Assert.Equal(ComposeImageAction.Build, plan.Services[0].ImageAction);
        Assert.Empty(fixture.Engine.Mutations);
        var result = await fixture.Supervisor.UpAsync(fixture.Project, request);

        Assert.True(result.AllSucceeded);
        Assert.Equal("build:fixture", fixture.Engine.Mutations[0]);
        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
    }

    [Fact]
    public async Task PreviewReportsMissingImagePullWithoutPerformingIt()
    {
        var fixture = new Fixture();
        fixture.Engine.Images.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);

        Assert.True(plan.CanApply);
        Assert.Equal(ComposeImageAction.Pull, Assert.Single(plan.Services).ImageAction);
        Assert.Equal(ComposeServiceAction.Create, plan.Services[0].Action);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.Engine.Images);
        Assert.Null(fixture.SavedProject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedImagePreparationIsDeduplicatedByReference(bool build)
    {
        var fixture = new Fixture();
        var worker = AddService(fixture, "worker");
        fixture.Engine.Images.Clear();
        if (build)
        {
            fixture.Project.Services[0].Build = new() { Context = Directory.GetCurrentDirectory() };
            worker.Build = new() { Context = Directory.GetCurrentDirectory() };
        }
        else
            fixture.Project.Services.ForEach(s => s.PullPolicy = ComposeImagePolicy.Always);

        var result = await fixture.Supervisor.UpAsync(fixture.Project, new ComposeOperationRequest { Build = build });

        Assert.True(result.AllSucceeded);
        Assert.Equal(build ? "build:fixture" : "pull:fixture",
            Assert.Single(fixture.Engine.Mutations, m => m.StartsWith("build:") || m.StartsWith("pull:")));
        Assert.All(fixture.Engine.Containers.Values, c => Assert.Equal(fixture.Engine.NextImageId, c.Image));
        Assert.Equal(["web", "worker"], result.Services.Select(s => s.Service));
    }

    [Fact]
    public async Task BuildProviderPreparesSharedImageBeforeConsumersButStartupKeepsDependencyOrder()
    {
        var fixture = new Fixture();
        var provider = AddService(fixture, "provider");
        provider.Build = new() { Context = Directory.GetCurrentDirectory() };
        provider.DependsOn = [new() { ServiceName = "web" }];
        fixture.Engine.Images.Clear();

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.True(result.AllSucceeded);
        Assert.Equal("build:fixture", fixture.Engine.Mutations[0]);
        Assert.DoesNotContain("pull:fixture", fixture.Engine.Mutations);
        Assert.Equal(["web", "provider"], result.Services.Select(s => s.Service));
        Assert.True(fixture.Engine.Mutations.IndexOf("start:demo_web") < fixture.Engine.Mutations.IndexOf("run:demo_provider"));
        Assert.All(fixture.Engine.Containers.Values, c => Assert.Equal(fixture.Engine.NextImageId, c.Image));
    }

    [Fact]
    public async Task ConflictingBuildDefinitionsForSameTagBlockAllMutations()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Build = new() { Context = Directory.GetCurrentDirectory(), Args = ["MODE=first"] };
        AddService(fixture, "worker").Build = new() { Context = Directory.GetCurrentDirectory(), Args = ["MODE=second"] };

        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project, new ComposeOperationRequest { Build = true })).AllSucceeded);

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.Engine.Containers);
    }

    [Fact]
    public async Task LaterSharedTagPullPropagatesFinalIdentityToEarlierConsumers()
    {
        var fixture = new Fixture();
        var worker = AddService(fixture, "worker");
        worker.DependsOn = [new() { ServiceName = "web" }];
        await UpSuccessfullyAsync(fixture);
        worker.PullPolicy = ComposeImagePolicy.Always;
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Equal("pull:fixture", fixture.Engine.Mutations[0]);
        Assert.Single(fixture.Engine.Mutations, m => m.StartsWith("pull:"));
        Assert.All(fixture.Engine.Containers.Values, c => Assert.Equal(fixture.Engine.NextImageId, c.Image));
        Assert.All(fixture.SavedProject!.AppliedServices.Values, a => Assert.Equal(fixture.Engine.NextImageId, a.ImageId));
        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
        Assert.Contains("remove:demo_worker", fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(11, false)]
    public async Task RestartWaitsForSelectedCompletedDependencyBeforeTouchingConsumer(int exitCode, bool success)
    {
        var fixture = new Fixture();
        AddService(fixture, "consumer").DependsOn =
            [new() { ServiceName = "web", Condition = DependencyCondition.ServiceCompletedSuccessfully }];
        fixture.Engine.AfterStart = name =>
        {
            if (name == "demo_web")
                fixture.Engine.States[name] = ContainerState.Stopped;
        };
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.ExitCodes["demo_web"] = exitCode;
        fixture.Engine.Mutations.Clear();

        var result = await fixture.Supervisor.OperateAsync("demo", new() { Operation = ComposeLifecycleOperation.Restart });

        Assert.Equal(success, result.AllSucceeded);
        Assert.Equal(success, result.Services.Single(s => s.Service == "consumer").Success);
        Assert.Equal(success, fixture.Engine.Mutations.Contains("stop:demo_consumer"));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("remove:") || m.StartsWith("run:") || m.StartsWith("create:"));
    }

    [Fact]
    public async Task AlwaysPullOnlyRecreatesWhenTheResolvedImageActuallyChanges()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].PullPolicy = ComposeImagePolicy.Always;
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Equal(["pull:fixture"], fixture.Engine.Mutations);
        fixture.Engine.NextImageId = "sha256:fixture-v3";
        fixture.Engine.Mutations.Clear();
        await UpSuccessfullyAsync(fixture);
        Assert.Equal("pull:fixture", fixture.Engine.Mutations[0]);
        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
        Assert.Equal("sha256:fixture-v3", fixture.SavedProject!.AppliedServices["web"].ImageId);
    }

    [Fact]
    public async Task MissingImageWithNeverPullPolicyFailsBeforeCreatingAnything()
    {
        var fixture = new Fixture();
        fixture.Engine.Images.Clear();
        fixture.Project.Services[0].PullPolicy = ComposeImagePolicy.Never;

        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.Engine.Containers);
        Assert.Null(fixture.SavedProject);
    }

    [Fact]
    public async Task MissingBuildContextCannotTearDownAnExistingService()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Build = new()
        {
            Context = Path.Combine(Directory.GetCurrentDirectory(), "nonexistent-compose-context-" + Guid.NewGuid().ToString("N")),
        };
        fixture.Engine.Mutations.Clear();

        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project, new ComposeOperationRequest { Build = true })).AllSucceeded);

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_web"]);
    }

    [Theory]
    [InlineData(ContainerState.Running)]
    [InlineData(ContainerState.Stopped)]
    public async Task LaterManualStopRemainsSuppressedAndPersistedWhenUpReusesAnInstance(ContainerState state)
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.States["demo_web"] = state;
        fixture.Engine.Mutations.Clear();
        fixture.BeforeCapabilities = () =>
        {
            fixture.Suppression.Suppress("demo_web");
            return Task.CompletedTask;
        };

        await UpSuccessfullyAsync(fixture);

        Assert.True(fixture.Suppression.IsSuppressed("demo_web"));
        Assert.True(fixture.SavedProject!.AppliedServices["web"].ManuallyStopped);
        Assert.DoesNotContain("remove:demo_web", fixture.Engine.Mutations);
        Assert.False(fixture.Engine.LastStartWasExplicit);
    }

    [Fact]
    public async Task InstanceReplacementAfterPreflightCannotBeMutatedOrRecordedAsApplied()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.States["demo_web"] = ContainerState.Stopped;
        fixture.Engine.Mutations.Clear();
        fixture.BeforeCapabilities = () =>
        {
            fixture.Engine.ContainerIds["demo_web"] = "externally-replaced-id";
            return Task.CompletedTask;
        };

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal("demo_web", fixture.SavedProject!.AppliedServices["web"].ContainerId);
    }

    [Fact]
    public async Task ExplicitDependencyRestartCascadesOnlyToRestartTrueDependents()
    {
        var fixture = new Fixture();
        AddService(fixture, "restart_peer").DependsOn = [new() { ServiceName = "web", Restart = true }];
        AddService(fixture, "keep_peer").DependsOn = [new() { ServiceName = "web" }];
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();

        var result = await fixture.Supervisor.OperateAsync("demo",
            new() { Operation = ComposeLifecycleOperation.Restart, Services = ["web"] });

        Assert.True(result.AllSucceeded);
        Assert.Equal(["web", "restart_peer"], result.Services.Select(s => s.Service));
        Assert.Equal(["stop:demo_web", "start:demo_web", "stop:demo_restart_peer", "start:demo_restart_peer"],
            fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("remove")]
    [InlineData("start")]
    public async Task PartialMutationFailureKeepsUnaffectedSiblingsSupervised(string failure)
    {
        var fixture = new Fixture();
        AddService(fixture, "worker");
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        await UpSuccessfullyAsync(fixture);
        var originalPolicy = fixture.RestartPolicies.Single(p => p.ContainerName == "demo_web");
        var originalHealth = new HealthCheckConfig { ContainerName = "demo_web", Command = "original-probe" };
        fixture.HealthChecks.Add(originalHealth);
        fixture.Engine.Mutations.Clear();
        fixture.Project.Services[0].Options.Command = "updated";
        if (failure == "stop") fixture.Engine.FailStop = "demo_web";
        if (failure == "remove") fixture.Engine.FailRemove = "demo_web";
        if (failure == "start") fixture.Engine.FailStart = "demo_web";

        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.True(result.Services.Single(s => s.Service == "worker").Success);
        Assert.Contains(fixture.RestartPolicies, r => r.ContainerName == "demo_worker");
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_worker"));
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_worker"]);
        if (failure is "stop" or "remove")
        {
            Assert.Same(originalPolicy, fixture.RestartPolicies.Single(p => p.ContainerName == "demo_web"));
            Assert.Same(originalHealth, fixture.HealthChecks.Single(p => p.ContainerName == "demo_web"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRestartRestoresTheOriginalSupervisionObjects(bool failStop)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        var restart = Assert.Single(fixture.RestartPolicies);
        var health = new HealthCheckConfig { ContainerName = "demo_web", Command = "original-probe" };
        fixture.HealthChecks.Add(health);
        if (failStop) fixture.Engine.FailStop = "demo_web";
        else fixture.Engine.FailStart = "demo_web";
        fixture.Engine.Mutations.Clear();

        var result = await fixture.Supervisor.OperateAsync("demo",
            new() { Operation = ComposeLifecycleOperation.Restart });

        Assert.False(result.AllSucceeded);
        Assert.Same(restart, Assert.Single(fixture.RestartPolicies));
        Assert.Same(health, Assert.Single(fixture.HealthChecks));
        Assert.DoesNotContain("remove:demo_web", fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    public async Task CancellationAfterAcknowledgedStartupStillPersistsTheSuccessfulIdentity(WslcCapabilitySupport support)
    {
        using var cts = new CancellationTokenSource();
        var fixture = new Fixture(support);
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        AddService(fixture, "worker");
        fixture.Engine.AfterStart = _ => cts.Cancel();
        fixture.Engine.AfterRun = name =>
        {
            fixture.Engine.ContainerIds[name] = "verified-run-id";
            cts.Cancel();
        };

        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project, cts.Token)).IsCancelled);

        Assert.Equal(support == WslcCapabilitySupport.Supported ? "demo_web" : "verified-run-id",
            fixture.SavedProject!.AppliedServices["web"].ContainerId);
        Assert.Equal("demo_web", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.True(fixture.Engine.Containers.ContainsKey("demo_web"));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_worker") || m.StartsWith("remove:"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PartialLegacyRunCleanupRequiresMatchingOperationOwnership(bool cancel, bool replaced)
    {
        using var cts = new CancellationTokenSource();
        var fixture = new Fixture(WslcCapabilitySupport.Unsupported);
        fixture.Engine.FailRunAfterCreate = true;
        fixture.Engine.AfterRun = name =>
        {
            if (replaced)
                fixture.Engine.Containers[name].Labels.Clear();
            if (cancel)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
        };

        if (cancel)
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project, cts.Token)).IsCancelled);
        else
            Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);

        Assert.Equal(replaced, fixture.Engine.Containers.ContainsKey("demo_web"));
        Assert.Equal(!replaced, fixture.Engine.Mutations.Contains("remove:demo_web"));
        Assert.Empty(fixture.SavedProject!.AppliedServices);
        Assert.Empty(fixture.RestartPolicies);
    }

    [Fact]
    public async Task CancellationAfterSuccessfulReplacementKeepsCompletedAndUntouchedSnapshots()
    {
        using var cts = new CancellationTokenSource();
        var fixture = new Fixture();
        AddService(fixture, "worker");
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        await UpSuccessfullyAsync(fixture);
        var workerFingerprint = fixture.SavedProject!.AppliedServices["worker"].Fingerprint;
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Project.Services[1].Options.Command = "not-yet-applied";
        fixture.Engine.Mutations.Clear();
        fixture.Engine.AfterStart = _ => cts.Cancel();

        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project, cts.Token)).IsCancelled);

        Assert.Contains(fixture.RestartPolicies, p => p.ContainerName == "demo_web");
        Assert.Contains(fixture.RestartPolicies, p => p.ContainerName == "demo_worker");
        Assert.Equal("updated", fixture.SavedProject!.AppliedServices["web"].Service.Options.Command);
        Assert.Equal(workerFingerprint, fixture.SavedProject.AppliedServices["worker"].Fingerprint);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.Contains("demo_worker"));
    }

    [Fact]
    public async Task ReadoptionUsesAppliedHealthAndRestartSettingsDespitePendingDesiredEdits()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        fixture.Project.Services[0].Health = new() { Command = "applied-probe", MaxRestarts = 0 };
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Restart = RestartPolicyKind.No;
        fixture.Project.Services[0].Health!.Command = "pending-probe";
        fixture.Project.Services[0].Options.NetworkAttachments[1].Aliases = ["pending-alias"];
        fixture.RestartPolicies.Clear();
        fixture.HealthChecks.Clear();
        fixture.Engine.Mutations.Clear();

        await fixture.Supervisor.ReconcileAsync();

        Assert.Empty(fixture.Supervisor.ReconciliationWarnings);
        Assert.Equal(RestartPolicyKind.Always, Assert.Single(fixture.RestartPolicies).Policy);
        Assert.Equal("applied-probe", Assert.Single(fixture.HealthChecks).Command);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task StoppedAppliedServiceIsNotReadoptedIntoSupervision()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        var result = await fixture.Supervisor.OperateAsync("demo",
            new() { Operation = ComposeLifecycleOperation.Stop, Services = ["web"] });
        Assert.True(result.AllSucceeded);
        fixture.Engine.Mutations.Clear();
        var relaunched = new Fixture(engine: fixture.Engine, persisted: fixture.SavedProject);
        Assert.False(relaunched.Suppression.IsSuppressed("demo_web"));

        await relaunched.Supervisor.ReconcileAsync();

        Assert.Empty(relaunched.RestartPolicies);
        Assert.True(relaunched.Suppression.IsSuppressed("demo_web"));
        Assert.True(relaunched.SavedProject!.AppliedServices["web"].ManuallyStopped);
        Assert.Empty(relaunched.Engine.Mutations);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Up, false, false)]
    [InlineData(ComposeLifecycleOperation.Restart, false, false)]
    [InlineData(ComposeLifecycleOperation.Up, true, false)]
    [InlineData(ComposeLifecycleOperation.Restart, true, false)]
    [InlineData(ComposeLifecycleOperation.Up, false, true)]
    [InlineData(ComposeLifecycleOperation.Restart, false, true)]
    public async Task PersistedStopClearsOnlyAfterSuccessfulExplicitResumeWithoutANewerStop(
        ComposeLifecycleOperation operation, bool fail, bool laterStop)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        Assert.True((await fixture.Supervisor.OperateAsync("demo",
            new() { Operation = ComposeLifecycleOperation.Stop, Services = ["web"] })).AllSucceeded);
        Assert.True(fixture.SavedProject!.AppliedServices["web"].ManuallyStopped);
        if (fail)
            fixture.Engine.FailStart = "demo_web";
        if (laterStop)
            fixture.BeforeCapabilities = () =>
            {
                fixture.Suppression.Suppress("demo_web");
                return Task.CompletedTask;
            };

        var result = operation == ComposeLifecycleOperation.Up
            ? await fixture.Supervisor.UpAsync(fixture.Project, new ComposeOperationRequest { Services = ["web"] })
            : await fixture.Supervisor.OperateAsync("demo", new() { Operation = operation, Services = ["web"] });

        Assert.Equal(!fail, result.AllSucceeded);
        Assert.Equal(fail || laterStop, fixture.SavedProject!.AppliedServices["web"].ManuallyStopped);
        Assert.Equal(fail || laterStop, fixture.Suppression.IsSuppressed("demo_web"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStopPersistsIntentBeforeIssuingCommandAndNeverRestoresStoppedSupervision(bool cancelledAfterCommit)
    {
        using var cts = new CancellationTokenSource();
        var fixture = new Fixture();
        AddService(fixture, "worker");
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        await UpSuccessfullyAsync(fixture);
        var workerPolicy = fixture.RestartPolicies.Single(p => p.ContainerName == "demo_worker");
        fixture.Engine.Mutations.Clear();
        if (cancelledAfterCommit)
            fixture.Engine.AfterStop = name =>
            {
                Assert.True(fixture.SavedProject!.AppliedServices["web"].ManuallyStopped);
                Assert.Equal(ContainerState.Stopped, fixture.Engine.States[name]);
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            };
        else
            fixture.Engine.FailStop = "demo_web";
        var request = new ComposeOperationRequest { Operation = ComposeLifecycleOperation.Stop, Services = ["web"] };

        if (cancelledAfterCommit)
            Assert.True((await fixture.Supervisor.OperateAsync("demo", request, cts.Token)).IsCancelled);
        else
            Assert.False((await fixture.Supervisor.OperateAsync("demo", request)).AllSucceeded);

        Assert.True(fixture.SavedProject!.AppliedServices["web"].ManuallyStopped);
        Assert.True(fixture.Suppression.IsSuppressed("demo_web"));
        Assert.Same(workerPolicy, Assert.Single(fixture.RestartPolicies));
        Assert.Equal(["stop:demo_web"], fixture.Engine.Mutations);
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_worker"]);
        fixture.Engine.Mutations.Clear();
        await fixture.Supervisor.ReconcileAsync();
        Assert.Equal("demo_worker", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadoptionPreservesAppliedInactiveAndOrphanServices(bool orphan)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Profiles = ["tools"];
        fixture.Project.ActiveProfiles = ["tools"];
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        fixture.Project.ActiveProfiles.Clear();
        if (orphan)
            fixture.Project.Services.Clear();
        fixture.RestartPolicies.Clear();
        fixture.Engine.Mutations.Clear();

        await fixture.Supervisor.ReconcileAsync();

        Assert.Empty(fixture.Supervisor.ReconciliationWarnings);
        Assert.Equal("demo_web", Assert.Single(fixture.RestartPolicies).ContainerName);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task TransientReadoptionInspectFailurePreservesExistingPolicyObjects()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        var restart = Assert.Single(fixture.RestartPolicies);
        var health = new HealthCheckConfig { ContainerName = "demo_web", Command = "retained-probe" };
        fixture.HealthChecks.Add(health);
        fixture.Engine.InspectionErrors["demo_web"] = "WSLC_E_ACCESS_DENIED";
        fixture.Engine.Mutations.Clear();

        await fixture.Supervisor.ReconcileAsync();

        Assert.NotEmpty(fixture.Supervisor.ReconciliationWarnings);
        Assert.Same(restart, Assert.Single(fixture.RestartPolicies));
        Assert.Same(health, Assert.Single(fixture.HealthChecks));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadoptionRemovesSupervisionForProvenForeignOrReplacedIdentity(bool replaced)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        fixture.HealthChecks.Add(new() { ContainerName = "demo_web", Command = "old-probe" });
        if (replaced)
            fixture.Engine.ContainerIds["demo_web"] = "replaced-id";
        else
            fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ProjectLabel] = "foreign";
        fixture.Engine.Mutations.Clear();

        await fixture.Supervisor.ReconcileAsync();

        Assert.NotEmpty(fixture.Supervisor.ReconciliationWarnings);
        Assert.Empty(fixture.RestartPolicies);
        Assert.Empty(fixture.HealthChecks);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task LegacyOwnedContainerIsMigratedOnceThenRepeatedUpIsNonDisruptive()
    {
        var fixture = new Fixture();
        fixture.Engine.Add(Options(), allEndpoints: true);
        await UpSuccessfullyAsync(fixture);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ConfigHashLabel]));
        Assert.True(fixture.SavedProject!.AppliedServices.ContainsKey("web"));
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task ContainerInspectionIsCorrelatedByIdRatherThanContainerName()
    {
        var fixture = new Fixture();
        fixture.Engine.Add(Options(), allEndpoints: true);
        fixture.Engine.ContainerIds["demo_web"] = "verified-container-id";

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project,
            new() { Operation = ComposeLifecycleOperation.Restart });

        Assert.True(plan.CanApply);
        Assert.Equal("verified-container-id", Assert.Single(plan.Services).ContainerId);
        Assert.Equal(ComposeServiceAction.Restart, plan.Services[0].Action);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task ContainerInventoryFailureNeverBecomesAnEmptyProjectOrDestroysSupervision()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mutations.Clear();
        fixture.Engine.BeforeList = () => throw new IOException("inventory unavailable");

        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        await Assert.ThrowsAsync<IOException>(() => fixture.Supervisor.PlanAsync(fixture.Project));

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Single(fixture.RestartPolicies);
        Assert.Single(fixture.SavedProject!.AppliedServices);
    }

    [Fact]
    public async Task InspectionFailureProducesAnIncompatibleBlockedPlanWithoutThrowing()
    {
        var fixture = new Fixture();
        AddService(fixture, "worker");
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Engine.InspectionErrors["demo_worker"] = "fixture inspection unavailable";
        fixture.Engine.Mutations.Clear();
        fixture.Engine.Reads.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        var blocked = plan.Services.Single(s => s.Service.Name == "worker");
        Assert.False(plan.CanApply);
        Assert.Equal(ComposeServiceChange.Incompatible, blocked.Change);
        Assert.Equal(ComposeServiceAction.Blocked, blocked.Action);
        Assert.Contains("inspection", blocked.Reason, StringComparison.OrdinalIgnoreCase);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.DoesNotContain("images", fixture.Engine.Reads);
        Assert.Equal(2, fixture.RestartPolicies.Count);
    }

    [Fact]
    public async Task MatchingOldFingerprintDoesNotAuthorizeAForeignContainer()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ProjectLabel] = "foreign";
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Engine.Mutations.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        Assert.False(plan.CanApply);
        Assert.Equal(ComposeServiceAction.Blocked, Assert.Single(plan.Services).Action);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.False(result.AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal("foreign", fixture.Engine.Containers["demo_web"].Labels[ComposeProject.ProjectLabel]);
    }

    [Fact]
    public async Task UnrelatedEndpointSurvivesRepeatedUpAndDeclaredDriftHasAnExplicitRecreationPlan()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Endpoints["demo_web"].Add(new() { Network = "externally-attached", Aliases = ["owner"] });
        fixture.Engine.Mutations.Clear();
        await UpSuccessfullyAsync(fixture);
        Assert.Equal(3, fixture.Engine.Endpoints["demo_web"].Count);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(["owner"], fixture.Engine.Endpoints["demo_web"].Single(n => n.Network == "externally-attached").Aliases);
        fixture.Engine.Endpoints["demo_web"].Single(n => n.Network == "b").Aliases.Clear();

        var plan = await fixture.Supervisor.PlanAsync(fixture.Project);
        Assert.Equal(ComposeServiceAction.Recreate, Assert.Single(plan.Services).Action);
        Assert.Contains("network endpoints", plan.Services[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Engine.Mutations);
        var result = await fixture.Supervisor.UpAsync(fixture.Project);

        Assert.True(result.AllSucceeded);
        Assert.Equal(ComposeServiceAction.Recreate, Assert.Single(result.Services).Action);
        Assert.Contains("remove:demo_web", fixture.Engine.Mutations);
        Assert.Equal(["private", "web", "demo_web"], fixture.Engine.Endpoints["demo_web"].Single(n => n.Network == "b").Aliases);
    }

    [Fact]
    public async Task DeclaredVolumeIsCreatedOnceAndPreservedAcrossRecreationAndDefaultDown()
    {
        var fixture = new Fixture();
        fixture.Project.Volumes = [new() { Name = "data" }];
        fixture.Project.Services[0].Options.Volumes = ["data:/data"];
        await UpSuccessfullyAsync(fixture);
        Assert.Equal("volume-create:data", fixture.Engine.Mutations[0]);
        Assert.Equal("demo", fixture.Engine.Volumes["data"]);
        fixture.Engine.Mutations.Clear();
        await UpSuccessfullyAsync(fixture);
        Assert.Empty(fixture.Engine.Mutations);
        fixture.Project.Services[0].Options.Command = "updated";

        await UpSuccessfullyAsync(fixture);
        await fixture.Supervisor.DownAsync("demo");

        Assert.Contains("data:/data", fixture.SavedSnapshots.First(s => s.AppliedServices.ContainsKey("web"))
            .AppliedServices["web"].Service.Options.Volumes);
        Assert.True(fixture.Engine.Volumes.ContainsKey("data"));
        Assert.DoesNotContain("volume-remove:data", fixture.Engine.Mutations);
        Assert.DoesNotContain("volume-create:data", fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("inspect")]
    [InlineData("external")]
    public async Task VolumePreflightFailurePreservesEveryRunningService(string failure)
    {
        var fixture = new Fixture();
        var worker = AddService(fixture, "worker");
        fixture.Project.Services.ForEach(s => s.Restart = RestartPolicyKind.Always);
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Options.Command = "requires-recreate";
        fixture.Project.Volumes = [new() { Name = "data", External = failure == "external" }];
        worker.Options.Volumes = ["data:/data"];
        if (failure == "create") fixture.Engine.FailCreateVolume = "data";
        if (failure == "inspect") fixture.Engine.VolumeInspectionError = "WSLC_E_ACCESS_DENIED";
        fixture.Engine.Mutations.Clear();

        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);

        Assert.DoesNotContain(fixture.Engine.Mutations, m =>
            m.StartsWith("stop:") || m.StartsWith("remove:") || m.StartsWith("create:") || m.StartsWith("run:"));
        if (failure != "create")
            Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.Equal(2, fixture.RestartPolicies.Count);
        Assert.False(fixture.Engine.Volumes.ContainsKey("data"));
    }

    [Fact]
    public async Task UnreferencedVolumesAreNotInspectedOrCreated()
    {
        var fixture = new Fixture();
        fixture.Project.Volumes = [new() { Name = "external", External = true }, new() { Name = "unused" }];

        await UpSuccessfullyAsync(fixture);

        Assert.DoesNotContain(fixture.Engine.Reads, read => read.StartsWith("volume:", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Engine.Mutations, mutation => mutation.StartsWith("volume-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameNameResourceDeclarationDriftIsRejectedBeforeAnyMutation(bool volume)
    {
        var fixture = new Fixture();
        if (volume)
        {
            fixture.Project.Volumes = [new() { Name = "data", Driver = "local" }];
            fixture.Project.Services[0].Options.Volumes = ["data:/data"];
        }
        else
            fixture.Project.Networks = [new() { Name = "a", Subnet = "172.28.0.0/16" }];
        await UpSuccessfullyAsync(fixture);
        fixture.Project.Services[0].Options.Command = "updated";
        if (volume)
            fixture.Project.Volumes[0].Driver = "different-driver";
        else
            fixture.Project.Networks[0].Subnet = "172.30.0.0/16";
        fixture.Engine.Mutations.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.PlanAsync(fixture.Project));
        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_web"]);
    }

    [Fact]
    public async Task DownWithVolumesRemovesOnlyOwnedNonExternalVolumes()
    {
        var fixture = new Fixture();
        fixture.Project.Volumes =
        [
            new() { Name = "ours" },
            new() { Name = "foreign" },
            new() { Name = "external", External = true },
        ];
        fixture.Engine.Volumes["ours"] = "demo";
        fixture.Engine.Volumes["foreign"] = "another-project";
        fixture.Engine.Volumes["external"] = "demo";
        fixture.Project.Services[0].Options.Volumes = ["ours:/ours", "foreign:/foreign", "external:/external"];

        await fixture.Supervisor.DownAsync("demo", removeVolumes: true);

        Assert.Equal(["volume-remove:ours"], fixture.Engine.Mutations);
        Assert.True(fixture.Engine.Volumes.ContainsKey("foreign"));
        Assert.True(fixture.Engine.Volumes.ContainsKey("external"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AnonymousVolumeIdentityIsPreservedWhileDesiredMountModeIsApplied(bool originalReadOnly, bool desiredReadOnly)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.Volumes = [originalReadOnly ? "/cache:ro" : "/cache"];
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mounts["demo_web"] =
        [
            new { Type = "volume", Name = "opaque-anonymous-volume", Destination = "/cache", RW = !originalReadOnly, IsAnonymous = true },
        ];
        fixture.Project.Services[0].Options.Volumes = [desiredReadOnly ? "/cache:ro" : "/cache"];
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Contains("opaque-anonymous-volume:/cache" + (desiredReadOnly ? ":ro" : ""), fixture.Engine.Containers["demo_web"].Volumes);
        Assert.DoesNotContain("/cache", fixture.Engine.Containers["demo_web"].Volumes);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("volume-remove:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageDeclaredAnonymousVolumeIsPreservedUnlessDesiredMountOverridesItsTarget(bool overridden)
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mounts["demo_web"] =
        [
            new { Type = "volume", Name = "image-anonymous-volume", Destination = "/image-data", ReadOnly = true, IsAnonymous = true },
        ];
        fixture.Project.Services[0].Options.Command = "updated";
        if (overridden)
            fixture.Project.Services[0].Options.Volumes = ["replacement:/image-data"];
        fixture.Engine.Mutations.Clear();

        await UpSuccessfullyAsync(fixture);

        Assert.Equal(overridden ? "replacement:/image-data" : "image-anonymous-volume:/image-data:ro",
            Assert.Single(fixture.Engine.Containers["demo_web"].Volumes));
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("volume-remove:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingMountMetadataBlocksRecreationBeforeStoppingAService()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        fixture.Engine.Mounts["demo_web"] = null;
        fixture.Project.Services[0].Options.Command = "updated";
        fixture.Engine.Mutations.Clear();

        Assert.False((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);

        Assert.Empty(fixture.Engine.Mutations);
        Assert.Equal(ContainerState.Running, fixture.Engine.States["demo_web"]);
    }

    [Fact]
    public async Task PersistingDesiredEditsPreservesAppliedStateButAuthoritativeDownRemovesIt()
    {
        var fixture = new Fixture();
        await UpSuccessfullyAsync(fixture);
        var fingerprint = fixture.SavedProject!.AppliedServices["web"].Fingerprint;
        fixture.Project.Services[0].Options.Command = "pending";
        fixture.PersistDesired();

        Assert.Equal(fingerprint, fixture.SavedProject!.AppliedServices["web"].Fingerprint);
        Assert.Null(fixture.SavedProject.AppliedServices["web"].Service.Options.Command);
        Assert.Equal("pending", fixture.SavedProject.Services[0].Options.Command);
        var result = await fixture.Supervisor.OperateAsync("demo", new() { Operation = ComposeLifecycleOperation.Down });

        Assert.True(result.AllSucceeded);
        Assert.Empty(fixture.SavedProject!.AppliedServices);
    }

    private static ComposeService AddService(Fixture fixture, string name)
    {
        var service = new ComposeService { Name = name, Options = new() { Image = "fixture" } };
        fixture.Project.Services.Add(service);
        return service;
    }

    private static async Task UpSuccessfullyAsync(Fixture fixture)
    {
        var result = await fixture.Supervisor.UpAsync(fixture.Project);
        Assert.True(result.AllSucceeded, string.Join(Environment.NewLine, result.Services.Select(s => $"{s.Service}: {s.Detail}")));
    }
}
