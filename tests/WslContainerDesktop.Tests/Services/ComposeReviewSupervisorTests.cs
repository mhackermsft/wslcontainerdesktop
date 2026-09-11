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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;
using static WslContainerDesktop.Tests.Services.ComposeNetworkSupervisorTests;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeReviewSupervisorTests
{
    [Theory]
    [InlineData(WslcCapabilitySupport.Supported, "NativeCreateConnectStart", ComposeSettingDisposition.Supported)]
    [InlineData(WslcCapabilitySupport.Unsupported, "LegacyRun", ComposeSettingDisposition.Approximated)]
    [InlineData(WslcCapabilitySupport.Unknown, "Unknown", ComposeSettingDisposition.Blocked)]
    public async Task PreviewMatchesActualTriStateBackendAndNeverMutates(
        WslcCapabilitySupport support, string backend, ComposeSettingDisposition disposition)
    {
        var fixture = new Fixture(support);
        fixture.Project.Services[0].Restart = RestartPolicyKind.Always;
        var before = JsonSerializer.Serialize(fixture.Project);
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);

        Assert.Equal(before, JsonSerializer.Serialize(fixture.Project));
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Empty(fixture.SavedSnapshots);
        Assert.Empty(fixture.HealthChecks);
        Assert.Empty(fixture.RestartPolicies);
        var backendRow = Row(review, "backend");
        Assert.Equal(backend, backendRow.EffectiveValue);
        Assert.Equal(disposition, backendRow.Disposition);
        Assert.Equal(support, backendRow.Capability);
        Assert.Contains("application", Row(review, "restart").EffectiveValue);
        Assert.Equal(support != WslcCapabilitySupport.Unknown, review.Preview.CanApply);
        Assert.Equal(1, fixture.CapabilityInvalidations);

        var outcome = await fixture.Supervisor.ApplyReviewedAsync(review, true);
        if (support == WslcCapabilitySupport.Unknown)
        {
            Assert.Equal(ComposeReviewOutcomeKind.Blocked, outcome.Kind);
            Assert.Empty(fixture.Engine.Mutations);
        }
        else
        {
            Assert.Equal(ComposeReviewOutcomeKind.Applied, outcome.Kind);
            Assert.Equal(2, fixture.CapabilityInvalidations);
            Assert.Contains(support == WslcCapabilitySupport.Supported ? "create:demo_web" : "run:demo_web", fixture.Engine.Mutations);
            Assert.Equal(2, fixture.SavedProject!.Services[0].Options.GetNetworkAttachments().Count);
            if (support == WslcCapabilitySupport.Unsupported)
                Assert.Equal(ComposeSettingDisposition.Ignored, Row(review, "networks[2]").Disposition);
        }
    }

    [Theory]
    [InlineData(WslcCapabilitySupport.Supported, ComposePolicyOwner.Engine)]
    [InlineData(WslcCapabilitySupport.Unsupported, ComposePolicyOwner.Application)]
    [InlineData(WslcCapabilitySupport.Unknown, ComposePolicyOwner.Unknown)]
    public async Task NativeHealthFallbackAndUnknownHaveDistinctOwners(WslcCapabilitySupport support, ComposePolicyOwner owner)
    {
        var fixture = new Fixture();
        fixture.Project.Services[0].Options.Health = new() { Test = ["CMD-SHELL", "true"] };
        fixture.Snapshot = new("wslc.exe", "fixture", Enum.GetValues<WslcFeature>().ToDictionary(f => f,
            f => new WslcCapability(f.ToString().StartsWith("CreateHealth", StringComparison.Ordinal)
                ? support : WslcCapabilitySupport.Supported, "not-for-display")));
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.Contains($"Probe owner: {owner}", Row(review, "healthcheck").EffectiveValue);
        Assert.Equal(support != WslcCapabilitySupport.Unknown, review.Preview.CanApply);
        Assert.DoesNotContain("not-for-display", JsonSerializer.Serialize(review));
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
        fixture.Snapshot = Capabilities(WslcCapabilitySupport.Unsupported);

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
    public async Task CapabilityBecomingUnknownIsBlockedNotLegacyFallback()
    {
        var fixture = new Fixture();
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        fixture.Snapshot = Capabilities(WslcCapabilitySupport.Unknown);
        Assert.Equal(ComposePlanValidation.Blocked, await fixture.Supervisor.ValidateReviewAsync(review));
        Assert.Equal(ComposeReviewOutcomeKind.Blocked, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.Empty(fixture.Engine.Mutations);
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
            fixture.Engine.VolumeInspectionError = "raw-private-resource-error";
        }
        var review = await fixture.Supervisor.PrepareReviewAsync(fixture.Project);
        Assert.False(review.Preview.CanApply);
        Assert.Contains(review.Preview.Settings, row => row.Disposition == ComposeSettingDisposition.Blocked);
        Assert.Equal(ComposeReviewOutcomeKind.Blocked, (await fixture.Supervisor.ApplyReviewedAsync(review, true)).Kind);
        Assert.DoesNotContain("raw-private-resource-error", JsonSerializer.Serialize(review));
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
        var fixture = new Fixture(WslcCapabilitySupport.Unsupported);
        fixture.Project.Services.Add(new() { Name = "worker", Options = new() { Image = "fixture" } });
        fixture.Engine.AfterRun = _ => cancellation.Cancel();
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
        var fixture = new Fixture(WslcCapabilitySupport.Unsupported, engine);
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

    private static ComposeCompatibilitySetting Row(ComposeReviewToken review, string setting) =>
        Assert.Single(review.Preview.Settings, row => row.Setting == setting);

    private sealed class ReviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
