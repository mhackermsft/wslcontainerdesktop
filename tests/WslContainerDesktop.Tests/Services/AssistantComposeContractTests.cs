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
using static WslContainerDesktop.Tests.Services.ComposeNetworkOrchestratorTests;
using static WslContainerDesktop.Tests.Services.ComposeNetworkSupervisorTests;

namespace WslContainerDesktop.Tests.Services;

public sealed class AssistantComposeContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string BasicYaml = "services:\n  web:\n    image: fixture\n";
    private const string NetworkYaml = """
        services:
          web:
            image: fixture
            networks: [front, back]
            restart: always
            ports: ["8080:80"]
            volumes: ["data:/data", "/tmp:/scratch:ro"]
            environment:
              ORDINARY: synthetic-private
            healthcheck:
              test: ["CMD-SHELL", "echo synthetic-private"]
            logging:
              driver: json-file
        networks:
          front: {}
          back: {}
        volumes:
          data: {}
        """;

    [Theory]
    [InlineData(false, WslcCapabilitySupport.Supported, "NativeCreateConnectStart")]
    [InlineData(true, WslcCapabilitySupport.Supported, "NativeCreateConnectStart")]
    [InlineData(false, WslcCapabilitySupport.Unsupported, "LegacyRun")]
    [InlineData(true, WslcCapabilitySupport.Unsupported, "LegacyRun")]
    public async Task FullConsequencesRequireOneExplicitApprovalEvenWhenAutoApproved(
        bool template, WslcCapabilitySupport support, string backend)
    {
        var f = new Fixture(support, presenterAvailable: false);
        f.Snapshot = new("wslc.exe", "fixture", Enum.GetValues<WslcFeature>()
            .ToDictionary(feature => feature, _ => new WslcCapability(support, "fixture")));
        var catalog = Templates(NetworkYaml);
        var h = Harness(f, catalog);
        var call = Call(template, NetworkYaml);
        h.AutoApproved.Add(call.Name);
        string? output = null;
        h.Provider.Turns.Enqueue(async (invoke, ct) => output = await invoke(call, ct));
        var requested = Approval(h);
        var turn = h.Assistant.SendAsync("deploy the reviewed app");
        await Task.WhenAny(turn, requested.Task).WaitAsync(Deadline);
        Assert.False(turn.IsCompleted, output + "\n" + string.Join("\n", h.Activity.Select(a => a.Detail)));
        var approval = await requested.Task.WaitAsync(Deadline);
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
        foreach (var text in new[] { backend, "8080:80", "/scratch:ro", "demo_data", "import diagnostic",
                     "Ignored", "restart", "application", "healthcheck", "writable", "web", "image" })
            Assert.Contains(text, approval.Details);
        Assert.DoesNotContain("synthetic-private", approval.Details);
        Assert.DoesNotContain("synthetic-private", approval.Summary);
        Assert.True(approval.Details.Length <= AiTextSanitizer.EvidenceLimit);
        await h.Assistant.ApproveAsync(approval);
        await turn.WaitAsync(Deadline);
        AssertKind(output!, "Applied");
        Assert.Empty(f.Reviews);
        Assert.Contains("ORDINARY=synthetic-private", f.Engine.Containers["demo_web"].EnvironmentVariables);
        Assert.All(h.PersistedActivity, json => Assert.DoesNotContain("synthetic-private", json));
        h.Provider.Turns.Enqueue((_, _) => Task.FromResult("next"));
        await h.Assistant.SendAsync("summarize").WaitAsync(Deadline);
        Assert.DoesNotContain("synthetic-private", JsonSerializer.Serialize(h.Provider.Requests[^1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedCapabilitiesNeverRequestApprovalOrMutate(bool template)
    {
        var f = new Fixture(WslcCapabilitySupport.Unknown);
        var h = Harness(f, Templates(NetworkYaml));
        var approvals = 0;
        h.Assistant.ApprovalChanged += (_, a) => { if (a is not null) approvals++; };
        string? output = null;
        h.Provider.Turns.Enqueue(async (invoke, ct) => output = await invoke(Call(template, NetworkYaml), ct));
        await h.Assistant.SendAsync("deploy").WaitAsync(Deadline);
        AssertKind(output!, "Blocked");
        Assert.Contains("Unknown", output);
        Assert.Equal(0, approvals);
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
        Assert.Contains(h.Activity, a => a.Detail!.Contains("Unknown"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryDriftInvalidatesExactReviewedPlan(bool template)
    {
        var f = new Fixture();
        var tools = Tools(f, Templates(BasicYaml));
        var resolved = await tools.ResolveAsync(Call(template, BasicYaml), default);
        f.Engine.Add(new() { Name = "new-unrelated", Image = "fixture" });
        var output = await resolved.ExecuteAsync(default);
        AssertKind(output, "Stale");
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
        AssertKind(await resolved.ExecuteAsync(default), "AlreadyUsed");
    }

    [Fact]
    public async Task CapabilityDriftBlocksBeforeMutation()
    {
        var f = new Fixture();
        var resolved = await Tools(f).ResolveAsync(Call(false, NetworkYaml), default);
        f.Snapshot = Capabilities(WslcCapabilitySupport.Unknown);
        AssertKind(await resolved.ExecuteAsync(default), "Blocked");
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialExecutionReportsStartedFailedAndSkippedInstances(bool template)
    {
        const string yaml = """
            services:
              good: { image: fixture }
              bad: { image: fixture }
              dependent:
                image: fixture
                depends_on: [bad]
            """;
        var f = new Fixture(engine: new Engine { FailRun = "demo_bad" });
        var resolved = await Tools(f, Templates(yaml)).ResolveAsync(Call(template, yaml), default);
        Assert.Null(resolved.BlockedResult);
        var output = await resolved.ExecuteAsync(default);
        using var doc = JsonDocument.Parse(output);
        AssertKind(output, "PartialFailure");
        var outcomes = doc.RootElement.GetProperty("outcomes").EnumerateArray().ToList();
        Assert.Contains(outcomes, o => o.GetProperty("instance").GetString() == "good" && o.GetProperty("status").GetString() == "started");
        Assert.Contains(outcomes, o => o.GetProperty("instance").GetString() == "bad" && o.GetProperty("status").GetString() == "failed");
        Assert.Contains(outcomes, o => o.GetProperty("instance").GetString() == "dependent" && o.GetProperty("status").GetString() == "skipped");
        Assert.Contains("may remain", doc.RootElement.GetProperty("retentionNotice").GetString());
        Assert.Contains(doc.RootElement.GetProperty("retainedResources").EnumerateArray(),
            r => r.GetProperty("Kind").GetString() == "network" && r.GetProperty("State").GetString() == "unverified");
        Assert.DoesNotContain("demo_dependent", f.Engine.Containers.Keys);
        Assert.DoesNotContain("Execution", output);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("reset")]
    public async Task RefusalCancellationAndResetConsumeApprovalWithoutMutation(string action)
    {
        var f = new Fixture();
        var h = Harness(f);
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(Call(false, BasicYaml), ct));
        var requested = Approval(h);
        using var cancellation = new CancellationTokenSource();
        var turn = h.Assistant.SendAsync("deploy", cancellation.Token);
        var approval = await requested.Task.WaitAsync(Deadline);
        if (action == "reject") await h.Assistant.RejectAsync(approval);
        else if (action == "cancel") cancellation.Cancel();
        else h.Assistant.Reset();
        if (action == "reject") await turn.WaitAsync(Deadline);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Deadline));
        await h.Assistant.ApproveAsync(approval);
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
        Assert.Empty(f.RestartPolicies);
        Assert.Empty(f.HealthChecks);
    }

    [Fact]
    public async Task DeclineAndCancelledExecutionConsumeLocalToken()
    {
        var f = new Fixture();
        var tools = Tools(f);
        var rejected = await tools.ResolveAsync(Call(false, BasicYaml), default);
        AssertKind(await rejected.DeclineAsync!(), "Cancelled");
        AssertKind(await rejected.ExecuteAsync(default), "AlreadyUsed");
        var cancelled = await tools.ResolveAsync(Call(false, BasicYaml), default);
        AssertKind(await cancelled.ExecuteAsync(new CancellationToken(true)), "Cancelled");
        AssertKind(await cancelled.ExecuteAsync(default), "AlreadyUsed");
        Assert.Empty(f.Engine.Mutations);
    }

    [Fact]
    public async Task TemplateEditsAndRemovalCannotChangeReviewedExecution()
    {
        foreach (var remove in new[] { false, true })
        {
            var f = new Fixture();
            var catalog = Templates(BasicYaml);
            var resolved = await Tools(f, catalog).ResolveAsync(Call(true, BasicYaml), default);
            if (remove) catalog.Clear();
            else catalog[0].ComposeYaml = BasicYaml.Replace("fixture", "changed");
            AssertKind(await resolved.ExecuteAsync(default), "Stale");
            AssertKind(await resolved.ExecuteAsync(default), "AlreadyUsed");
            Assert.Empty(f.Engine.Mutations);
            Assert.Empty(f.SavedSnapshots);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IncludedSourceDriftOrDisappearanceInvalidatesApproval(bool template, bool remove)
    {
        var directory = Directory.CreateTempSubdirectory("assistant-compose-contract-");
        try
        {
            var path = Path.Combine(directory.FullName, "included.yml");
            await File.WriteAllTextAsync(path, BasicYaml);
            var yaml = $"include: [{JsonSerializer.Serialize(path)}]\n";
            var f = new Fixture();
            var resolved = await Tools(f, Templates(yaml)).ResolveAsync(Call(template, yaml), default);
            Assert.Null(resolved.BlockedResult);
            if (remove) File.Delete(path);
            else await File.WriteAllTextAsync(path, BasicYaml.Replace("fixture", "changed"));
            AssertKind(await resolved.ExecuteAsync(default), remove ? "Blocked" : "Stale");
            AssertKind(await resolved.ExecuteAsync(default), "AlreadyUsed");
            Assert.Empty(f.Engine.Mutations);
            Assert.Empty(f.SavedSnapshots);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidParseAndPreflightPreserveSavedProjectAndTemplate(bool template)
    {
        foreach (var yaml in new[] { "services: [broken", "services:\n  web:\n    image: ${UNSET_ISSUE94_IMAGE}\n" })
        {
            var f = new Fixture();
            f.PersistDesired();
            var before = JsonSerializer.Serialize(f.SavedProject);
            var count = f.SavedSnapshots.Count;
            var catalog = Templates(yaml);
            var templateBefore = JsonSerializer.Serialize(catalog);
            var resolved = await Tools(f, catalog).ResolveAsync(Call(template, yaml), default);
            Assert.NotNull(resolved.BlockedResult);
            AssertKind(resolved.BlockedResult, "Blocked");
            Assert.Equal(before, JsonSerializer.Serialize(f.SavedProject));
            Assert.Equal(count, f.SavedSnapshots.Count);
            Assert.Equal(templateBefore, JsonSerializer.Serialize(catalog));
            Assert.Empty(f.Engine.Mutations);
        }
    }

    [Fact]
    public async Task OversizedConsequencesFailClosedRatherThanApproveTruncatedText()
    {
        var f = new Fixture();
        var yaml = "services:\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => $"  web{i}: {{image: fixture}}"));
        var resolved = await Tools(f).ResolveAsync(Call(false, yaml), default);
        Assert.NotNull(resolved.BlockedResult);
        Assert.Contains("display budget", resolved.Details);
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
    }

    [Fact]
    public async Task ReuseAndReplacementsReflectTheSharedAppliedSnapshot()
    {
        var f = new Fixture();
        var tools = Tools(f);
        var first = await tools.ResolveAsync(Call(false, BasicYaml), default);
        AssertKind(await first.ExecuteAsync(default), "Applied");
        f.Engine.Mutations.Clear();
        var reuse = await tools.ResolveAsync(Call(false, BasicYaml), default);
        Assert.Contains("Keep", reuse.Details);
        using var result = JsonDocument.Parse(await reuse.ExecuteAsync(default));
        Assert.Equal("reused", result.RootElement.GetProperty("outcomes")[0].GetProperty("status").GetString());
        Assert.Empty(f.Engine.Mutations);
        var replacement = await tools.ResolveAsync(Call(false, BasicYaml + "    command: changed\n"), default);
        Assert.Contains("Recreate", replacement.Details);
        Assert.Contains("writable", replacement.Details);
        Assert.Empty(f.Engine.Mutations);
        AssertKind(await replacement.ExecuteAsync(default), "Applied");
        Assert.Contains(f.Engine.Mutations, mutation => mutation.StartsWith("remove:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplicaCancellationPreservesStartedAndUnattemptedInstanceOutcomes()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Engine.AfterRun = _ => cancellation.Cancel();
        var resolved = await Tools(f).ResolveAsync(Call(false, BasicYaml + "    scale: 2\n"), default);
        Assert.Contains("web#2", resolved.Details);
        var output = await resolved.ExecuteAsync(cancellation.Token);
        AssertKind(output, "Cancelled");
        using var result = JsonDocument.Parse(output);
        var outcomes = result.RootElement.GetProperty("outcomes").EnumerateArray().ToList();
        Assert.Contains(outcomes, o => o.GetProperty("instance").GetString() == "web" &&
            o.GetProperty("status").GetString() == "started");
        Assert.Contains(outcomes, o => o.GetProperty("instance").GetString() == "web#2" &&
            o.GetProperty("status").GetString() == "cancelled");
        AssertKind(await resolved.ExecuteAsync(default), "AlreadyUsed");
    }

    [Fact]
    public async Task ExpiredAssistantClosureCannotApply()
    {
        var clock = new ReviewClock();
        var f = new Fixture(clock: clock);
        var resolved = await Tools(f).ResolveAsync(Call(false, BasicYaml), default);
        clock.Now = clock.Now.AddMinutes(10);
        AssertKind(await resolved.ExecuteAsync(default), "Expired");
        AssertKind(await resolved.ExecuteAsync(default), "AlreadyUsed");
        Assert.Empty(f.Engine.Mutations);
        Assert.Empty(f.SavedSnapshots);
    }

    [Fact]
    public async Task ResourceConflictsAndForeignReplacementAreBlocked()
    {
        foreach (var resource in new[] { true, false })
        {
            var f = new Fixture();
            if (resource) f.Engine.Networks["demo_default"] = "foreign-project";
            else f.Engine.Add(new() { Name = "demo_web", Image = "fixture" });
            var resolved = await Tools(f).ResolveAsync(Call(false, BasicYaml), default);
            Assert.NotNull(resolved.BlockedResult);
            AssertKind(resolved.BlockedResult, "Blocked");
            Assert.Empty(f.Engine.Mutations);
            Assert.Empty(f.SavedSnapshots);
        }
    }

    [Fact]
    public async Task RawEngineFailureNeverEntersOutcomeOrActivity()
    {
        var f = new Fixture();
        var h = Harness(f);
        string? output = null;
        h.Provider.Turns.Enqueue(async (invoke, ct) => output = await invoke(Call(false, BasicYaml), ct));
        var requested = Approval(h);
        var turn = h.Assistant.SendAsync("deploy");
        var approval = await requested.Task.WaitAsync(Deadline);
        f.Engine.ImageInventoryError = new IOException("unstructured synthetic-private");
        await h.Assistant.ApproveAsync(approval);
        await turn.WaitAsync(Deadline);
        AssertKind(output!, "Blocked");
        Assert.DoesNotContain("synthetic-private", output);
        Assert.All(h.PersistedActivity, json => Assert.DoesNotContain("synthetic-private", json));
        Assert.Empty(f.Engine.Mutations);
    }

    [Fact]
    public async Task FailedNativeCleanupReportsRetainedCandidateWithoutLegacyRetry()
    {
        var f = new Fixture(engine: new Engine { FailStart = "demo_web", FailRemove = "demo_web" });
        f.Snapshot = new("wslc.exe", "fixture", Enum.GetValues<WslcFeature>()
            .ToDictionary(feature => feature, _ => new WslcCapability(WslcCapabilitySupport.Supported, "fixture")));
        var resolved = await Tools(f).ResolveAsync(Call(false, NetworkYaml), default);
        var output = await resolved.ExecuteAsync(default);
        AssertKind(output, "PartialFailure");
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("failed", doc.RootElement.GetProperty("outcomes")[0].GetProperty("status").GetString());
        Assert.Contains(doc.RootElement.GetProperty("retainedResources").EnumerateArray(),
            r => r.GetProperty("Kind").GetString() == "container" &&
                r.GetProperty("Name").GetString() == "demo_web" &&
                r.GetProperty("State").GetString() == "unverified");
        Assert.Contains("demo_web", f.Engine.Containers.Keys);
        Assert.DoesNotContain(f.Engine.Mutations, m => m.StartsWith("run:", StringComparison.Ordinal));
        Assert.DoesNotContain("synthetic-private", output);
    }

    [Fact]
    public void BoundedComposeEvidenceKeepsFailureDiscriminatorAndCountsOmissions()
    {
        var json = JsonSerializer.Serialize(new
        {
            status = "partial", kind = "PartialFailure", allSucceeded = false,
            message = "Refresh actual state.",
            outcomes = Enumerable.Range(1, 1024).Select(i => new
                { instance = $"web#{i}", status = "failed", detail = new string('x', 100) }),
            retainedResources = Enumerable.Range(1, 100).Select(i => new
                { Kind = "volume", Name = $"data{i}", State = "unverified" }),
        });
        var safe = AiTextSanitizer.Sanitize(json);
        Assert.True(safe.Length <= AiTextSanitizer.EvidenceLimit);
        AssertKind(safe, "PartialFailure");
        using var doc = JsonDocument.Parse(safe);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("omittedOutcomes").GetInt32() > 0);
        Assert.True(doc.RootElement.GetProperty("omittedResources").GetInt32() > 0);
        Assert.NotEmpty(doc.RootElement.GetProperty("outcomes").EnumerateArray());
        Assert.DoesNotContain("<redacted>", doc.RootElement.GetProperty("outcomes")[0].GetProperty("instance").GetString());
    }

    [Fact]
    public async Task SchemasDoNotAcceptConfirmationOrExecutableReviewTokens()
    {
        var tools = Tools(new());
        foreach (var name in new[] { "deploy_compose", "deploy_template" })
        {
            var definitions = await tools.GetDefinitionsAsync(default);
            var definition = Assert.Single(definitions, d => d.Name == name);
            Assert.Contains("explicit approval", definition.Description);
            Assert.DoesNotContain("confirmed", definition.JsonSchemaParameters);
            var args = name == "deploy_compose" ? new Dictionary<string, object> { ["yaml"] = BasicYaml }
                : new Dictionary<string, object> { ["idOrName"] = "demo" };
            args["confirmed"] = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                tools.ResolveAsync(AiContractHarness.Call(name, JsonSerializer.Serialize(args)), default));
        }
    }

    private static void AssertKind(string output, string kind)
    {
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(kind, doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(kind == "Applied", doc.RootElement.GetProperty("allSucceeded").GetBoolean());
    }

    private sealed class ReviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TaskCompletionSource<AssistantApprovalRequest> Approval(AiContractHarness harness)
    {
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        harness.Assistant.ApprovalChanged += (_, a) => { if (a is not null) requested.TrySetResult(a); };
        return requested;
    }

    private static AiToolCall Call(bool template, string yaml) => AiContractHarness.Call(
        template ? "deploy_template" : "deploy_compose",
        template ? """{"idOrName":"demo"}""" : JsonSerializer.Serialize(new { yaml, projectName = "demo" }));

    private static List<StackTemplate> Templates(string yaml) =>
        [new() { Id = "demo", Name = "Demo", Category = "Tests", Description = "Synthetic",
            Kind = StackTemplateKind.Compose, ComposeYaml = yaml, ComposeProjectName = "demo" }];

    private static AiContractHarness Harness(Fixture f, List<StackTemplate>? templates = null)
    {
        var h = new AiContractHarness(settings => Tools(f, templates, settings));
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        return h;
    }

    private static AssistantToolset Tools(Fixture f, List<StackTemplate>? templates = null, ISettingsService? settings = null) =>
        new(f.Engine.Service,
            NetworkTestProxy.Create<IKubernetesService>((_, _) => Task.FromResult(new ClusterStatus { State = ClusterState.NotInstalled })),
            NetworkTestProxy.Create<ITemplateCatalog>((_, _) => templates ?? Templates(BasicYaml)),
            NetworkTestProxy.Create<IComposeProjectStore>((_, _) => throw new InvalidOperationException("Assistant must not save before review")),
            f.Supervisor,
            settings ?? NetworkTestProxy.Create<ISettingsService>((_, _) => new List<RegistryEntry>()),
            NetworkTestProxy.Create<IRegistryCatalogService>((_, _) => throw new InvalidOperationException("Unexpected registry access")));
}
