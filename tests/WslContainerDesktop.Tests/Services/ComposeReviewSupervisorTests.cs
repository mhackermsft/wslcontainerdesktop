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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;
using static WslContainerDesktop.Tests.Services.ComposeNetworkSupervisorTests;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Covers compose review supervision so previewed plans, tokens, validation, conflict detection, cancellation, and outcomes stay trustworthy.</summary>
public sealed class ComposeReviewSupervisorTests
{
    [Fact]
    public async Task PreviewMatchesNativeBaselineBackendAndNeverMutates()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        var before = JsonSerializer.Serialize(fixture.Project);
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Project));
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
        Assert.Empty(fixture.HealthChecks);
        Assert.Empty(fixture.RestartPolicies);
        var backendRow = Row(review, "backend");
        Assert.Equal("NativeCreateConnectStart", backendRow.EffectiveValue);
        Assert.Equal(ComposeSettingDisposition.Supported, backendRow.Disposition);
        Assert.Equal(WslcCapabilitySupport.Supported, backendRow.Capability);
        Assert.Contains("application", Row(review, "restart").EffectiveValue);
        Assert.True(review.Preview.CanApply);
        Assert.Equal(1, fixture.CapabilityInvalidations);

        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true);
        Assert.Equal(ComposeReviewOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(2, fixture.CapabilityInvalidations);
        Assert.Contains("create:demo_web", fixture.Engine.Mutations);
        Assert.Equal(2, fixture.SavedProject!.Services[0].Options.GetNetworkAttachments().Count);
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported, ComposePolicyOwner.Engine)]
    [InlineData(WslcCapabilitySupport.Unsupported, ComposePolicyOwner.Application)]
    [InlineData(WslcCapabilitySupport.Unknown, ComposePolicyOwner.Application)]
    public async Task NativeHealthFallbackAndUnknownHaveDistinctOwners(WslcCapabilitySupport support, ComposePolicyOwner owner)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.Health = new() { Test = ["CMD-SHELL", "true"], StartInterval = "2s" };
        fixture.Snapshot = new("wslc.exe", "fixture", Enum.GetValues<WslcFeature>().ToDictionary(f => f,
            f => new WslcCapability(f == WslcFeature.CreateHealthStartInterval
                ? support : WslcCapabilitySupport.Supported, "health-start-interval unavailable")));
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Contains($"Probe owner: {owner}", Row(review, "healthcheck").EffectiveValue);
        Assert.True(review.Preview.CanApply);
        if (support == WslcCapabilitySupport.Unknown)
            Assert.Contains("health-start-interval unavailable", JsonSerializer.Serialize(review));
        else
            Assert.DoesNotContain("health-start-interval unavailable", JsonSerializer.Serialize(review));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task DecliningReviewConsumesOnceWithoutPersistingOverridesOrSupervision()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options = new() { Image = "fixture" };
        var request = new ComposeOperationRequest { Replicas = new Dictionary<string, int> { ["web"] = 2 } };
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project, request);
        Assert.True(review.Preview.CanApply);

        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, false, saveReplicaOverrides: true);
        Assert.Equal(ComposeReviewOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(ComposeReviewOutcomeKind.AlreadyUsed, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Empty(fixture.Project.ReplicaOverrides);
        Assert.Empty(fixture.SavedSnapshots);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.RestartPolicies);
    }

    [Fact]
    public async Task MissingPresenterAndDeclinedPresenterFailClosed()
    {
        foreach (var presenter in new[] { false, true })
        {
            var fixture = new Fixture(presenterAvailable: presenter) { ConfirmReviewAsync = _ => Task.FromResult(false) };
            var result = await fixture.Supervisor.UpAsync(fixture.Project);
            Assert.True(result.IsCancelled);
            Assert.False(result.AllSucceeded);
            Assert.Empty(fixture.Engine.Mutations);
            Assert.Empty(fixture.SavedSnapshots);
        }
    }

    [Fact]
    public async Task AdvisoryValidationIsFreshNonConsumingAndApplyChecksAgain()
    {
        var fixture = new Fixture();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Equal(ComposePlanValidation.Valid, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Equal(2, fixture.CapabilityInvalidations);
        Assert.Empty(fixture.Engine.Mutations);
        fixture.Project.Services[0].Options.NetworkAttachments[1].Ipv4Address = "changed-after-review";

        Assert.Equal(ComposeReviewOutcomeKind.Stale, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Equal(3, fixture.CapabilityInvalidations);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("image")]
    [InlineData("settings")]
    [InlineData("resource")]
    public async Task DriftRequiresNewReviewBeforeAnyMutation(string drift)
    {
        var fixture = new Fixture();
        fixture.Project.Networks = [new() { Name = "a" }];
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.True(review.Preview.CanApply);
        switch (drift)
        {
            case "inventory":
                fixture.Engine.Add(new() { Name = "unrelated", Image = "fixture" });
                break;
            case "image":
                fixture.Engine.Images[0].Id = "sha256:changed";
                break;
            case "settings":
                fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
                break;
            case "resource":
                fixture.Engine.Networks["a"] = "demo";
                break;
        }
        Assert.Equal(ComposePlanValidation.Stale, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Equal(ComposeReviewOutcomeKind.Stale, (await fixture.Supervisor.ApplyReviewedAsync(review, true, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Fact]
    public async Task ConcurrentConsumersAuthorizeExactlyOneApply()
    {
        var fixture = new Fixture();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        var outcomes = await Task.WhenAll(fixture.Supervisor.ApplyReviewedAsync(review, true),
            fixture.Supervisor.ApplyReviewedAsync(review, true));
        Assert.Single(outcomes, o => o.Kind == ComposeReviewOutcomeKind.Applied);
        Assert.Single(outcomes, o => o.Kind == ComposeReviewOutcomeKind.AlreadyUsed);
        Assert.Single(fixture.Engine.Mutations, mutation => mutation == "create:demo_web");
        Assert.Equal(ComposePlanValidation.AlreadyUsed, await fixture.Supervisor.ValidateReviewAsync(review));
    }

    [Fact]
    public async Task ForeignTokenDoesNotConsumeOriginal()
    {
        var fixture = new Fixture();
        var foreign = new Fixture();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Equal(ComposePlanValidation.ForeignToken, await foreign.Supervisor.ValidateReviewAsync(review));
        Assert.Equal(ComposeReviewOutcomeKind.ForeignToken, (await foreign.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Equal(ComposePlanValidation.Valid, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Empty(foreign.Engine.Mutations);
    }

    [Fact]
    public async Task ExpirationIsTenMinutesAndApplyConsumesExpiredToken()
    {
        var clock = new ReviewClock();
        var fixture = new Fixture(clock: clock);
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Equal(clock.Now.AddMinutes(10), review.ExpiresAt);
        clock.Now = review.ExpiresAt;
        Assert.Equal(ComposePlanValidation.Expired, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Equal(ComposeReviewOutcomeKind.Expired, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Equal(ComposeReviewOutcomeKind.AlreadyUsed, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Fact]
    public async Task ExpirationDuringRevalidationCannotAuthorizeMutation()
    {
        var clock = new ReviewClock();
        var fixture = new Fixture(clock: clock);
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        fixture.BeforeCapabilities = () => { clock.Now = review.ExpiresAt; return Task.CompletedTask; };
        Assert.Equal(ComposeReviewOutcomeKind.Expired, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task RequestCollectionsAreSnapshottedRatherThanTrustingCallerEdits()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options = new() { Image = "fixture" };
        var replicas = new Dictionary<string, int> { ["web"] = 2 };
        var services = new List<string> { "web" };
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project, new() { Replicas = replicas, Services = services });
        replicas["web"] = 0;
        services.Clear();
        Assert.Equal("2", Row(review, "replicas").EffectiveValue);
        Assert.True((await fixture.Supervisor.ApplyReviewedAsync(review, true)).AllSucceeded);
        Assert.Equal(2, fixture.Engine.Containers.Count);
        Assert.Empty(fixture.SavedProject!.ReplicaOverrides);
    }

    [Fact]
    public async Task DesiredInputIsAnImmutableSnapshotIndependentOfCallerObject()
    {
        var fixture = new Fixture();
        var desired = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(fixture.Project))!;
        var review = await fixture.Supervisor.PrepareReviewAsync(desired);
        desired.Services[0].Options.Image = "edited-after-review";
        desired.Services.Clear();
        Assert.Equal(ComposePlanValidation.Valid, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.True((await fixture.Supervisor.ApplyReviewedAsync(review, true)).AllSucceeded);
        Assert.Equal("fixture", fixture.SavedProject!.Services[0].Options.Image);
    }

    [Fact]
    public async Task ReviewDoesNotRetainMutableAppliedStateReferencesFromStore()
    {
        var fixture = new Fixture { ReturnStoredReferences = true };
        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        fixture.Engine.Mutations.Clear();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project,
            new() { Operation = ComposeLifecycleOperation.Restart });
        fixture.SavedProject!.AppliedServices["web"].Service.Options.Image = "mutated-stored-image";
        Assert.Equal("fixture", review.Project.AppliedServices["web"].Service.Options.Image);
        Assert.Equal("fixture", Row(review, "image").EffectiveValue);
        Assert.NotEqual(ComposePlanValidation.Valid, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task ActiveSelectionAndDependencyClosureComeFromTheSharedPlanner()
    {
        var fixture = new Fixture();
        var project = ComposeImporter.ParseProject("""
            name: demo
            services:
              web:
                image: fixture
                profiles: [frontend]
                depends_on: [db]
              db:
                image: fixture
                scale: 2
              unrelated:
                image: fixture
                profiles: [frontend]
            """);
        var request = new ComposeOperationRequest { Services = ["web"] };
        var plan = await fixture.Supervisor.PlanAsync(project, request);
        var review = await fixture.Supervisor.PrepareReviewAsync(project, request);
        Assert.True(review.Preview.CanApply);
        Assert.Equal(plan.Services.Select(p => p.Service.Name).Distinct().Order(),
            review.Preview.Settings.Where(row => row.Setting == "replicas").Select(row => row.Service).Order());
        Assert.DoesNotContain(review.Preview.Settings, row => row.Service == "unrelated");
        Assert.Equal("2", Assert.Single(review.Preview.Settings, row => row.Service == "db" && row.Setting == "replicas").EffectiveValue);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Fact]
    public async Task CancellationDuringRevalidationConsumesWithoutMutating()
    {
        var fixture = new Fixture();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        using var cancellation = new CancellationTokenSource();
        fixture.BeforeCapabilities = () =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        };
        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true, true, cancellation.Token);
        Assert.Equal(ComposeReviewOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(ComposeReviewOutcomeKind.AlreadyUsed, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("unresolved")]
    [InlineData("invalid-health")]
    [InlineData("missing-image")]
    [InlineData("missing-build")]
    [InlineData("inventory-error")]
    [InlineData("capability-error")]
    public async Task InvalidOrUnresolvedEvidenceIsAnUnignorableSafeBlocker(string failure)
    {
        const string privateDiagnostic = "unstructured-private-engine-payload";
        var fixture = new Fixture();
        var request = new ComposeOperationRequest();
        switch (failure)
        {
            case "negative": request = new() { Replicas = new Dictionary<string, int> { ["web"] = -1 } }; break;
            case "unresolved": fixture.Project.Warnings.Add("Variable 'X' is unset; using an empty string."); break;
            case "invalid-health": fixture.Project.Services[0].Options.Health = new() { Test = ["CMD-SHELL", "true"], Retries = 0 }; break;
            case "missing-image":
                fixture.Engine.Images.Clear();
                fixture.Project.Services[0].PullPolicy = ComposeImagePolicy.Never;
                break;
            case "missing-build":
                fixture.Project.Services[0].Build = new() { Context = Path.Combine(Directory.GetCurrentDirectory(), "absent-" + Guid.NewGuid().ToString("N")) };
                request = new() { Build = true };
                break;
            case "inventory-error": fixture.Engine.BeforeList = () => throw new IOException(privateDiagnostic); break;
            case "capability-error": fixture.CapabilityError = new IOException(privateDiagnostic); break;
        }
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project, request);
        Assert.False(review.Preview.CanApply);
        Assert.DoesNotContain(privateDiagnostic, JsonSerializer.Serialize(review));
        Assert.Equal(ComposePlanValidation.Blocked, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Equal(ComposeReviewOutcomeKind.Blocked, (await fixture.Supervisor.ApplyReviewedAsync(review, true, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Fact]
    public async Task SelectedResourceDeclarationsShowCreationSettingsWithoutPrivateOptions()
    {
        var fixture = new Fixture();
        fixture.Project.Networks = [new()
        {
            Name = "a", Driver = "bridge", Subnet = "10.42.0.0/24",
            Gateway = "10.42.0.1", IpRange = "10.42.0.128/25",
            DriverOpts = ["credential=private-driver-value"], Labels = new() { ["custom"] = "private-label" },
        }];
        fixture.Project.Volumes = [new() { Name = "data", Driver = "local", DriverOpts = ["o=private-volume-value"] }];
        fixture.Project.Services[0].Options.Volumes = ["data:/data"];
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.True(review.Preview.CanApply);
        var network = Row(review, "network");
        Assert.Contains("driver: bridge", network.EffectiveValue);
        Assert.Contains("10.42.0.0/24", network.EffectiveValue);
        Assert.Contains("10.42.0.1", network.EffectiveValue);
        Assert.Contains("10.42.0.128/25", network.EffectiveValue);
        Assert.Contains("driver options: 1", network.EffectiveValue);
        Assert.Contains("driver: local", Row(review, "volume").EffectiveValue);
        var serialized = JsonSerializer.Serialize(review);
        Assert.DoesNotContain("private-driver-value", serialized);
        Assert.DoesNotContain("private-volume-value", serialized);
        Assert.DoesNotContain("private-label", serialized);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("owner")]
    [InlineData("driver")]
    [InlineData("driver-unknown")]
    [InlineData("owner-unknown")]
    [InlineData("unknown")]
    [InlineData("network-inventory")]
    public async Task ResourceConflictsAreBlockedBeforeProvisioning(string conflict)
    {
        var fixture = new Fixture();
        fixture.Project.Networks = [new() { Name = "a", Driver = "bridge", External = conflict == "external" }];
        if (conflict == "owner") fixture.Engine.Networks["a"] = "other";
        if (conflict == "driver") fixture.Engine.NetworkInspectionOverrides["a"] = """{"Driver":"other","Labels":{}}""";
        if (conflict == "driver-unknown") fixture.Engine.Networks["a"] = "demo";
        if (conflict == "owner-unknown") fixture.Engine.NetworkInspectionOverrides["a"] = """{"Driver":"bridge"}""";
        if (conflict == "unknown")
        {
            fixture.Project.Volumes = [new() { Name = "data" }];
            fixture.Project.Services[0].Options.Volumes = ["data:/data"];
            fixture.Engine.VolumeInventoryError = new IOException("raw-private-resource-error");
        }
        if (conflict == "network-inventory")
        {
            fixture.Engine.NetworkInventoryError = new IOException("localized-private-network-error");
        }
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.False(review.Preview.CanApply);
        Assert.Contains(review.Preview.Settings, row => row.Disposition == ComposeSettingDisposition.Blocked);
        Assert.Equal(ComposeReviewOutcomeKind.Blocked, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.DoesNotContain("raw-private-resource-error", JsonSerializer.Serialize(review));
        Assert.DoesNotContain("localized-private-network-error", JsonSerializer.Serialize(review));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task CancelledAdvisoryAndApplyLeaveEngineAndSettingsUntouched()
    {
        var fixture = new Fixture();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(ComposePlanValidation.Cancelled, await fixture.Supervisor.ValidateReviewAsync(review, cancellation.Token));
        Assert.Equal(ComposeReviewOutcomeKind.Cancelled, (await fixture.Supervisor.ApplyReviewedAsync(review, true, true, cancellation.Token)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
    }

    [Fact]
    public async Task CancelledExecutionRetainsActualPartialOutcomesAndDesiredIntent()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine { BeforeStart = () => cancellation.Cancel() };
        var fixture = new Fixture(engine: engine);
        fixture.Project.Services.Add(new() { Name = "worker", Options = new() { Image = "fixture" } });
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true, ct: cancellation.Token);
        Assert.Equal(ComposeReviewOutcomeKind.Cancelled, outcome.Kind);
        Assert.False(outcome.AllSucceeded);
        Assert.NotEmpty(outcome.Services);
        Assert.Contains("refresh actual state", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedApplyFailureIsExplicitAndDoesNotLogExceptionSecrets()
    {
        var logger = new ReviewLogger();
        var fixture = new Fixture(logger: logger);
        fixture.Project.Services[0].Options = new() { Image = "fixture" };
        fixture.Project.Volumes = [new() { Name = "data" }];
        fixture.Project.Services[0].Options.Volumes = ["data:/data"];
        fixture.Engine.FailCreateVolume = "data";
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project,
            new() { Replicas = new Dictionary<string, int> { ["web"] = 2 } });
        Assert.True(review.Preview.CanApply);
        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true, true);
        Assert.Equal(ComposeReviewOutcomeKind.PartialFailure, outcome.Kind);
        Assert.Contains("partially", outcome.Message);
        Assert.Contains(logger.Messages, line => line.Contains("Technical values withheld"));
        Assert.DoesNotContain("fixture volume creation failed", string.Join("\n", logger.Messages));
        Assert.All(logger.Exceptions, Assert.Null);
        Assert.Equal(2, fixture.SavedProject!.ReplicaOverrides["web"]);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("create:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicTokenAndOutcomeDoNotSerializeRawPlanOrEngineErrors()
    {
        var engine = new Engine { FailRun = "demo_web" };
        var fixture = new Fixture(engine: engine);
        fixture.Project.Services[0].Options.EnvironmentVariables.Add("ORDINARY=private-value-1974");
        fixture.Project.Services[0].Options.Command = "unstructured-command-9324";
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        var serializedToken = JsonSerializer.Serialize(review);
        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true);
        var serializedOutcome = JsonSerializer.Serialize(outcome);
        foreach (var text in new[] { serializedToken, serializedOutcome })
        {
            Assert.DoesNotContain("private-value-1974", text);
            Assert.DoesNotContain("unstructured-command-9324", text);
            Assert.DoesNotContain("Fingerprint", text);
            Assert.DoesNotContain("EnvironmentVariables", text);
            Assert.DoesNotContain("fixture run failure", text);
        }
        Assert.False(outcome.AllSucceeded);
        Assert.All(outcome.Services, row => Assert.Null(row.ContainerId));
        Assert.DoesNotContain("\"Execution\"", serializedOutcome);
        Assert.DoesNotContain("\"Stamp\"", serializedToken);
    }

    [Fact]
    public async Task NativeMutationFailureNeverFallsBackAndPublicOutcomeWithholdsArbitraryExceptionText()
    {
        const string secret = "private-unstructured-connect-failure-2897";
        var fixture = new Fixture();
        fixture.Engine.BeforeConnect = (_, _) => throw new IOException(secret);
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true);
        Assert.Equal(ComposeReviewOutcomeKind.PartialFailure, outcome.Kind);
        Assert.NotEmpty(outcome.Services);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(outcome));
        Assert.DoesNotContain(fixture.Engine.Mutations, mutation => mutation.StartsWith("run:", StringComparison.Ordinal));
        Assert.Contains("create:demo_web", fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Restart)]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    public async Task ExistingLifecycleReviewsAppliedNotPendingConfiguration(ComposeLifecycleOperation operation)
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        fixture.Engine.Mutations.Clear();
        fixture.Project.Services[0].Options.Image = "pending-image";
        fixture.Project.Services[0].Options.EnvironmentVariables = ["ORDINARY=pending-secret"];
        fixture.Project.Warnings.Add("Blocked deployment: pending edit.");
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project, new() { Operation = operation });
        Assert.True(review.Preview.CanApply);
        Assert.Equal("fixture", Row(review, "image").EffectiveValue);
        Assert.DoesNotContain("pending-image", JsonSerializer.Serialize(review));
        Assert.Equal("Existing container only", Row(review, "backend").EffectiveValue);
        Assert.True((await fixture.Supervisor.ApplyReviewedAsync(review, true)).AllSucceeded);
        Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("create:", StringComparison.Ordinal) || m.StartsWith("pull:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecreateMountIdentityDriftRequiresFreshReview()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        fixture.Engine.Mounts["demo_web"] = [new { Type = "volume", Name = "old-anonymous", Source = "/old", Destination = "/data", RW = true }];
        fixture.Project.Services[0].Options.Command = "changed";
        fixture.Engine.Mutations.Clear();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.True(review.Preview.CanApply);
        Assert.Contains("Recreate", Row(review, "instances").EffectiveValue);
        // A replacement must still say plainly that container contents are lost.
        Assert.Contains("discards anything written inside it", Row(review, "instances").Explanation);
        fixture.Engine.Mounts["demo_web"] = [new { Type = "volume", Name = "new-anonymous", Source = "/new", Destination = "/data", RW = true }];
        Assert.Equal(ComposeReviewOutcomeKind.Stale, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Theory]
    [InlineData("8080:80", "0.0.0.0", 6, true)]
    [InlineData("127.0.0.1:8080:80", "127.0.0.2", 6, false)]
    [InlineData("8080:80/udp", "0.0.0.0", 6, false)]
    [InlineData("[::1]:8080:80", "::1", 6, true)]
    public async Task PublishedPortConflictsRespectBindingAndProtocol(string desired, string occupiedHost, int protocol, bool blocked)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.PortMappings = [desired];
        fixture.Engine.Add(new() { Name = "other", Image = "fixture" });
        fixture.Engine.PublishedPorts["other"] = [new() { HostPort = 8080, ContainerPort = 80, BindingAddress = occupiedHost, Protocol = protocol }];
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Equal(!blocked, review.Preview.CanApply);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task UnknownPortInventoryAndPublishedRangesHaveExplicitBlockers()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.PortMappings = ["8080:80"];
        fixture.Engine.Add(new() { Name = "other", Image = "fixture" });
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Contains(review.Preview.Settings, row => row.Disposition == ComposeSettingDisposition.Blocked &&
            row.Setting == "ports" && row.Explanation.Contains("Unknown"));
        fixture.Project.Services[0].Options.PortMappings = ["8080-8081:80-81"];
        review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Contains(review.Preview.Settings, row => row.Disposition == ComposeSettingDisposition.Blocked &&
            row.Explanation.Contains("ranges"));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task KeptInstanceDoesNotConflictWithItsOwnBinding()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.PortMappings = ["8080:80"];
        Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
        fixture.Engine.PublishedPorts["demo_web"] = [new() { HostPort = 8080, ContainerPort = 80, Protocol = 6 }];
        fixture.Engine.Mutations.Clear();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.True(review.Preview.CanApply);
        Assert.Contains("Keep", Row(review, "instances").EffectiveValue);
        Assert.True((await fixture.Supervisor.ApplyReviewedAsync(review, true)).AllSucceeded);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task SelectedServicesCannotPublishTheSameHostBinding()
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.PortMappings = ["8080:80"];
        fixture.Project.Services.Add(new() { Name = "worker", Options = new() { Image = "fixture", PortMappings = ["8080:81"] } });
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.False(review.Preview.CanApply);
        Assert.Contains(review.Preview.Settings, row => row.Disposition == ComposeSettingDisposition.Blocked &&
            row.Explanation.Contains("conflicting published"));
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task InlineConfigStagingIsApprovedReadOnlyAndDoesNotModifyExistingMounts()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-stage-" + Guid.NewGuid().ToString("N"));
        var fixture = new Fixture(persisted: ComposeInlineConfigTests.InlineProject("original\n"), stagingRoot: root);
        try
        {
            var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
            Assert.True(review.Preview.CanApply);
            Assert.False(Directory.Exists(root));
            Assert.DoesNotContain("original", JsonSerializer.Serialize(review));
            Assert.True((await fixture.Supervisor.ApplyReviewedAsync(review, true)).AllSucceeded);
            var name = fixture.Project.Name + "_web";
            var original = InlineMountSource(fixture.Engine.Containers[name].Volumes.Single());
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes("original\n"), File.ReadAllBytes(original));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, original);

            fixture.Engine.Mutations.Clear();
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
            Assert.Empty(fixture.Engine.Mutations);
            Assert.Equal(original, InlineMountSource(fixture.Engine.Containers[name].Volumes.Single()));

            fixture.Project.Configs[0].Content = "changed \u2603";
            var changed = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
            Assert.True(changed.Preview.CanApply);
            Assert.Contains("Recreate", Row(changed, "instances").EffectiveValue);
            Assert.Equal("original\n", File.ReadAllText(original));
            Assert.True((await fixture.Supervisor.ApplyReviewedAsync(changed, true)).AllSucceeded);
            var replacement = InlineMountSource(fixture.Engine.Containers[name].Volumes.Single());
            Assert.NotEqual(original, replacement);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes("changed \u2603"), File.ReadAllBytes(replacement));
            Assert.Equal("original\n", File.ReadAllText(original));
            Assert.Equal("changed \u2603", fixture.SavedProject!.Configs[0].Content);

            fixture.Project.Configs[0].Content = "pending";
            fixture.Engine.Mutations.Clear();
            var restart = await fixture.Supervisor.PrepareReviewAsync(fixture.Project,
                new() { Operation = ComposeLifecycleOperation.Restart });
            Assert.True((await fixture.Supervisor.ApplyReviewedAsync(restart, true)).AllSucceeded);
            Assert.DoesNotContain(fixture.Engine.Mutations, m => m.StartsWith("create:") || m.StartsWith("run:"));
            Assert.Equal("changed \u2603", File.ReadAllText(replacement));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InlineConfigEditsAfterReviewAreStaleWithoutStagingOrDisclosure()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-stale-" + Guid.NewGuid().ToString("N"));
        var fixture = new Fixture(persisted: ComposeInlineConfigTests.InlineProject("private-inline-original"), stagingRoot: root);
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        fixture.Project.Configs[0].Content = "private-inline-edit";
        var result = await fixture.Supervisor.ApplyReviewedAsync(review, true);
        Assert.Equal(ComposeReviewOutcomeKind.Stale, result.Kind);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.False(Directory.Exists(root));
        Assert.DoesNotContain("private-inline", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineConfigFailedPreparationOrCancellationPreservesOldContainer(bool cancel)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-failure-" + Guid.NewGuid().ToString("N"));
        var logger = new ReviewLogger();
        var fixture = new Fixture(persisted: ComposeInlineConfigTests.InlineProject("private-original"), logger: logger, stagingRoot: root);
        try
        {
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
            var name = fixture.Project.Name + "_web";
            var original = InlineMountSource(fixture.Engine.Containers[name].Volumes.Single());
            fixture.Project.Configs[0].Content = "private-replacement";
            var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
            fixture.Engine.Mutations.Clear();
            using var cancellation = new CancellationTokenSource();
            var probes = new List<string>();
            fixture.Engine.VerifyBindMount = source =>
            {
                probes.Add(source);
                Assert.Equal("private-original", File.ReadAllText(original));
                if (cancel) cancellation.Cancel();
                return cancel ? BindMountProbeResult.MountsAsFile : BindMountProbeResult.MountsAsDirectory;
            };
            var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true, ct: cancellation.Token);
            Assert.False(outcome.AllSucceeded);
            Assert.Empty(fixture.Engine.Mutations);
            Assert.Equal("private-original", File.ReadAllText(original));
            Assert.NotEmpty(probes);
            Assert.Equal(probes.Count, probes.Distinct().Count());
            Assert.DoesNotContain("private-replacement", JsonSerializer.Serialize(outcome));
            Assert.DoesNotContain("private-replacement", string.Join("\n", logger.Messages));
            Assert.All(logger.Exceptions, Assert.Null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InlineStagingSeparatesCollidingNamesAndOnlyMountsGrantedConfigs()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-names-" + Guid.NewGuid().ToString("N"));
        var inputDirectory = root + "-input";
        var project = ComposeInlineConfigTests.InlineProject("first");
        project.Configs[0].Name = "../name";
        project.Services[0].Configs[0].Source = "../name";
        project.Configs.Add(new() { Name = "..?name", Content = "second" });
        project.Services[0].Configs.Add(new() { Source = "..?name", Target = "/other" });
        project.Services[0].Configs.Add(new() { Source = "../name", Target = "/again" });
        var fileName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("../name")));
        project.Configs.Add(new() { Name = fileName, File = Path.Combine(inputDirectory, "content") });
        project.Services[0].Configs.Add(new() { Source = fileName, Target = "/binary" });
        project.Configs.Add(new() { Name = "ungranted", Content = "not-mounted" });
        var fixture = new Fixture(persisted: project, stagingRoot: root);
        try
        {
            Directory.CreateDirectory(inputDirectory);
            byte[] binary = [0xff, 0x00, 0x80, 0x0d, 0x0a];
            File.WriteAllBytes(Path.Combine(inputDirectory, "content"), binary);
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
            var sources = fixture.Engine.Containers[project.Name + "_web"].Volumes.Select(InlineMountSource).ToArray();
            Assert.Equal(4, sources.Length);
            Assert.Equal(3, sources.Distinct().Count());
            Assert.Equal(new[] { "first", "first", "second" }, sources.Take(3).Select(File.ReadAllText).Order().ToArray());
            Assert.Equal(binary, File.ReadAllBytes(sources[3]));
            Assert.All(sources, path => Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path));
            fixture.Supervisor.CleanStaging(project.Name);
            Assert.All(sources, path => Assert.False(File.Exists(path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(inputDirectory)) Directory.Delete(inputDirectory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupInlineValidationIsScopedToUntrackedInstancesAndCurrentSnapshot(bool legacy)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ComposeInlineConfigTests.InlineProject("applied-content");
            project.Services[0].Replicas = 2;
            project.Services[0].Restart = RestartPolicyKind.Always;
            var fixture = new Fixture(persisted: project, stagingRoot: root);
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
            var saved = fixture.SavedProject!;
            saved.Configs[0].File = "invalid-pending-source";
            if (legacy) saved.AppliedServices.Clear();
            var relaunched = new Fixture(engine: fixture.Engine, persisted: saved, stagingRoot: root);
            fixture.Engine.Mutations.Clear();

            await relaunched.Supervisor.ReconcileAsync();
            if (legacy)
            {
                Assert.Equal(2, relaunched.Supervisor.ReconciliationWarnings.Count);
                Assert.Empty(relaunched.RestartPolicies);
            }
            else
            {
                Assert.Empty(relaunched.Supervisor.ReconciliationWarnings);
                Assert.Equal(2, relaunched.RestartPolicies.Count);
            }

            relaunched.Project.Configs[0].File = null;
            await relaunched.Supervisor.ReconcileAsync();
            Assert.Empty(relaunched.Supervisor.ReconciliationWarnings);
            Assert.Equal(2, relaunched.RestartPolicies.Count);
            Assert.Empty(fixture.Engine.Mutations);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("snowman \u2603\r\n")]
    public async Task SeparateInlineAndFileWritersPreserveExactBytes(string content)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-writers-" + Guid.NewGuid().ToString("N"));
        var input = root + "-input";
        try
        {
            Directory.CreateDirectory(input);
            byte[] binary = [0xff, 0x00, 0x80, 0x0d, 0x0a];
            var source = Path.Combine(input, "token.bin");
            File.WriteAllBytes(source, binary);
            var project = ComposeInlineConfigTests.InlineProject(content);
            project.Secrets.Add(new() { Name = "token", File = source });
            project.Services[0].Secrets.Add(new() { Source = "token", Target = "/run/secrets/token" });
            var fixture = new Fixture(persisted: project, stagingRoot: root + Path.DirectorySeparatorChar);
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
            var mounts = fixture.Engine.Containers[project.ContainerNameFor("web")].Volumes;
            var inline = InlineMountSource(Assert.Single(mounts, mount => mount.EndsWith(":/settings:ro", StringComparison.Ordinal)));
            var file = InlineMountSource(Assert.Single(mounts, mount => mount.EndsWith(":/run/secrets/token:ro", StringComparison.Ordinal)));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, inline);
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, file);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(content), File.ReadAllBytes(inline));
            Assert.Equal(binary, File.ReadAllBytes(file));
            Assert.Equal(2, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(input)) Directory.Delete(input, true);
        }
    }

    [Theory]
    [InlineData(ComposeLifecycleOperation.Stop)]
    [InlineData(ComposeLifecycleOperation.Down)]
    [InlineData(ComposeLifecycleOperation.Restart)]
    public async Task InvalidSavedInlineSourceDoesNotBlockAppliedContainerOperations(ComposeLifecycleOperation operation)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "inline-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "store");
            var staging = Path.Combine(root, "staging");
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, directory);
            var fixture = new Fixture(persisted: ComposeInlineConfigTests.InlineProject("applied-content"),
                stagingRoot: staging, storeOverride: store);
            Assert.True((await fixture.Supervisor.UpAsync(fixture.Project)).AllSucceeded);
            var saved = store.Get(fixture.Project.Name)!;
            var original = Assert.Single(Directory.GetFiles(staging, "content", SearchOption.AllDirectories));
            var containerId = Assert.Single(saved.AppliedServices).Value.ContainerId;
            saved.Configs[0].Content = "invalid-pending-content";
            saved.Configs[0].File = "conflicting-source";
            File.WriteAllText(Path.Combine(directory, "compose-projects.json"), JsonSerializer.Serialize(new[] { saved }));

            var reloadedStore = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, directory);
            var loaded = reloadedStore.Get(saved.Name)!;
            var reloaded = new Fixture(engine: fixture.Engine, persisted: loaded, stagingRoot: staging,
                storeOverride: reloadedStore);
            fixture.Engine.Mutations.Clear();
            var blockedUp = await reloaded.Supervisor.PrepareReviewAsync(loaded);
            Assert.False(blockedUp.Preview.CanApply);
            Assert.Equal(ComposeReviewOutcomeKind.Blocked,
                (await reloaded.Supervisor.ApplyReviewedAsync(blockedUp, true)).Kind);
            Assert.Empty(fixture.Engine.Mutations);

            var review = await reloaded.Supervisor.PrepareReviewAsync(loaded, new() { Operation = operation });
            Assert.True(review.Preview.CanApply);
            var result = await reloaded.Supervisor.ApplyReviewedAsync(review, true);
            Assert.True(result.AllSucceeded, JsonSerializer.Serialize(result));
            Assert.NotEmpty(fixture.Engine.Mutations);
            Assert.DoesNotContain(fixture.Engine.Mutations, mutation =>
                mutation.StartsWith("create:", StringComparison.Ordinal) || mutation.StartsWith("run:", StringComparison.Ordinal));
            Assert.Equal("applied-content", File.ReadAllText(original));
            Assert.Single(Directory.GetFiles(staging, "content", SearchOption.AllDirectories));

            var persisted = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, directory).Get(saved.Name)!;
            Assert.Equal("invalid-pending-content", persisted.Configs[0].Content);
            Assert.Equal("conflicting-source", persisted.Configs[0].File);
            if (operation == ComposeLifecycleOperation.Down)
            {
                Assert.Empty(persisted.AppliedServices);
                Assert.Empty(fixture.Engine.Containers);
            }
            else
            {
                var applied = Assert.Single(persisted.AppliedServices).Value;
                Assert.Equal(operation == ComposeLifecycleOperation.Stop, applied.ManuallyStopped);
                Assert.Equal(containerId, applied.ContainerId);
                Assert.Single(fixture.Engine.Containers);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string InlineMountSource(string mount)
    {
        Assert.EndsWith(":ro", mount);
        return mount[..mount.LastIndexOf(":/", StringComparison.Ordinal)];
    }

    private static ComposeCompatibilitySetting Row(ComposeReviewToken review, string setting) =>
        Assert.Single(review.Preview.Settings, row => row.Setting == setting);

    /// <summary>Provides deterministic review-token time so expiration and revalidation cases do not wait in real time.</summary>
    private sealed class ReviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Captures supervisor log entries so tests can verify sensitive exceptions are not exposed.</summary>
    private sealed class ReviewLogger : ILogger<ComposeProjectSupervisor>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
