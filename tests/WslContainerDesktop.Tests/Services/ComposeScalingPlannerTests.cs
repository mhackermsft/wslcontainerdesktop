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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeScalingPlannerTests
{
    private static ComposeProject Project(int count = 1) => new()
    {
        Name = "demo", Services = [new() { Name = "web", Replicas = count, Options = new() { Image = "fixture" } }],
    };

    private static ComposeReconciliationPlan Plan(ComposeProject project, ComposeOperationRequest? request = null) =>
        ComposeReconciliationPlanner.Plan(project, request ?? new(), [], new Dictionary<string, ContainerNetworkState>());

    [Fact]
    public void StatusObservationDoesNotExpandHugeDesiredCountsOrCountNoncanonicalNames()
    {
        var project = Project(int.MaxValue);
        var containers = new List<ContainerInfo>
        {
            new() { Id = "one", Name = "/demo_web", StateValue = (int)ContainerState.Running },
            new() { Id = "two", Name = "demo_web_2", StateValue = (int)ContainerState.Running },
            new() { Id = "leading-zero", Name = "demo_web_02", StateValue = (int)ContainerState.Running },
            new() { Id = "suffix", Name = "demo_web_backup", StateValue = (int)ContainerState.Running },
            new() { Id = "stopped", Name = "demo_web_3", StateValue = (int)ContainerState.Stopped },
        };
        Assert.Equal(2, ComposeReconciliationPlanner.ObservedRunningCount(project, containers));
        Assert.Null(ComposeReconciliationPlanner.ObservedInstanceIndex(project, project.Services[0], "demo_web_02"));
        Assert.Equal(int.MaxValue, ComposeReconciliationPlanner.ObservedInstanceIndex(
            project, project.Services[0], "demo_web_2147483647"));
    }

    [Fact]
    public void ReplicaPrecedenceAndStableInstanceNamesAreIndependentOfFingerprint()
    {
        var project = Project(2);
        var original = ComposeReconciliationPlanner.Fingerprint(project, project.Services[0]);
        Assert.Equal(["web", "web#2"], Plan(project).Services.Select(p => p.InstanceKey));
        project.ReplicaOverrides["web"] = 3;
        Assert.Equal(3, Plan(project).Services.Count);
        var plan = Plan(project, new() { Replicas = new Dictionary<string, int> { ["web"] = 4 } });
        Assert.Equal(["demo_web", "demo_web_2", "demo_web_3", "demo_web_4"], plan.Services.Select(p => p.ContainerName));
        Assert.All(plan.Services, p => Assert.Equal(original, p.Fingerprint));
        Assert.All(plan.Services, p => Assert.Equal(4, p.DesiredReplicas));
        Assert.Equal(2, project.Services[0].Replicas);
        Assert.False(project.Services[0].Options.Labels.ContainsKey(ComposeProject.InstanceLabel));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2147483647")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-an-index")]
    public void ImportedInstanceLabelCannotChooseOrRedirectIdentity(string importedIndex)
    {
        var project = Project(2);
        var fingerprint = ComposeReconciliationPlanner.Fingerprint(project, project.Services[0]);
        project.Services[0].Options.Labels[ComposeProject.InstanceLabel] = importedIndex;
        Assert.Equal(1, ComposeReconciliationPlanner.InstanceIndex(project.Services[0]));
        Assert.Equal("demo_web", ComposeReconciliationPlanner.ContainerName(project, project.Services[0]));

        var plan = Plan(project);

        Assert.Equal(["demo_web", "demo_web_2"], plan.Services.Select(p => p.ContainerName));
        Assert.All(plan.Services, p =>
        {
            Assert.False(p.Service.Options.Labels.ContainsKey(ComposeProject.InstanceLabel));
            Assert.Equal(fingerprint, p.Fingerprint);
            p.Service.Options.Labels[ComposeProject.InstanceLabel] = "500";
            Assert.Equal(p.InstanceIndex, ComposeReconciliationPlanner.InstanceIndex(p.Service));
            Assert.Equal(p.ContainerName, ComposeReconciliationPlanner.ContainerName(project, p.Service));
        });
    }

    [Fact]
    public void TrustedRuntimeOrdinalCannotBeInjectedThroughServiceJson()
    {
        var service = JsonSerializer.Deserialize<ComposeService>(
            """{"Name":"web","RuntimeInstanceIndex":42,"Options":{"Labels":{"com.wsldesktop.instance":"42"}}}""")!;
        Assert.Equal(1, ComposeReconciliationPlanner.InstanceIndex(service));
        var second = ComposeReconciliationPlanner.ForInstance(service, 2);
        Assert.Equal(2, ComposeReconciliationPlanner.InstanceIndex(second));
        Assert.DoesNotContain("RuntimeInstanceIndex", JsonSerializer.Serialize(second));
        Assert.False(second.Options.Labels.ContainsKey(ComposeProject.InstanceLabel));
        Assert.Throws<ArgumentOutOfRangeException>(() => ComposeReconciliationPlanner.ForInstance(service, 0));
    }

    [Fact]
    public void ZeroReplicasCreatesNothingAndRuntimeZeroOverridesSavedCount()
    {
        var project = Project(3);
        project.ReplicaOverrides["web"] = 4;
        Assert.Empty(Plan(project, new() { Replicas = new Dictionary<string, int> { ["web"] = 0 } }).Services);
    }

    [Theory]
    [InlineData(ComposeReconciliationPlanner.MaximumPlanInstances + 1)]
    [InlineData(int.MaxValue)]
    public void OversizedInt32CountsAreRejectedBeforeExpansion(int count)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Plan(Project(count)));
        Assert.Contains("at most 1024", error.Message);
    }

    [Fact]
    public void ExactPlanBudgetIsAcceptedAndAccumulatedAcrossSelectedServices()
    {
        var project = Project(ComposeReconciliationPlanner.MaximumPlanInstances);
        Assert.Equal(ComposeReconciliationPlanner.MaximumPlanInstances, Plan(project).Services.Count);
        project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        Assert.Throws<InvalidOperationException>(() => Plan(project));
        Assert.Single(Plan(project, new() { Services = ["other"] }).Services);
    }

    [Fact]
    public void ExistingSurplusInstancesCountTowardsPlanBudgetWithoutExpandingOrdinalRanges()
    {
        var project = Project(ComposeReconciliationPlanner.MaximumPlanInstances);
        project.AppliedServices["web#2147483647"] = new()
        {
            InstanceIndex = int.MaxValue, Service = project.Services[0], ContainerId = "surplus",
        };
        Assert.Throws<InvalidOperationException>(() => Plan(project));
        project.Services[0].Replicas = 0;
        Assert.Equal(int.MaxValue, Assert.Single(Plan(project).Services).InstanceIndex);
    }

    [Theory]
    [InlineData("explicit")]
    [InlineData("port")]
    [InlineData("host")]
    [InlineData("none")]
    [InlineData("container:other")]
    [InlineData("ip")]
    public void IncompatibleReplicaConfigurationBlocksAllInstances(string conflict)
    {
        var project = Project(2);
        var options = project.Services[0].Options;
        switch (conflict)
        {
            case "explicit": options.Name = "custom"; break;
            case "port": options.PortMappings = ["127.0.0.1:8080:80"]; break;
            case "ip": options.NetworkAttachments = [new() { Network = "a", Ipv4Address = "10.0.0.5" }]; break;
            default: options.NetworkMode = conflict; break;
        }
        var plan = Plan(project);
        Assert.False(plan.CanApply);
        Assert.All(plan.Services, p => Assert.Equal(ComposeServiceAction.Blocked, p.Action));
    }

    [Fact]
    public void SingleExplicitNameAndLegacyFirstInstanceContractsRemainStable()
    {
        var project = Project();
        project.Services[0].Options.Name = "custom";
        Assert.Equal("custom", Assert.Single(Plan(project).Services).ContainerName);
        var saved = JsonSerializer.Deserialize<ComposeAppliedService>("""{"Service":{"Name":"web"}}""")!;
        Assert.Equal(1, saved.InstanceIndex);
        Assert.Equal("web", saved.InstanceKey);
        saved.InstanceIndex = 3;
        Assert.Equal("web#3", saved.InstanceKey);
        var json = JsonSerializer.Serialize(new Dictionary<string, ComposeAppliedService> { [saved.InstanceKey] = saved });
        Assert.DoesNotContain("\"InstanceKey\"", json);
        var restored = JsonSerializer.Deserialize<Dictionary<string, ComposeAppliedService>>(json)!;
        Assert.Equal(3, restored["web#3"].InstanceIndex);
        Assert.Equal("web#3", restored["web#3"].InstanceKey);
    }

    [Fact]
    public void InstanceOwnershipRequiresOrdinalExceptForLegacyFirst()
    {
        var project = Project(2);
        var state = ContainerNetworkState.Parse(JsonSerializer.Serialize(new
        {
            Id = "id", Config = new { Labels = new Dictionary<string, string>
            {
                [ComposeProject.ProjectLabel] = "demo", [ComposeProject.ServiceLabel] = "web",
            } },
            NetworkSettings = new { Networks = new Dictionary<string, object>() },
        }));
        Assert.True(ComposeReconciliationPlanner.IsOwnedInstance(state, project, project.Services[0]));
        Assert.False(ComposeReconciliationPlanner.IsOwnedInstance(state, project,
            ComposeReconciliationPlanner.ForInstance(project.Services[0], 2)));
    }

    [Fact]
    public void ScaleDownUsesAppliedCountEvenWhenDesiredCountDecreases()
    {
        var project = Project();
        var previous = new ComposeService { Name = "web", Replicas = 3, Options = new() { Image = "fixture" } };
        project.AppliedServices["web#3"] = new() { Service = previous, InstanceIndex = 3, ContainerId = "third" };
        var expansion = ComposeReconciliationPlanner.ExpandInstances(project, new(), project.Services, []);
        Assert.Equal([1, 3], expansion.Select(ComposeReconciliationPlanner.InstanceIndex));
        var plan = Plan(project);
        Assert.Equal(ComposeServiceAction.Keep, plan.Services.Single(p => p.InstanceIndex == 3).Action);
    }

    [Fact]
    public void GeneratedNamesCannotCollideWithAnotherSelectedService()
    {
        var project = Project(2);
        project.Services.Add(new() { Name = "web_2", Options = new() { Image = "fixture" } });
        var plan = Plan(project);
        Assert.False(plan.CanApply);
        Assert.All(plan.Services.Where(p => p.ContainerName == "demo_web_2"),
            p => Assert.Equal(ComposeServiceAction.Blocked, p.Action));
    }

    [Fact]
    public void OverridesCannotSilentlyEscapeSelectedServiceBoundary()
    {
        var project = Project();
        project.Services.Add(new() { Name = "other" });
        Assert.Throws<InvalidOperationException>(() => Plan(project, new()
        {
            Services = ["web"], Replicas = new Dictionary<string, int> { ["other"] = 2 },
        }));
        Assert.Throws<InvalidOperationException>(() => Plan(project, new()
        {
            Replicas = new Dictionary<string, int> { ["missing"] = 2 },
        }));
        Assert.Throws<InvalidOperationException>(() => Plan(project, new()
        {
            Replicas = new Dictionary<string, int> { ["web"] = -1 },
        }));
    }

    [Fact]
    public void SharedAndAnonymousMountDeclarationsAreNotRewrittenAcrossInstances()
    {
        var project = Project(3);
        project.Services[0].Options.Volumes = ["data:/shared", "/host:/bind", "/anonymous"];
        var plan = Plan(project);
        Assert.True(plan.CanApply);
        Assert.All(plan.Services, p => Assert.Equal(project.Services[0].Options.Volumes, p.Service.Options.Volumes));
        Assert.All(plan.Services, p => Assert.Contains("shared", p.StorageWarning));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void NamespaceProviderCountConflictsEvenWhenConsumerIsNotSelected(int count)
    {
        var project = Project(count);
        project.Services.Add(new() { Name = "sidecar", Options = new() { Image = "fixture", NetworkMode = "service:web" } });
        var state = ContainerNetworkState.Parse(JsonSerializer.Serialize(new
        {
            Id = "id", Config = new { Labels = new Dictionary<string, string>
            {
                [ComposeProject.ProjectLabel] = "demo", [ComposeProject.ServiceLabel] = "web",
            } },
            NetworkSettings = new { Networks = new Dictionary<string, object>() },
        }));
        var plan = ComposeReconciliationPlanner.Plan(project, new() { Services = ["web"] },
            [new() { Id = "id", Name = "demo_web", StateValue = (int)ContainerState.Running }],
            new Dictionary<string, ContainerNetworkState> { ["id"] = state });
        Assert.False(plan.CanApply);
        Assert.All(plan.Services, p => Assert.Contains("namespace provider", p.Reason));
    }

    [Fact]
    public void RequiredZeroReplicaDependencyBlocksStartupDuringPreflight()
    {
        var project = Project(0);
        project.Services.Add(new()
        {
            Name = "consumer", Options = new() { Image = "fixture" },
            DependsOn = [new() { ServiceName = "web" }],
        });
        Assert.False(Plan(project).CanApply);
        Assert.Contains("zero", Assert.Single(Plan(project).Services).Reason);
    }
}
