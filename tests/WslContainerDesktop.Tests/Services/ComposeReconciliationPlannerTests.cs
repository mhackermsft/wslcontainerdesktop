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

public sealed class ComposeReconciliationPlannerTests
{
    [Fact]
    public void EngineOwnedBuildPathsRetainCaseSensitiveIdentity()
    {
        Assert.NotEqual(
            ComposeReconciliationPlanner.BuildFingerprint(new() { Context = "/work/app" }),
            ComposeReconciliationPlanner.BuildFingerprint(new() { Context = "/work/App" }));
    }

    [Fact]
    public void NamespaceProviderNameIsPartOfEffectiveConfiguration()
    {
        var provider = Service("db");
        var consumer = Service("sidecar");
        consumer.Options.NetworkMode = "service:db";
        var project = Project(provider, consumer);
        var before = ComposeReconciliationPlanner.Fingerprint(project, consumer);
        provider.Options.Name = "renamed-db";
        Assert.NotEqual(before, ComposeReconciliationPlanner.Fingerprint(project, consumer));
    }

    private static ComposeService Service(string name, params string[] dependencies) => new()
    {
        Name = name, Options = new() { Image = "fixture:1" },
        DependsOn = dependencies.Select(d => new ComposeDependency { ServiceName = d }).ToList(),
    };

    private static ComposeProject Project(params ComposeService[] services) => new()
    {
        Name = "project", Services = services.ToList(),
    };

    private static string[] Selected(ComposeProject project, ComposeOperationRequest? request = null) =>
        ComposeReconciliationPlanner.SelectServices(project, request ?? new()).Select(s => s.Name).ToArray();

    private static ContainerNetworkState Inspect(ComposeProject project, ComposeService service,
        string? fingerprint, string id = "container-id", bool owned = true)
    {
        var labels = new Dictionary<string, string>();
        if (owned)
        {
            labels[ComposeProject.ProjectLabel] = project.Name;
            labels[ComposeProject.ServiceLabel] = service.Name;
        }
        if (fingerprint is not null) labels[ComposeProject.ConfigHashLabel] = fingerprint;
        return ContainerNetworkState.Parse(JsonSerializer.Serialize(new
        {
            Id = id, Config = new { Labels = labels },
            NetworkSettings = new { Networks = new Dictionary<string, object>() },
        }));
    }

    private static ComposeReconciliationPlan Plan(ComposeProject project, ContainerState state,
        string? fingerprint, ComposeOperationRequest? request = null, bool owned = true, string inspectId = "container-id")
    {
        var service = Assert.Single(project.Services);
        return ComposeReconciliationPlanner.Plan(project, request ?? new(),
            [new() { Name = ComposeReconciliationPlanner.ContainerName(project, service), Id = "container-id", StateValue = (int)state }],
            new Dictionary<string, ContainerNetworkState>
            {
                ["container-id"] = Inspect(project, service, fingerprint, inspectId, owned),
            });
    }

    [Fact]
    public void UpSelectsActiveProfilesAndOrdersDependencies()
    {
        var db = Service("db");
        var web = Service("web", "db");
        var debug = Service("debug");
        debug.Profiles = ["debug"];
        var project = Project(web, debug, db);
        Assert.Equal(["db", "web"], Selected(project));
        project.ActiveProfiles = ["debug"];
        Assert.Equal(["debug", "db", "web"], Selected(project));
        project.ActiveProfiles = ["*"];
        Assert.Equal(["debug", "db", "web"], Selected(project));
    }

    [Fact]
    public void IndependentServicesKeepDeclaredOrderAndStopReversesIt()
    {
        var project = Project(Service("web"), Service("other"));
        Assert.Equal(["web", "other"], Selected(project));
        Assert.Equal(["other", "web"], Selected(project, new() { Operation = ComposeLifecycleOperation.Stop }));
        project = Project(Service("web", "db"), Service("other"), Service("db"));
        Assert.Equal(["other", "db", "web"], Selected(project));
        Assert.Equal(["web", "db", "other"], Selected(project, new() { Operation = ComposeLifecycleOperation.Down }));
    }

