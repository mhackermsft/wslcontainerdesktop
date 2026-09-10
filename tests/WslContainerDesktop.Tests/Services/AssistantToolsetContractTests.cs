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

public sealed class AssistantToolsetContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly string[] LifecycleTools =
        ["start_container", "stop_container", "restart_container", "remove_container"];
    private static readonly string[] ArrayFields =
        ["ports", "environment", "volumes", "labels", "networks", "aliases", "dns",
            "dnsSearch", "dnsOptions", "tmpfs", "ulimits"];

    [Theory]
    [InlineData("inspect_container", false)]
    [InlineData("inspect_container", true)]
    [InlineData("get_container_logs", false)]
    [InlineData("get_container_logs", true)]
    public async Task RealReadToolsSanitizeSuccessAndFailureBeforeProviderCallback(string tool, bool failed)
    {
        var fixture = new Fixture
        {
            ReadResult = new CommandResult
            {
                ExitCode = failed ? 1 : 0,
                StandardOutput = """{"Config":{"Env":["PASSWORD=synthetic-private"]},"name":"ordinary-context"}""",
                StandardError = "ordinary-context password=\"synthetic-private\"",
            },
        };
        var h = new AiContractHarness(fixture.CreateTools);
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        h.Provider.Turns.Enqueue(async (invoke, ct) =>
        {
            var evidence = await invoke(AiContractHarness.Call(tool, """{"id":"app-one"}"""), ct);
            Assert.DoesNotContain("synthetic-private", evidence);
            Assert.Contains("ordinary-context", evidence);
            return evidence;
        });
        await h.Assistant.SendAsync("read the app").WaitAsync(Deadline);
        Assert.All(h.PersistedActivity, json => Assert.DoesNotContain("synthetic-private", json));
    }

    [Fact]
    public async Task RealRunApprovalAndAuditRedactEnvironmentButOriginalOptionsExecute()
    {
        var fixture = new Fixture();
        var h = new AiContractHarness(fixture.CreateTools);
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        var call = AiContractHarness.Call("run_container",
            """{"image":"nginx","environment":["PASSWORD=synthetic-private with spaces","MODE=production"]}""");
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(call, ct));
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, approval) =>
        {
            if (approval is not null) requested.TrySetResult(approval);
        };
        var turn = h.Assistant.SendAsync("run nginx");
        var approval = await requested.Task.WaitAsync(Deadline);
        Assert.Null(fixture.RunOptions);
        Assert.DoesNotContain("synthetic-private", approval.Details);
        Assert.Contains("MODE=production", approval.Details);
        await h.Assistant.ApproveAsync(approval);
        await turn.WaitAsync(Deadline);
        Assert.Contains("PASSWORD=synthetic-private with spaces", fixture.RunOptions!.EnvironmentVariables);
        Assert.All(h.PersistedActivity, json => Assert.DoesNotContain("synthetic-private", json));
    }

    [Fact]
    public async Task TerminalNormalizationNeverChangesApprovedExecutionValues()
    {
        var fixture = new Fixture();
        const string command = "printf '\u001b[32mordinary-context\u001b[0m'";
        var plan = await fixture.Resolve("run_container",
            JsonSerializer.Serialize(new { image = "nginx", command }));
        Assert.DoesNotContain("\\u001B", plan.Details, StringComparison.OrdinalIgnoreCase);
        await plan.ExecuteAsync(CancellationToken.None);
        Assert.Equal(command, fixture.RunOptions!.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredPartialOutcomesRedactCommandAndExceptionDetails(bool throws)
    {
        var fixture = new Fixture
        {
            Inventory = [Container("one", "app-one"), Container("two", "app-two"), Container("three", "app-three")],
            Mutate = (id, _) => id == "one" ? Task.FromResult(new CommandResult())
                : throws ? Task.FromException<CommandResult>(new IOException("password=synthetic-private"))
                : Task.FromResult(new CommandResult { ExitCode = 1, StandardError = """{"password":"synthetic-private","reason":"ordinary-context"}""" }),
        };
        var plan = await fixture.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "partial");
        Assert.DoesNotContain("synthetic-private", result);
        Assert.Contains("app-one", result);
        Assert.Contains(throws ? "unknown" : "failed", result);
        Assert.Contains(throws ? "not_run" : "ordinary-context", result);
    }

    [Fact]
    public void CommandSummaryRedactsBeforeFormerFourThousandCharacterCut()
    {
        var result = AssistantToolset.Summarize(new CommandResult
        {
            StandardOutput = "ordinary-context\nPASSWORD=\"" + new string('s', 5000) + "synthetic-private\"\n" + new string('x', 5000),
        });
        Assert.DoesNotContain("synthetic-private", result);
        Assert.DoesNotContain(new string('s', 20), result);
        Assert.Contains("ordinary-context", result);
        Assert.True(result.Length <= 4000);
    }

    [Fact]
    public async Task OversizedPartialResultPreservesTargetIdentityAndStatusWhileBoundingDetails()
    {
        var fixture = new Fixture
        {
            Inventory = Enumerable.Range(1, 4).Select(i => Container($"id-{i}", $"app-{i}")).ToArray(),
            Mutate = (id, _) => Task.FromResult(new CommandResult
            {
                ExitCode = id == "id-4" ? 1 : 0,
                StandardOutput = new string('x', 5000),
                StandardError = "password=synthetic-private\n" + new string('x', 5000),
            }),
        };
        var plan = await fixture.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        Assert.True(result.Length <= AiTextSanitizer.EvidenceLimit);
        AssertStatus(result, "partial");
        using var document = JsonDocument.Parse(result);
        var outcomes = document.RootElement.GetProperty("outcomes");
        Assert.Equal(4, outcomes.GetArrayLength());
        Assert.Equal("id-4", outcomes[3].GetProperty("Id").GetString());
        Assert.Equal("failed", outcomes[3].GetProperty("Status").GetString());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("omittedOutcomes").GetInt32());
        Assert.DoesNotContain("synthetic-private", result);
    }

    public static IEnumerable<object[]> InvalidArguments()
    {
        foreach (var tool in LifecycleTools.Concat(["stop_all_containers", "remove_all_containers", "run_container"]))
        {
            foreach (var json in new[] { "", " ", "{", "null", "[]", "\"text\"", "1", "true", "{}",
                         """{"unexpected":true}""" })
                yield return [tool, json];
        }
        foreach (var tool in LifecycleTools.Concat(["inspect_container", "get_container_logs"]))
            foreach (var value in new[] { "null", "true", "42", "[]", "{}", "\"\"", "\"  \"" })
                yield return [tool, $$"""{"id":{{value}}}"""];
        foreach (var tool in new[] { "stop_all_containers", "remove_all_containers" })
        {
            foreach (var json in new[]
            {
                """{"scope":"everything"}""", """{"scope":null}""", """{"scope":true}""",
                """{"scope":"ALL"}""", """{"scope":"all","namePrefix":"app"}""",
                """{"scope":"all","nameContains":"app"}""", """{"namePrefix":""}""",
                """{"nameContains":" "}""", """{"namePrefix":null}""", """{"nameContains":3}""",
                """{"namePrefix":"app","namePrefix":"other"}""", """{"scope":"all","typo":false}""",
                """{"scope":"all","scope":"all"}"""
            })
                yield return [tool, json];
        }
        foreach (var value in new[] { "null", "\"false\"", "0", "[]", "{}" })
            yield return ["remove_all_containers", $$"""{"scope":"all","onlyRunning":{{value}}}"""];
        yield return ["remove_all_containers", """{"onlyRunning":false}"""];
        yield return ["stop_all_containers", """{"scope":"all","onlyRunning":true}"""];
        foreach (var field in ArrayFields)
            foreach (var value in new[] { "null", "\"value\"", "{}", "true", "3", "[null]", "[1]", "[true]", "[\"\"]", "[\" \"]", "[\"valid\",null]" })
                yield return ["run_container", $$"""{"image":"image:v1","{{field}}":{{value}}}"""];
        foreach (var field in new[] { "name", "command", "entrypoint", "user", "workingDir", "hostname",
                     "domainname", "cpuLimit", "memoryLimit", "shmSize", "stopSignal", "image" })
            foreach (var value in new[] { "null", "false", "17", "[]", "\"\"", "\" \"" })
                yield return ["run_container", $$"""{"{{field}}":{{value}}{{(field == "image" ? "" : ",\"image\":\"image:v1\"")}}}"""];
        foreach (var field in new[] { "gpus", "removeOnExit" })
            foreach (var value in new[] { "null", "\"true\"", "1", "[]" })
                yield return ["run_container", $$"""{"image":"image:v1","{{field}}":{{value}}}"""];
        foreach (var field in new[] { "environment", "labels" })
            foreach (var value in new[] { "[\"missing-separator\"]", "[\"=value\"]",
                         "[\" =value\"]", "[\"KEY=first\",\"KEY=second\"]", "[\"KEY=first\",\" KEY=second\"]" })
                yield return ["run_container", $$"""{"image":"image:v1","{{field}}":{{value}}}"""];
        foreach (var value in new[] { "0", "-1", "1,5", "NaN", "Infinity", "unlimited" })
            yield return ["run_container", $$"""{"image":"image:v1","cpuLimit":"{{value}}"}"""];
        foreach (var tool in new[] { "get_container_logs", "get_k8s_logs" })
            foreach (var value in new[] { "0", "-1", "1001", "1.5", "2147483648", "null", "\"200\"", "true" })
                yield return [tool, $$"""{"{{(tool == "get_container_logs" ? "id" : "name")}}":"app","tail":{{value}}}"""];
        foreach (var value in new[] { "-1", "101", "0.5", "2147483648", "null", "\"1\"", "false" })
            yield return ["scale_deployment", $$"""{"name":"app","replicas":{{value}}}"""];
        foreach (var (tool, field) in new[]
        {
            ("pull_image", "reference"), ("deploy_template", "idOrName"),
            ("create_volume", "name"), ("remove_volume", "name"), ("create_network", "name"),
            ("remove_network", "name"), ("apply_yaml", "yaml"), ("deploy_compose", "yaml"),
            ("list_registry_repositories", "registry"), ("list_registry_tags", "repository"),
            ("list_k8s_resources", "kind"), ("restart_deployment", "name"), ("delete_resource", "name")
        })
            foreach (var value in new[] { "null", "\"\"", "\"  \"", "false", "123", "[]" })
                yield return [tool, $$"""{"{{field}}":{{value}}}"""];
        yield return ["stop_container", """{"id":"app","id":"different"}"""];
        yield return ["stop_container", """{"Id":"app"}"""];
        yield return ["list_containers", """{"unexpected":true}"""];
        yield return ["get_k8s_logs", """{"name":"app","namespace":""}"""];
        yield return ["get_k8s_logs", """{"name":"app","namespace":null}"""];
        yield return ["restart_deployment", """{"name":"app","namespace":""}"""];
        yield return ["scale_deployment", """{"name":"app","replicas":1,"namespace":""}"""];
        yield return ["list_k8s_resources", """{"kind":"pods","namespace":""}"""];
        yield return ["list_k8s_resources", """{"kind":"unsupported"}"""];
        yield return ["delete_resource", """{"kind":"pods","name":"app","namespace":" "}"""];
        yield return ["deploy_compose", """{"yaml":"services: {}"}"""];
        yield return ["unknown_tool", "{}"];
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidArgumentsFailBeforeInventoryOrMutation(string tool, string arguments)
    {
        var h = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Resolve(tool, arguments));
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData("stop_all_containers", """{"scope":"all"}""")]
    [InlineData("remove_all_containers", """{"scope":"all","onlyRunning":false}""")]
    [InlineData("stop_all_containers", """{"namePrefix":"app"}""")]
    [InlineData("remove_all_containers", """{"nameContains":"pp"}""")]
    [InlineData("remove_all_containers", """{"namePrefix":"app","nameContains":"one","onlyRunning":true}""")]
    public async Task ExplicitAllOrNonblankFiltersResolveWithoutMutation(string tool, string arguments)
    {
        var h = new Fixture();
        var plan = await h.Resolve(tool, arguments);
        Assert.Contains("app-one", plan.Details);
        Assert.Contains("immutable-one", plan.Details);
        Assert.Equal(["list"], h.Calls);
    }

    [Theory]
    [InlineData("get_container_logs", """{"id":"app","tail":1}""")]
    [InlineData("get_container_logs", """{"id":"app","tail":1000}""")]
    [InlineData("get_k8s_logs", """{"name":"app","tail":1}""")]
    [InlineData("get_k8s_logs", """{"name":"app","tail":1000}""")]
    [InlineData("scale_deployment", """{"name":"app","replicas":0}""")]
    [InlineData("scale_deployment", """{"name":"app","replicas":100}""")]
    [InlineData("delete_resource", """{"kind":"namespace","name":"app","namespace":""}""")]
    public async Task SupportedBoundsResolveWithoutSideEffects(string tool, string arguments)
    {
        var h = new Fixture();
        await h.Resolve(tool, arguments);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task AllSupportedRunFieldsAreCapturedAndExecutedUnchanged()
    {
        var h = new Fixture();
        var plan = await h.Resolve("run_container", """
            {"image":"image:v1","name":"app","command":"echo hello","entrypoint":"/bin/sh",
             "ports":["8080:80"],"environment":["KEY=value"],"volumes":["data:/data"],
             "labels":["owner=synthetic"],"networks":["private"],"aliases":["alias"],
             "dns":["1.1.1.1"],"dnsSearch":["example.invalid"],"dnsOptions":["ndots:1"],
             "tmpfs":["/cache"],"ulimits":["nofile=1024:2048"],"gpus":true,"removeOnExit":true,
             "user":"1000","workingDir":"/work","hostname":"host","domainname":"example.invalid",
             "cpuLimit":"1.5","memoryLimit":"512M","shmSize":"64M","stopSignal":"SIGTERM"}
            """);
        Assert.Empty(h.Calls);
        await plan.ExecuteAsync(CancellationToken.None);
        var options = Assert.IsType<RunContainerOptions>(h.RunOptions);
        Assert.Equal("image:v1", options.Image);
        Assert.Equal("app", options.Name);
        Assert.Equal("echo hello", options.Command);
        Assert.Equal("/bin/sh", options.Entrypoint);
        Assert.Equal(["8080:80"], options.PortMappings);
        Assert.Equal(["KEY=value"], options.EnvironmentVariables);
        Assert.Equal(["data:/data"], options.Volumes);
        Assert.Equal("synthetic", options.Labels["owner"]);
        Assert.Equal(["private"], options.Networks);
        Assert.Equal(["alias"], options.Aliases);
        Assert.Equal(["1.1.1.1"], options.Dns);
        Assert.Equal(["example.invalid"], options.DnsSearch);
        Assert.Equal(["ndots:1"], options.DnsOptions);
        Assert.Equal(["/cache"], options.Tmpfs);
        Assert.Equal(["nofile=1024:2048"], options.Ulimits);
        Assert.True(options.AllGpus);
        Assert.True(options.RemoveOnExit);
        Assert.Equal("1000", options.User);
        Assert.Equal("/work", options.WorkingDir);
        Assert.Equal("host", options.Hostname);
        Assert.Equal("example.invalid", options.Domainname);
        Assert.Equal("1.5", options.CpuLimit);
        Assert.Equal("512M", options.MemoryLimit);
        Assert.Equal("64M", options.ShmSize);
        Assert.Equal("SIGTERM", options.StopSignal);
    }

    [Theory]
    [InlineData("start_container")]
    [InlineData("stop_container")]
    [InlineData("restart_container")]
    [InlineData("remove_container")]
    public async Task SingleNameResolvesToImmutableIdAndRechecksBeforeMutation(string tool)
    {
        var h = new Fixture();
        var plan = await h.Resolve(tool, """{"id":"app-one"}""");
        Assert.Contains("immutable-one", plan.Details);
        Assert.Contains("app-one", plan.Details);
        var result = await plan.ExecuteAsync(CancellationToken.None);
        Assert.Equal(["list", "list", $"{tool}:immutable-one"], h.Calls);
        AssertStatus(result, "succeeded");
    }

    [Fact]
    public async Task ExactIdCanPrepareAndExecuteWithoutUsingName()
    {
        var h = new Fixture();
        var plan = await h.Resolve("restart_container", """{"id":"immutable-one"}""");
        AssertStatus(await plan.ExecuteAsync(CancellationToken.None), "succeeded");
        Assert.Equal(["list", "list", "restart_container:immutable-one"], h.Calls);
    }

    [Fact]
    public async Task UniqueHexIdPrefixFreezesFullInventoryIdBeforeApproval()
    {
        var h = new Fixture { Inventory = [Container("0123456789abcdef0123456789abcdef", "app-one")] };
        var plan = await h.Resolve("remove_container", """{"id":"0123456789ab"}""");
        Assert.Contains("0123456789abcdef0123456789abcdef", plan.Details);
        h.Inventory = [.. h.Inventory, Container("0123456789abffffffffffffffffffff", "app-two")];
        AssertStatus(await plan.ExecuteAsync(CancellationToken.None), "succeeded");
        Assert.Equal(["list", "list", "remove_container:0123456789abcdef0123456789abcdef"], h.Calls);
    }

    [Theory]
    [InlineData("0123456789a")]
    [InlineData("0123456789ab")]
    public async Task ShortOrAmbiguousHexPrefixesNeverPrepareMutation(string prefix)
    {
        var h = new Fixture
        {
            Inventory = [Container("0123456789abcdef0123456789abcdef", "app-one"),
                Container("0123456789abffffffffffffffffffff", "app-two")],
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Resolve("remove_container", JsonSerializer.Serialize(new { id = prefix })));
        Assert.Equal(["list"], h.Calls);
    }

    [Fact]
    public async Task IdAndDifferentContainersNameCollisionIsAmbiguous()
    {
        var h = new Fixture { Inventory = [Container("one", "first"), Container("two", "one")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Resolve("remove_container", """{"id":"one"}"""));
        Assert.Equal(["list"], h.Calls);
    }

    [Fact]
    public async Task ResolutionInventoryFailureIsNotAnEmptySuccessfulPlan()
    {
        var h = new Fixture { BeforeList = _ => throw new IOException("inventory unavailable") };
        await Assert.ThrowsAsync<IOException>(() => h.Resolve("stop_all_containers", """{"scope":"all"}"""));
        Assert.Equal(["list"], h.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task MissingOrAmbiguousNamesNeverPrepareAMutation(string scenario)
    {
        var h = new Fixture();
        h.Inventory = scenario == "missing" ? [] : [Container("first", "app-one"), Container("second", "app-one")];
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Resolve("stop_container", """{"id":"app-one"}"""));
        Assert.Equal(["list"], h.Calls);
    }

    public static IEnumerable<object[]> IdentityChanges()
    {
        foreach (var tool in LifecycleTools)
            foreach (var change in new[] { "missing", "replacement", "name", "image", "created", "known", "duplicate" })
                yield return [tool, change];
    }

    [Theory]
    [MemberData(nameof(IdentityChanges))]
    public async Task ChangedIdentityIsSkippedRatherThanRetargeted(string tool, string change)
    {
        var h = new Fixture();
        var plan = await h.Resolve(tool, """{"id":"app-one"}""");
        var original = h.Inventory[0];
        switch (change)
        {
            case "missing": h.Inventory = []; break;
            case "replacement": h.Inventory = [Container("new-id", "app-one")]; break;
            case "name": original.Name = "renamed"; break;
            case "image": original.Image = "other:v2"; break;
            case "created": original.CreatedAt++; break;
            case "known": original.CreatedAtKnown = false; break;
            case "duplicate": h.Inventory = [original, Container(original.Id, original.Name)]; break;
        }
        var result = await plan.ExecuteAsync(CancellationToken.None);
        Assert.Equal(["list", "list"], h.Calls);
        Assert.Contains("skipped", result);
        Assert.DoesNotContain("succeeded", result);
    }

    [Fact]
    public async Task BulkSnapshotIgnoresNewMatchesAndRechecksEveryOriginalTarget()
    {
        var h = new Fixture { Inventory = [Container("one", "app-one"), Container("two", "app-two")] };
        var plan = await h.Resolve("remove_all_containers", """{"namePrefix":"app","onlyRunning":false}""");
        h.Inventory = [.. h.Inventory, Container("new", "app-new")];
        h.Mutate = (_, _) =>
        {
            h.Inventory = [h.Inventory[0], Container("replacement", "app-two"), h.Inventory[2]];
            return Task.FromResult(new CommandResult());
        };
        var result = await plan.ExecuteAsync(CancellationToken.None);
        Assert.Equal(["list", "list", "remove_container:one", "list"], h.Calls);
        AssertStatus(result, "partial");
        Assert.Contains("two", result);
        Assert.Contains("skipped", result);
        Assert.DoesNotContain("app-new", result);
    }

    [Theory]
    [InlineData("stop_all_containers", """{"scope":"all"}""")]
    [InlineData("remove_all_containers", """{"scope":"all"}""")]
    [InlineData("stop_container", """{"id":"app-one"}""")]
    public async Task RunningOnlyPlanSkipsTargetsStoppedSinceApproval(string tool, string arguments)
    {
        var h = new Fixture();
        var plan = await h.Resolve(tool, arguments);
        h.Inventory[0].StateValue = (int)ContainerState.Stopped;
        var result = await plan.ExecuteAsync(CancellationToken.None);
        Assert.Contains("skipped", result);
        Assert.Equal(["list", "list"], h.Calls);
    }

    [Fact]
    public async Task EmptySnapshotStaysEmptyEvenWhenMatchingContainerAppears()
    {
        var h = new Fixture { Inventory = [] };
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        h.Inventory = [Container("new", "app-new")];
        AssertStatus(await plan.ExecuteAsync(CancellationToken.None), "no_targets");
        Assert.Equal(["list"], h.Calls);
    }

    [Fact]
    public async Task ExplicitRemoveIncludingStoppedDoesNotAcquireRunningOnlyRestriction()
    {
        var h = new Fixture();
        h.Inventory[0].StateValue = (int)ContainerState.Stopped;
        var plan = await h.Resolve("remove_all_containers", """{"scope":"all","onlyRunning":false}""");
        AssertStatus(await plan.ExecuteAsync(CancellationToken.None), "succeeded");
        Assert.Equal(["list", "list", "remove_container:immutable-one"], h.Calls);
    }

    [Fact]
    public async Task EveryNormalCommandFailureReportsFailedRatherThanSuccess()
    {
        var h = new Fixture
        {
            Mutate = (_, _) => Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "refused" }),
        };
        var plan = await h.Resolve("remove_container", """{"id":"app-one"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "failed");
        Assert.Contains("refused", result);
        Assert.Equal(["list", "list", "remove_container:immutable-one"], h.Calls);
    }

    [Fact]
    public async Task NormalCommandFailureContinuesAndReportsHonestPartialOutcomesWithoutRetry()
    {
        var h = new Fixture { Inventory = [Container("one", "app-one"), Container("two", "app-two"), Container("three", "app-three")] };
        h.Mutate = (id, _) => Task.FromResult(new CommandResult
        {
            ExitCode = id == "two" ? 9 : 0,
            StandardError = id == "two" ? "synthetic failure" : "",
        });
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "partial");
        Assert.Contains("failed", result);
        Assert.Contains("synthetic failure", result);
        Assert.Equal(["list", "list", "stop_container:one", "list", "stop_container:two", "list", "stop_container:three"], h.Calls);
    }

    [Fact]
    public async Task InventoryFailureAfterSuccessStopsAllLaterMutations()
    {
        var h = new Fixture { Inventory = [Container("one", "app-one"), Container("two", "app-two"), Container("three", "app-three")] };
        h.BeforeList = count => { if (count == 3) throw new IOException("inventory unavailable"); };
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "partial");
        Assert.Contains("not_run", result);
        Assert.Contains("inventory unavailable", result);
        Assert.Contains("three", result);
        Assert.Equal(["list", "list", "stop_container:one", "list"], h.Calls);
    }

    [Fact]
    public async Task FirstExecutionInventoryFailureReportsFailedWithEveryTargetNotRun()
    {
        var h = new Fixture { Inventory = [Container("one", "app-one"), Container("two", "app-two")] };
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        h.BeforeList = _ => throw new IOException("inventory unavailable");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "failed");
        using var document = JsonDocument.Parse(result);
        var outcomes = document.RootElement.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.All(outcomes, outcome => Assert.Equal("not_run", outcome.GetProperty("Status").GetString()));
        Assert.Equal(["list", "list"], h.Calls);
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("io")]
    [InlineData("win32")]
    [InlineData("json")]
    [InlineData("timeout")]
    public async Task MutationExceptionReportsUncertaintyAndStopsRemainingTargets(string kind)
    {
        Exception failure = kind switch
        {
            "invalid-operation" => new InvalidOperationException("synthetic operation failure"),
            "io" => new IOException("synthetic I/O failure"),
            "win32" => new System.ComponentModel.Win32Exception("synthetic process failure"),
            "json" => new JsonException("synthetic JSON failure"),
            _ => new TimeoutException("synthetic timeout"),
        };
        var h = new Fixture
        {
            Inventory = [Container("one", "app-one"), Container("two", "app-two")],
            Mutate = (_, _) => Task.FromException<CommandResult>(failure),
        };
        var plan = await h.Resolve("remove_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "failed");
        using var document = JsonDocument.Parse(result);
        var outcomes = document.RootElement.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(["unknown", "not_run"], outcomes.Select(outcome => outcome.GetProperty("Status").GetString()));
        Assert.Equal(["list", "list", "remove_container:one"], h.Calls);
    }

    [Fact]
    public async Task CancellationBeforeFirstMutationThrowsWithoutMutation()
    {
        var h = new Fixture();
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        using var cancellation = new CancellationTokenSource();
        h.BeforeList = count => { if (count == 2) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.ExecuteAsync(cancellation.Token));
        Assert.Equal(["list", "list"], h.Calls);
    }

    [Fact]
    public async Task CancellationDuringFirstMutationReportsUnknownWithoutRetry()
    {
        var h = new Fixture { Inventory = [Container("one", "app-one"), Container("two", "app-two")] };
        using var cancellation = new CancellationTokenSource();
        h.Mutate = (_, ct) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<CommandResult>(ct);
        };
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(cancellation.Token);
        AssertStatus(result, "cancelled");
        Assert.Contains("unknown", result);
        Assert.Contains("not_run", result);
        Assert.DoesNotContain("succeeded", result);
        Assert.Equal(["list", "list", "stop_container:one"], h.Calls);
    }

    [Fact]
    public async Task IndependentlyCancelledMutationReportsUnknownAndStopsRemainingTargets()
    {
        var h = new Fixture
        {
            Inventory = [Container("one", "app-one"), Container("two", "app-two")],
            Mutate = (_, _) => Task.FromException<CommandResult>(new OperationCanceledException("internal cancellation")),
        };
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(CancellationToken.None);
        AssertStatus(result, "cancelled");
        Assert.Contains("unknown", result);
        Assert.Contains("not_run", result);
        Assert.DoesNotContain("succeeded", result);
        Assert.Equal(["list", "list", "stop_container:one"], h.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterPartialMutationPreservesCompletedAndUnattemptedOutcomes(bool duringCall)
    {
        var h = new Fixture { Inventory = [Container("one", "app-one"), Container("two", "app-two"), Container("three", "app-three")] };
        using var cancellation = new CancellationTokenSource();
        h.Mutate = (id, ct) =>
        {
            if ((!duringCall && id == "one") || (duringCall && id == "two"))
            {
                cancellation.Cancel();
                if (duringCall) return Task.FromCanceled<CommandResult>(ct);
            }
            return Task.FromResult(new CommandResult());
        };
        var plan = await h.Resolve("stop_all_containers", """{"scope":"all"}""");
        var result = await plan.ExecuteAsync(cancellation.Token);
        AssertStatus(result, "cancelled");
        Assert.Contains("succeeded", result);
        Assert.Contains("not_run", result);
        Assert.Contains("three", result);
        if (duringCall) Assert.Contains("unknown", result);
        Assert.Equal(duringCall
            ? ["list", "list", "stop_container:one", "list", "stop_container:two"]
            : new[] { "list", "list", "stop_container:one" }, h.Calls);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("cancel")]
    [InlineData("reset")]
    public async Task RealToolsetApprovalRejectionAndCancellationNeverExecute(string action)
    {
        var fixture = new Fixture();
        var h = new AiContractHarness(fixture.CreateTools);
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call("stop_container", """{"id":"app-one"}"""), ct));
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, approval) =>
        {
            if (approval is not null) requested.TrySetResult(approval);
        };
        using var cancellation = new CancellationTokenSource();
        var turn = h.Assistant.SendAsync("stop the synthetic app", cancellation.Token);
        var approval = await requested.Task.WaitAsync(Deadline);
        Assert.Contains("app-one", approval.Details);
        Assert.Contains("immutable-one", approval.Details);
        Assert.Equal(["list"], fixture.Calls);
        if (action == "reject")
        {
            await h.Assistant.RejectAsync(approval);
            Assert.Contains("rejected", Assert.Single((await turn.WaitAsync(Deadline)).Messages).Text);
        }
        else
        {
            if (action == "reset") h.Assistant.Reset();
            else cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(Deadline));
        }
        await h.Assistant.ApproveAsync(approval);
        Assert.Equal(["list"], fixture.Calls);
    }

    [Theory]
    [InlineData("stop_all_containers", "{}")]
    [InlineData("remove_all_containers", """{"scope":"all","onlyRunning":"false"}""")]
    [InlineData("stop_container", "{malformed")]
    [InlineData("run_container", """{"image":"image:v1","ports":[null]}""")]
    [InlineData("deploy_compose", """{"yaml":"services: {}"}""")]
    public async Task AutoApprovalCannotBypassRealArgumentValidation(string tool, string arguments)
    {
        var fixture = new Fixture();
        var h = new AiContractHarness(fixture.CreateTools);
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        h.AutoApproved.Add(tool);
        h.Assistant.ApprovalChanged += (_, approval) => Assert.Null(approval);
        h.Provider.Turns.Enqueue((invoke, ct) => invoke(AiContractHarness.Call(tool, arguments), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Assistant.SendAsync("invalid").WaitAsync(Deadline));
        Assert.Empty(fixture.Calls);
        Assert.Empty(h.Activity);
    }

    [Fact]
    public async Task DefinitionProbePropagatesCallerCancellationInsteadOfHidingKubernetesTools()
    {
        var fixture = new Fixture();
        var harness = new AiContractHarness();
        harness.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        using var cancellation = new CancellationTokenSource();
        fixture.GetStatus = ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ClusterStatus>(ct);
        };
        var tools = fixture.CreateTools(harness.Settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.GetDefinitionsAsync(cancellation.Token));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task RealApprovalExecutesOnlyOriginalSnapshotAndCannotBeReplayed()
    {
        var fixture = new Fixture();
        var h = new AiContractHarness(fixture.CreateTools);
        h.SettingsValues[nameof(ISettingsService.Registries)] = new List<RegistryEntry>();
        h.Provider.Turns.Enqueue((invoke, ct) =>
            invoke(AiContractHarness.Call("stop_all_containers", """{"scope":"all"}"""), ct));
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, approval) =>
        {
            if (approval is not null) requested.TrySetResult(approval);
        };
        var turn = h.Assistant.SendAsync("stop the synthetic apps");
        var approval = await requested.Task.WaitAsync(Deadline);
        Assert.Contains("immutable-one", approval.Details);
        fixture.Inventory = [.. fixture.Inventory, Container("new-id", "app-new")];
        await h.Assistant.ApproveAsync(approval);
        var result = await turn.WaitAsync(Deadline);
        AssertStatus(Assert.Single(result.Messages).Text, "succeeded");
        await h.Assistant.ApproveAsync(approval);
        Assert.Equal(["list", "list", "stop_container:immutable-one"], fixture.Calls);
    }

    private static void AssertStatus(string result, string expected)
    {
        using var document = JsonDocument.Parse(result);
        Assert.Equal(expected, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("outcomes").ValueKind);
    }

    private static ContainerInfo Container(string id, string name) => new()
    {
        Id = id, Name = name, Image = "image:v1", CreatedAt = 123456,
        CreatedAtKnown = true, StateValue = (int)ContainerState.Running,
    };

    private sealed class Fixture
    {
        public IReadOnlyList<ContainerInfo> Inventory { get; set; } = [Container("immutable-one", "app-one")];
        public List<string> Calls { get; } = [];
        public Action<int>? BeforeList { get; set; }
        public Func<string, CancellationToken, Task<CommandResult>> Mutate { get; set; } =
            (_, _) => Task.FromResult(new CommandResult());
        public Func<CancellationToken, Task<ClusterStatus>> GetStatus { get; set; } =
            _ => Task.FromResult(new ClusterStatus { State = ClusterState.NotInstalled });
        public RunContainerOptions? RunOptions { get; private set; }
        public CommandResult ReadResult { get; set; } = new();
        private int listCount;
        private AssistantToolset? tools;

        public Task<AssistantResolvedToolCall> Resolve(string name, string arguments) =>
            (tools ??= CreateTools(Strict<ISettingsService>())).ResolveAsync(AiContractHarness.Call(name, arguments), CancellationToken.None);

        public AssistantToolset CreateTools(ISettingsService settings)
        {
            var wslc = NetworkTestProxy.Create<IWslcService>((method, args) =>
            {
                if (method.Name == nameof(IWslcService.ListContainersAsync))
                {
                    Assert.True((bool)args[0]!);
                    Calls.Add("list");
                    listCount++;
                    BeforeList?.Invoke(listCount);
                    return Task.FromResult(Inventory);
                }
                if (method.Name == nameof(IWslcService.RunContainerAsync))
                {
                    Calls.Add("run_container");
                    RunOptions = (RunContainerOptions)args[0]!;
                    return Task.FromResult(new CommandResult());
                }
                if (method.Name is nameof(IWslcService.InspectContainerAsync) or nameof(IWslcService.GetLogsAsync))
                {
                    Calls.Add(method.Name);
                    return Task.FromResult(ReadResult);
                }
                var tool = method.Name switch
                {
                    nameof(IWslcService.StartContainerAsync) => "start_container",
                    nameof(IWslcService.StopContainerAsync) => "stop_container",
                    nameof(IWslcService.RestartContainerAsync) => "restart_container",
                    nameof(IWslcService.RemoveContainerAsync) => "remove_container",
                    _ => throw new InvalidOperationException($"Unexpected service call: {method.Name}"),
                };
                var id = (string)args[0]!;
                if (tool == "remove_container") Assert.True((bool)args[1]!);
                Calls.Add($"{tool}:{id}");
                return Mutate(id, args.OfType<CancellationToken>().Single());
            });
            var kubernetes = NetworkTestProxy.Create<IKubernetesService>((method, args) =>
                method.Name == nameof(IKubernetesService.GetStatusAsync)
                    ? GetStatus((CancellationToken)args[0]!)
                    : throw new InvalidOperationException($"Unexpected Kubernetes call: {method.Name}"));
            var templates = NetworkTestProxy.Create<ITemplateCatalog>((method, _) =>
                method.Name == "get_Templates" ? Array.Empty<StackTemplate>()
                    : throw new InvalidOperationException($"Unexpected template call: {method.Name}"));
            return new(wslc, kubernetes, templates, Strict<IComposeProjectStore>(), null!, settings,
                Strict<IRegistryCatalogService>());
        }

        private static T Strict<T>() where T : class => NetworkTestProxy.Create<T>((method, _) =>
            throw new InvalidOperationException($"Unexpected {typeof(T).Name} call: {method.Name}"));
    }
}