    [Fact]
    public void ExplicitTargetEnablesItsProfilesButNotUnrelatedSiblings()
    {
        var target = Service("target", "db");
        target.Profiles = ["debug"];
        var db = Service("db");
        db.Profiles = ["debug"];
        var sibling = Service("sibling");
        sibling.Profiles = ["debug"];
        var project = Project(target, db, sibling, Service("unrelated"));
        Assert.Equal(["db", "target"], Selected(project, new() { Services = ["target"] }));
        Assert.Empty(project.ActiveProfiles);
        db.Profiles = ["other"];
        Assert.Throws<InvalidOperationException>(() => Selected(project, new() { Services = ["target"] }));
    }

    [Fact]
    public void OptionalDependenciesAreIncludedOnlyWhenAvailableAndActive()
    {
        var web = Service("web", "db", "missing");
        web.DependsOn.ForEach(d => d.Required = false);
        var db = Service("db");
        db.Profiles = ["db"];
        var project = Project(web, db);
        Assert.Equal(["web"], Selected(project));
        project.ActiveProfiles = ["db"];
        Assert.Equal(["db", "web"], Selected(project));
        web.DependsOn[1].Required = true;
        Assert.Throws<InvalidOperationException>(() => Selected(project));
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public void StopAndDownIncludeInactiveServicesAndReverseOnlySelectedTopology(ComposeLifecycleOperation operation)
    {
        var web = Service("web", "db");
        web.Profiles = ["disabled"];
        var project = Project(Service("db"), web);
        Assert.Equal(["web", "db"], Selected(project, new() { Operation = operation }));
        Assert.Equal(["web"], Selected(project, new() { Operation = operation, Services = ["web"] }));
    }

    [Fact]
    public void RestartIncludesTransitiveOptInDependentsButDoesNotCreateDependencyClosure()
    {
        var api = Service("api", "db", "cache");
        api.DependsOn[0].Restart = true;
        var web = Service("web", "api");
        web.DependsOn[0].Restart = true;
        var project = Project(web, api, Service("cache"), Service("db"), Service("other", "db"));
        Assert.Equal(["db", "api", "web"], Selected(project,
            new() { Operation = ComposeLifecycleOperation.Restart, Services = ["db"] }));
        Assert.Equal(["api", "web"], Selected(project,
            new() { Operation = ComposeLifecycleOperation.Restart, Services = ["api"] }));
    }

    [Fact]
    public void FullRestartIncludesInactiveServicesWithoutApplyingDesiredConfiguration()
    {
        var service = Service("web");
        service.Profiles.Add("inactive");
        service.Secrets.Add(new() { Source = "unavailable", Target = "/secret" });
        var project = Project(service);
        Assert.Equal(["web"], Selected(project, new() { Operation = ComposeLifecycleOperation.Restart }));
        var plan = Plan(project, ContainerState.Running, "old-hash", new() { Operation = ComposeLifecycleOperation.Restart });
        Assert.Equal(ComposeServiceAction.Restart, Assert.Single(plan.Services).Action);
        Assert.Equal("old-hash", plan.Services[0].Fingerprint);
    }

    [Fact]
    public void ChangedContainerNameBlocksUpUntilTheAppliedInstanceIsRemoved()
    {
        var service = Service("web");
        var project = Project(service);
        project.AppliedServices.Add("web", new()
        {
            ContainerId = "old-id", Fingerprint = "old-hash",
            Service = ComposeReconciliationPlanner.DeepCloneService(service),
        });
        service.Options.Name = "new-name";
        var plan = ComposeReconciliationPlanner.Plan(project, new(), [],
            new Dictionary<string, ContainerNetworkState>());
        Assert.False(plan.CanApply);
        var item = Assert.Single(plan.Services);
        Assert.Equal(ComposeServiceChange.Incompatible, item.Change);
        Assert.Equal("old-id", item.ContainerId);
        Assert.Contains("old instance", item.Reason);
        Assert.Contains("down", item.Reason);
    }

    [Fact]
    public void NamespaceSharingIsAnImplicitRequiredDependencyForSavedModels()
    {
        var sidecar = Service("sidecar");
        sidecar.Options.NetworkMode = "service:web";
        var project = Project(sidecar, Service("web"));
        Assert.Equal(["web", "sidecar"], Selected(project, new() { Services = ["sidecar"] }));
        sidecar.DependsOn.Add(new() { ServiceName = "web", Required = false });
        project.Services.RemoveAt(1);
        Assert.Throws<InvalidOperationException>(() => Selected(project));
    }

    [Fact]
    public void InvalidGraphsAndIdentitiesFailBeforePlanning()
    {
        Assert.Throws<InvalidOperationException>(() => Selected(Project(Service("duplicate"), Service("duplicate"))));
        Assert.Throws<InvalidOperationException>(() => Selected(Project(Service("a", "b"), Service("b", "a"))));
        Assert.Throws<InvalidOperationException>(() => Selected(Project(Service("a", "missing"))));
        Assert.Throws<InvalidOperationException>(() => Selected(Project(Service("a")), new() { Services = ["missing"] }));
        var collision = Service("b");
        collision.Options.Name = " project_a ";
        Assert.Throws<InvalidOperationException>(() => Selected(Project(Service("a"), collision)));
    }

    [Fact]
    public void FingerprintCanonicalizesMapsSetsArgvAndLegacyNetworks()
    {
        var original = Service("web");
        original.Options.EnvironmentVariables = ["B=2", "A=outdated", "A=1"];
        original.Options.Labels = new() { ["b"] = "2", ["a"] = "1" };
        original.Options.PortMappings = ["8000:80", "9000:90"];
        original.Options.Volumes = ["one:/one", "two:/two"];
        original.Options.Command = "echo 'hello world' \"\"";
        original.Options.Entrypoint = "'/bin/sh'  -c";
        original.Options.Network = "net";
        original.Options.Aliases = ["z", "a"];
        original.Build = new() { Context = ".", Args = ["B=2", "A=old", "A=1"], Labels = new() { ["b"] = "2", ["a"] = "1" } };
        var other = ComposeReconciliationPlanner.CloneService(original);
        other.Options.EnvironmentVariables = ["A=1", "B=2"];
        other.Options.Labels = new() { ["a"] = "1", ["b"] = "2" };
        other.Options.PortMappings.Reverse();
        other.Options.Volumes.Reverse();
        other.Options.Command = "echo \"hello world\" ''";
        other.Options.Entrypoint = "/bin/sh -c";
        other.Options.Network = null;
        other.Options.Aliases.Clear();
        other.Options.NetworkAttachments = [new() { Network = "net", Aliases = ["a", "z", "a"] }];
        other.Build!.Args = ["A=1", "B=2"];
        other.Build.Labels = new() { ["a"] = "1", ["b"] = "2" };
        var project = Project(original);
        Assert.Equal(ComposeReconciliationPlanner.Fingerprint(project, original),
            ComposeReconciliationPlanner.Fingerprint(project, other));
        Assert.StartsWith("v1:", ComposeReconciliationPlanner.Fingerprint(project, original));
        other.Options.NetworkAttachments.Add(new() { Network = "second" });
        var hash = ComposeReconciliationPlanner.Fingerprint(project, other);
        other.Options.NetworkAttachments.Reverse();
        Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, other));
    }

    [Fact]
    public void BuildFingerprintCanonicalizesArgsAndLabelsWithLastArgumentWinning()
    {
        var first = new ComposeBuildConfig
        {
            Context = ".", Args = ["B=2", "A=old", "A=1"],
            Labels = new() { ["b"] = "2", ["a"] = "1" },
        };
        var second = new ComposeBuildConfig
        {
            Context = Directory.GetCurrentDirectory(), Dockerfile = "Dockerfile",
            Args = ["A=1", "B=2"], Labels = new() { ["a"] = "1", ["b"] = "2" },
        };
        var hash = ComposeReconciliationPlanner.BuildFingerprint(first);
        Assert.Equal(hash, ComposeReconciliationPlanner.BuildFingerprint(second));
        second.Args.Add("A=changed");
        Assert.NotEqual(hash, ComposeReconciliationPlanner.BuildFingerprint(second));
        second.Args = ["A=1", "B=2"];
        second.Pull = true;
        Assert.NotEqual(hash, ComposeReconciliationPlanner.BuildFingerprint(second));
        Assert.NotEqual(hash, ComposeReconciliationPlanner.BuildFingerprint(null));
        Assert.Equal(ComposeReconciliationPlanner.BuildFingerprint(null), ComposeReconciliationPlanner.BuildFingerprint(null));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("command")]
    [InlineData("entrypoint")]
    [InlineData("environment")]
    [InlineData("port")]
    [InlineData("mount")]
    [InlineData("label")]
    [InlineData("cpu")]
    [InlineData("memory")]
    [InlineData("user")]
    [InlineData("working-directory")]
    [InlineData("hostname")]
    [InlineData("domainname")]
    [InlineData("stop-signal")]
    [InlineData("dns")]
    [InlineData("dns-search")]
    [InlineData("dns-options")]
    [InlineData("tmpfs")]
    [InlineData("ulimit")]
    [InlineData("shm-size")]
    [InlineData("gpu")]
    [InlineData("network-mode")]
    [InlineData("native-health")]
    [InlineData("extra-hosts")]
    [InlineData("build")]
    public void EffectiveRuntimeChangesAlterFingerprint(string field)
    {
        var service = Service("web");
        var project = Project(service);
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        var options = service.Options;
        switch (field)
        {
            case "image": options.Image = "fixture:2"; break;
            case "command": options.Command = ""; break;
            case "entrypoint": options.Entrypoint = ""; break;
            case "environment": options.EnvironmentVariables.Add("TOKEN=private"); break;
            case "port": options.PortMappings.Add("8080:80"); break;
            case "mount": options.Volumes.Add("data:/data"); break;
            case "label": options.Labels.Add("label", "value"); break;
            case "cpu": options.CpuLimit = "2"; break;
            case "memory": options.MemoryLimit = "2g"; break;
            case "user": options.User = "1000"; break;
            case "working-directory": options.WorkingDir = "/work"; break;
            case "hostname": options.Hostname = "host"; break;
            case "domainname": options.Domainname = "domain"; break;
            case "stop-signal": options.StopSignal = "SIGUSR1"; break;
            case "dns": options.Dns.Add("1.2.3.4"); break;
            case "dns-search": options.DnsSearch.Add("internal"); break;
            case "dns-options": options.DnsOptions.Add("rotate"); break;
            case "tmpfs": options.Tmpfs.Add("/cache"); break;
            case "ulimit": options.Ulimits.Add("nofile=1024:2048"); break;
            case "shm-size": options.ShmSize = "1g"; break;
            case "gpu": options.AllGpus = true; break;
            case "network-mode": options.NetworkMode = "host"; break;
            case "native-health": options.Health = new() { Test = ["CMD", "true"] }; break;
            case "extra-hosts": service.ExtraHosts.Add("internal:1.2.3.4"); break;
            case "build": service.Build = new() { Context = "." }; break;
        }
        Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
    }

    [Fact]
    public void SupervisionSelectionAndGeneratedLabelsDoNotCauseRuntimeRecreation()
    {
        var service = Service("web");
        var project = Project(service);
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        service.Restart = RestartPolicyKind.Always;
        service.Profiles.Add("debug");
        service.DependsOn.Add(new() { ServiceName = "db", Restart = true, Required = false });
        service.StopGracePeriodSeconds = 90;
        service.PullPolicy = ComposeImagePolicy.Always;
        service.Health = new() { Command = "probe", MaxRestarts = 10 };
        service.Options.Labels[ComposeProject.ConfigHashLabel] = "old";
        service.Options.Labels[ComposeProject.ProjectLabel] = "generated";
        service.Options.Labels[ComposeProject.ServiceLabel] = "generated";
        service.Options.Labels[ComposeProject.ImageIdLabel] = "generated";
        service.Options.Labels["com.wsldesktop.apply-operation"] = "generated";
        service.Options.Labels["com.wsldesktop.network-operation"] = "generated";
        service.Options.Detached = false;
        service.Options.Interactive = true;
        service.Options.RemoveOnExit = true;
        Assert.Equal(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
    }

    [Theory]
    [InlineData("fixture", "fixture:latest")]
    [InlineData("registry:5000/team/fixture", "registry:5000/team/fixture:latest")]
    [InlineData("fixture:other", "fixture:other")]
    [InlineData("fixture@sha256:abcdef", "fixture@sha256:abcdef")]
    public void ImageFingerprintNormalizesDefaultLatestWithoutChangingTagsOrDigests(string image, string effective)
    {
        var service = Service("web");
        service.Options.Image = image;
        var project = Project(service);
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        service.Options.Image = effective;
        Assert.Equal(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
    }

    [Fact]
    public void ImageLessBuildUsesTheEffectiveProjectServiceTag()
    {
        var service = Service("web");
        service.Build = new() { Context = "." };
        service.Options.Image = "";
        service.Options.Name = "explicit-container-name";
        var project = Project(service);
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        service.Options.Image = "project_web";
        Assert.Equal(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
    }

    [Fact]
    public void ReferencedResourceDeclarationsAffectFingerprintButUnrelatedDeclarationsDoNot()
    {
        var service = Service("web");
        service.Options.Network = "net";
        service.Options.Volumes = ["data:/data"];
        var project = Project(service);
        project.Networks = [new() { Name = "net", Driver = "bridge" }];
        project.Volumes = [new() { Name = "data" }];
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        project.Networks.Add(new() { Name = "other", Subnet = "10.0.0.0/24" });
        project.Volumes.Add(new() { Name = "other" });
        Assert.Equal(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
        project.Networks[0].DriverOpts.Add("option=value");
        Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
        hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        project.Volumes[0].Labels.Add("label", "value");
        Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
    }

    [Theory]
    [InlineData(ContainerState.Running, ComposeServiceAction.Keep)]
    [InlineData(ContainerState.Stopped, ComposeServiceAction.Start)]
    [InlineData(ContainerState.Created, ComposeServiceAction.Start)]
    public void OwnedMatchingContainerIsReused(ContainerState state, ComposeServiceAction action)
    {
        var service = Service("web");
        var project = Project(service);
        var plan = Plan(project, state, ComposeReconciliationPlanner.Fingerprint(project, service));
        Assert.True(plan.CanApply);
        Assert.Equal(action, Assert.Single(plan.Services).Action);
        Assert.Equal(ComposeServiceChange.Unchanged, plan.Services[0].Change);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v1:old")]
    public void LegacyAndChangedOwnedContainersAreRecreated(string? hash)
    {
        var service = Service("web");
        service.Options.EnvironmentVariables = ["PASSWORD=synthetic-private"];
        var item = Assert.Single(Plan(Project(service), ContainerState.Running, hash).Services);
        Assert.Equal(ComposeServiceAction.Recreate, item.Action);
        Assert.Equal(ComposeServiceChange.Changed, item.Change);
        Assert.DoesNotContain("synthetic-private", item.Reason);
        if (hash is null) Assert.Contains("migration", item.Reason);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Up, ComposeServiceAction.Create)]
    [InlineData(ComposeLifecycleOperation.Restart, ComposeServiceAction.Keep)]
    [InlineData(ComposeLifecycleOperation.Stop, ComposeServiceAction.Keep)]
    [InlineData(ComposeLifecycleOperation.Down, ComposeServiceAction.Keep)]
    public void MissingContainerIsCreatedOnlyForUp(ComposeLifecycleOperation operation, ComposeServiceAction action)
    {
        var plan = ComposeReconciliationPlanner.Plan(Project(Service("web")), new() { Operation = operation },
            [], new Dictionary<string, ContainerNetworkState>());
        Assert.Equal(action, Assert.Single(plan.Services).Action);
        Assert.Equal(ComposeServiceChange.Missing, plan.Services[0].Change);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Restart, ComposeServiceAction.Restart)]
    [InlineData(ComposeLifecycleOperation.Stop, ComposeServiceAction.Stop)]
    [InlineData(ComposeLifecycleOperation.Down, ComposeServiceAction.Remove)]
    public void LifecycleOperationsDoNotApplyConfigOrReadUnavailableDesiredFiles(
        ComposeLifecycleOperation operation, ComposeServiceAction action)
    {
        var service = Service("web");
        service.Secrets.Add(new() { Source = "unavailable", Target = "/secret" });
        var plan = Plan(Project(service), ContainerState.Running, "applied-hash", new() { Operation = operation });
        Assert.Equal(action, Assert.Single(plan.Services).Action);
        Assert.Equal("applied-hash", plan.Services[0].Fingerprint);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Up)]
    [InlineData(ComposeLifecycleOperation.Restart)]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public void ForeignOrStaleInspectNeverAllowsMutation(ComposeLifecycleOperation operation)
    {
        var project = Project(Service("web"));
        var request = new ComposeOperationRequest { Operation = operation };
        Assert.False(Plan(project, ContainerState.Running, "hash", request, owned: false).CanApply);
        Assert.False(Plan(project, ContainerState.Running, "hash", request, inspectId: "different-id").CanApply);
    }

    [Theory]
    [InlineData(ContainerState.Unknown)]
    [InlineData(ContainerState.Paused)]
    public void UnsupportedStatesAreBlocked(ContainerState state)
    {
        var plan = Plan(Project(Service("web")), state, null);
        Assert.False(plan.CanApply);
        Assert.Equal(ComposeServiceAction.Blocked, Assert.Single(plan.Services).Action);
    }

    [Fact]
    public void UnknownInspectAndDuplicateInventoryAreBlocked()
    {
        var project = Project(Service("web"));
        var container = new ContainerInfo { Name = "project_web", Id = "id", StateValue = 2 };
        Assert.False(ComposeReconciliationPlanner.Plan(project, new(), [container],
            new Dictionary<string, ContainerNetworkState>()).CanApply);
        Assert.False(ComposeReconciliationPlanner.Plan(project, new(), [container, container],
            new Dictionary<string, ContainerNetworkState>()).CanApply);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Up)]
    [InlineData(ComposeLifecycleOperation.Restart)]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public void UniqueShortListIdCorrelatesToCanonicalInspectedId(ComposeLifecycleOperation operation)
    {
        var service = Service("web");
        var project = Project(service);
        var fullId = "123456789abc".PadRight(64, 'd');
        var shortId = fullId[..12];
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        var plan = ComposeReconciliationPlanner.Plan(project, new() { Operation = operation },
            [new() { Name = "/project_web", Id = shortId, StateValue = 2 }],
            new Dictionary<string, ContainerNetworkState> { [shortId] = Inspect(project, service, hash, fullId) });
        Assert.True(plan.CanApply);
        Assert.Equal(fullId, Assert.Single(plan.Services).ContainerId);
    }

    [Theory]
    [InlineData("123456789abc", "123456789abcdddd")]
    [InlineData("123456789abc", "123456789abc")]
    [InlineData("123456789ab", "unrelated")]
    public void AmbiguousOrTooShortListIdsCannotAuthorizeMutation(string firstId, string otherId)
    {
        var service = Service("web");
        var project = Project(service);
        var fullId = "123456789abc".PadRight(64, 'd');
        var plan = ComposeReconciliationPlanner.Plan(project, new(),
            [
                new() { Name = "project_web", Id = firstId, StateValue = 2 },
                new() { Name = "unrelated", Id = otherId, StateValue = 2 },
            ],
            new Dictionary<string, ContainerNetworkState> { [firstId] = Inspect(project, service, null, fullId) });
        Assert.False(plan.CanApply);
        Assert.Equal(ComposeServiceAction.Blocked, Assert.Single(plan.Services).Action);
    }

    [Fact]
    public void ForceRecreateAndExplicitBuildOverrideMatchingConfiguration()
    {
        var service = Service("web");
        service.Build = new() { Context = "." };
        var project = Project(service);
        var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
        Assert.Equal(ComposeServiceAction.Recreate,
            Assert.Single(Plan(project, ContainerState.Running, hash, new() { ForceRecreate = true }).Services).Action);
        Assert.Equal(ComposeServiceAction.Recreate,
            Assert.Single(Plan(project, ContainerState.Running, hash, new() { Build = true }).Services).Action);
    }

    [Fact]
    public void PlansAndAppliedServiceSnapshotsAreIndependentOfDesiredEdits()
    {
        var service = Service("web");
        service.Options.EnvironmentVariables = ["A=original"];
        service.Build = new() { Context = ".", Args = ["A=original"] };
        var project = Project(service);
        var item = Assert.Single(ComposeReconciliationPlanner.Plan(project, new(), [],
            new Dictionary<string, ContainerNetworkState>()).Services);
        project.AppliedServices["web"] = new()
        {
            ContainerId = "id", Fingerprint = item.Fingerprint, Service = item.Service, ImageId = "image-id",
        };
        service.Options.EnvironmentVariables[0] = "A=edited";
        service.Build.Args[0] = "A=edited";
        Assert.Equal("A=original", Assert.Single(item.Service.Options.EnvironmentVariables));
        Assert.Equal("A=original", Assert.Single(item.Service.Build!.Args));
        var restored = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(project))!;
        Assert.Equal(item.Fingerprint, restored.AppliedServices["web"].Fingerprint);
        Assert.Equal("image-id", restored.AppliedServices["web"].ImageId);
        Assert.Equal("A=original", Assert.Single(restored.AppliedServices["web"].Service.Options.EnvironmentVariables));
        Assert.Empty(JsonSerializer.Deserialize<ComposeProject>("{}")!.AppliedServices);
    }

    [Fact]
    public void ExplicitEmptyAppliedSnapshotAndPlanInitializersRetainTheirMeaning()
    {
        Assert.False(JsonSerializer.Deserialize<ComposeProject>("{}")!.AppliedStateKnown);
        var project = Project(Service("web"));
        project.AppliedStateKnown = true;
        var restored = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(project))!;
        Assert.True(restored.AppliedStateKnown);
        Assert.Empty(restored.AppliedServices);
        var entry = new ComposeServicePlan(Service("web"), "project_web", "hash",
            ComposeServiceChange.Unchanged, ComposeServiceAction.Keep, "unchanged", "id");
        Assert.Equal(ComposeImageAction.None, entry.ImageAction);
        var blocked = entry with
        {
            Action = ComposeServiceAction.Blocked, Change = ComposeServiceChange.Incompatible,
            Reason = "blocked", ImageId = "image-id", ImageAction = ComposeImageAction.Pull,
        };
        var plan = new ComposeReconciliationPlan { Services = [blocked] };
        Assert.False(plan.CanApply);
        Assert.Equal("image-id", Assert.Single(plan.Services).ImageId);
        Assert.Equal(ComposeImageAction.Pull, plan.Services[0].ImageAction);
    }

    [Fact]
    public void AppliedResourceSnapshotsRoundTripAndDefaultEmptyForOlderState()
    {
        var old = JsonSerializer.Deserialize<ComposeAppliedService>("{}")!;
        Assert.Empty(old.Networks);
        Assert.Empty(old.Volumes);
        Assert.False(old.ManuallyStopped);
        var applied = new ComposeAppliedService
        {
            ManuallyStopped = true,
            Networks = [new() { Name = "network", DriverOpts = ["option=value"], Labels = new() { ["label"] = "value" } }],
            Volumes = [new() { Name = "volume", DriverOpts = ["type=none"], Labels = new() { ["label"] = "value" } }],
        };
        var restored = JsonSerializer.Deserialize<ComposeAppliedService>(JsonSerializer.Serialize(applied))!;
        Assert.True(restored.ManuallyStopped);
        Assert.Equal("option=value", Assert.Single(Assert.Single(restored.Networks).DriverOpts));
        Assert.Equal("type=none", Assert.Single(Assert.Single(restored.Volumes).DriverOpts));
        Assert.Equal("value", restored.Networks[0].Labels["label"]);
        Assert.Equal("value", restored.Volumes[0].Labels["label"]);
    }

    [Fact]
    public void FileSourcesAreHashedWithoutCachingAndUnreadableResourcesBlockPreflight()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "planner-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "synthetic-private-source");
            File.WriteAllText(path, "first-private-value");
            var service = Service("web");
            service.Secrets.Add(new() { Source = "secret", Target = "/run/secrets/secret" });
            var project = Project(service);
            project.Secrets.Add(new() { Name = "secret", File = path });
            var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
            File.WriteAllText(path, "second-private-value");
            Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
            File.Delete(path);
            var error = Assert.Throws<InvalidOperationException>(() => ComposeReconciliationPlanner.Fingerprint(project, service));
            Assert.DoesNotContain("synthetic-private", error.ToString());
            Assert.Null(error.InnerException);
            var plan = ComposeReconciliationPlanner.Plan(project, new(), [], new Dictionary<string, ContainerNetworkState>());
            Assert.False(plan.CanApply);
            Assert.DoesNotContain("private", Assert.Single(plan.Services).Reason);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BuildAndBindContentsRequireExplicitRebuildButConfigIdentityAndContentsAreTracked()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "planner-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config");
            File.WriteAllText(path, "original");
            var service = Service("web");
            service.Build = new() { Context = directory };
            service.Options.Volumes = [path + ":/bind"];
            var project = Project(service);
            var hash = ComposeReconciliationPlanner.Fingerprint(project, service);
            File.WriteAllText(path, "changed");
            Assert.Equal(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
            service.Configs.Add(new() { Source = "config", Target = "/config" });
            project.Configs.Add(new() { Name = "config", File = path });
            hash = ComposeReconciliationPlanner.Fingerprint(project, service);
            File.WriteAllText(path, "changed again");
            Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
            hash = ComposeReconciliationPlanner.Fingerprint(project, service);
            var otherPath = Path.Combine(directory, "other-config");
            File.Copy(path, otherPath);
            project.Configs[0].File = otherPath;
            Assert.NotEqual(hash, ComposeReconciliationPlanner.Fingerprint(project, service));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("missing", ComposeImagePolicy.Missing)]
    [InlineData("if_not_present", ComposeImagePolicy.Missing)]
    [InlineData("always", ComposeImagePolicy.Always)]
    [InlineData("never", ComposeImagePolicy.Never)]
    [InlineData("build", ComposeImagePolicy.Build)]
    public void ParserRetainsExplicitImagePolicyWithoutChangingBaseImagePull(string text, ComposeImagePolicy policy)
    {
        var service = Assert.Single(ComposeImporter.ParseProject(
            $"services: {{web: {{image: fixture, build: ., pull_policy: {text}}}}}").Services);
        Assert.Equal(policy, service.PullPolicy);
        Assert.False(service.Build!.Pull);
    }

    [Fact]
    public void ParserRetainsDependencyRequiredAndRestartAndDefaults()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              web:
                image: fixture
                depends_on:
                  db: {condition: service_healthy, required: false, restart: true}
                  other: {condition: service_started}
              db: {image: fixture}
              other: {image: fixture}
            """);
        var dependencies = project.Services.Single(s => s.Name == "web").DependsOn;
        Assert.False(dependencies[0].Required);
        Assert.True(dependencies[0].Restart);
        Assert.Equal(DependencyCondition.ServiceHealthy, dependencies[0].Condition);
        Assert.True(dependencies[1].Required);
        Assert.False(dependencies[1].Restart);
        Assert.Equal(ComposeImagePolicy.Missing, project.Services[0].PullPolicy);
    }

    [Fact]
    public void ParserNamespaceSharingCannotBeMadeOptional()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              web: {image: fixture}
              sidecar:
                image: fixture
                network_mode: service:web
                depends_on:
                  web: {required: false, restart: true}
            """);
        var dependency = Assert.Single(project.Services.Single(s => s.Name == "sidecar").DependsOn);
        Assert.True(dependency.Required);
        Assert.True(dependency.Restart);
    }

    [Theory]
    [InlineData("pull_policy: hourly")]
    [InlineData("pull_policy: every_5m")]
    [InlineData("pull_policy: synthetic-private")]
    [InlineData("pull_policy: build")]
    [InlineData("pull_policy: []")]
    [InlineData("depends_on: {db: {required: synthetic-private}}")]
    [InlineData("depends_on: {db: {restart: 1}}")]
    [InlineData("depends_on: {db: {restart: []}}")]
    [InlineData("depends_on: {db: {condition: synthetic-private}}")]
    [InlineData("depends_on: {db: {unsupported: true}}")]
    [InlineData("depends_on: {db: true}")]
    public void UnsupportedPoliciesAndDependencyValuesAreRejectedWithoutEchoingValues(string definition)
    {
        var error = Assert.Throws<ComposeConfigurationException>(() =>
            ComposeImporter.ParseProject($"services:\n  web:\n    image: fixture\n    {definition}"));
        Assert.DoesNotContain("synthetic-private", error.ToString());
        Assert.Null(error.InnerException);
    }
}
