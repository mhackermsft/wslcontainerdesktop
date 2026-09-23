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

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class DevContainerLifecycleTests
{
    public static IEnumerable<object[]> UnsupportedHostWorkspaces()
    {
        yield return [@"\\server\share\workspace"];
        yield return ["//server/share/workspace"];
        yield return [@"\\?\C:\workspace"];
        yield return [@"\\.\C:\workspace"];
        yield return [@"\\?\UNC\server\share\workspace"];
        yield return [@"C:\" + new string('a', 257)];
    }

    [Theory]
    [MemberData(nameof(UnsupportedHostWorkspaces))]
    public async Task RejectsWorkspaceFormsCmdCannotHonorBeforeApprovalOrMutation(string workspace)
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.WorkspacePath = workspace;
        fixture.Config.Lifecycle.Initialize = ["cd"];
        var reviews = 0;

        var result = await fixture.Up(approveHostCommandsAsync: (_, _) =>
        {
            reviews++;
            return Task.FromResult(true);
        });

        Assert.False(result.Success);
        Assert.Contains("not supported by cmd.exe", result.Detail);
        Assert.Equal(0, reviews);
        Assert.Empty(fixture.HostProcesses);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Compose.Engine.Mutations);
    }

    [Fact]
    public async Task WorkspaceRestrictionsDoNotApplyWhenThereAreNoHostCommands()
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.WorkspacePath = @"\\server\share\workspace";
        fixture.Config.Lifecycle.Initialize = [];

        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.HostProcesses);
    }

    [Theory]
    [InlineData("echo \u202Etxt\u202C", @"echo \u202Etxt\u202C")]
    [InlineData("echo \u2066a\u2067b\u2068c\u2069", @"echo \u2066a\u2067b\u2068c\u2069")]
    [InlineData("\u061C\u200E\u200F\u202A\u202B\u202D", @"\u061C\u200E\u200F\u202A\u202B\u202D")]
    [InlineData("zero\u200Bwidth\u200C\u200D\uFEFF", @"zero\u200Bwidth\u200C\u200D\uFEFF")]
    [InlineData("a\0\b\t\u001B\u007F\u0085b", @"a\u0000\u0008\u0009\u001B\u007F\u0085b")]
    [InlineData("a\rb\u2028c\u2029d", @"a\u000Db\u2028c\u2029d")]
    [InlineData("echo \U000E0001", @"echo \U000E0001")]
    [InlineData(@"echo \u202E", @"echo \\u202E")]
    [InlineData("echo café \U0001F680", "echo café \U0001F680")]
    public void HostReviewDisplayExposesInvisibleCharactersWithoutAmbiguousLiteralEscapes(string input, string expected)
    {
        Assert.Equal(expected, DevContainerHostCommandReview.EscapeForDisplay(input));
    }

    [Fact]
    public void HostReviewDisplayPreservesMultilineScriptsAndExposesUnpairedSurrogates()
    {
        Assert.Equal("first\r\nsecond\nthird\r\n", DevContainerHostCommandReview.EscapeForDisplay("first\r\nsecond\nthird\r\n"));
        Assert.Equal(@"\uD800x\uDC00", DevContainerHostCommandReview.EscapeForDisplay("\uD800x\uDC00"));
    }

    [Fact]
    public async Task EscapedHostReviewDisplayDoesNotChangeOriginalExecutionSnapshot()
    {
        var fixture = Fixture.SingleContainer();
        var workspace = "C:\\work\u200B\\sub";
        var command = "echo first\r\necho \u202Esecond\u202C\n\techo literal \\u202E";
        fixture.Config.WorkspacePath = workspace;
        fixture.Config.Lifecycle.Initialize = [command];

        var result = await fixture.Up(approveHostCommandsAsync: (review, _) =>
        {
            Assert.Equal(@"C:\\work\u200B\\sub", review.DisplayWorkspacePath);
            Assert.Equal("echo first\r\necho \\u202Esecond\\u202C\n\\u0009echo literal \\\\u202E",
                Assert.Single(review.DisplayCommands));
            Assert.Equal(workspace, review.WorkspacePath);
            Assert.Equal(command, Assert.Single(review.Commands));
            return Task.FromResult(true);
        });

        Assert.True(result.Success);
        var process = Assert.Single(fixture.HostProcesses);
        Assert.Equal(workspace, process.WorkingDirectory);
        Assert.Equal(command, process.ArgumentList[2]);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task HostCommandsWithoutApprovalPreventAllPreparationAndMutation(
        bool hasApprovalCallback, bool rebuild, bool noCache)
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["untrusted host command"];
        fixture.Config.Build = new() { Context = "untrusted-build-context" };
        fixture.Config.Features = [new() { Id = "untrusted-feature" }];
        var reviews = 0;

        var result = await fixture.Up(rebuild: rebuild, noCache: noCache,
            approveHostCommandsAsync: hasApprovalCallback ? (_, _) =>
            {
                reviews++;
                return Task.FromResult(false);
            } : null);

        Assert.False(result.Success);
        Assert.Contains(hasApprovalCallback ? "not approved" : "requires explicit approval", result.Detail);
        Assert.Equal(hasApprovalCallback ? 1 : 0, reviews);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.HostProcesses);
        Assert.Empty(fixture.Compose.Engine.Mutations);
    }

    [Fact]
    public async Task HostReviewFailureFailsClosedWithoutPreparingAnything()
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["untrusted host command"];

        var result = await fixture.Up(approveHostCommandsAsync: (_, _) =>
            throw new InvalidOperationException("Host review unavailable."));

        Assert.False(result.Success);
        Assert.Contains("Host review unavailable", result.Detail);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task CancellationBeforeHostReviewDoesNotInvokeReviewOrAnyService()
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["untrusted host command"];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Up(cancellation.Token,
            approveHostCommandsAsync: (_, _) => throw new InvalidOperationException("Must not review.")));

        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task CancellationDuringPendingHostReviewRejectsEvenLateApproval()
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["untrusted host command"];
        using var cancellation = new CancellationTokenSource();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = fixture.Up(cancellation.Token, approveHostCommandsAsync: (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            opened.SetResult();
            return decision.Task; // Deliberately ignores cancellation.
        });
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Calls);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        decision.SetResult(true);
        Assert.Empty(fixture.Calls);

        // The cancelled operation released its gate, and approval is not cached for the retry.
        Assert.False((await fixture.Up()).Success);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task CancellationAtHostApprovalPreventsExecution()
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["untrusted host command"];
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Up(cancellation.Token,
            approveHostCommandsAsync: (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            }));

        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterHostCommandPreventsRemainingCommandsAndContainerMutation(bool hasSecondCommand)
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = hasSecondCommand ? ["first", "second"] : ["first"];
        using var cancellation = new CancellationTokenSource();
        fixture.AfterHostCommand = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Up(cancellation.Token,
            approveHostCommandsAsync: (_, _) => Task.FromResult(true)));

        Assert.Single(fixture.HostProcesses);
        Assert.Equal(["host"], fixture.Calls);
        Assert.Empty(fixture.Compose.Engine.Mutations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EveryStartAndRebuildRequiresFreshApprovalAndExecutesReviewedCommands(bool rebuild, bool noCache)
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["  first & second  ", "", " \t", "third\nfourth"];
        var reviews = new List<DevContainerHostCommandReview>();

        for (var operation = 0; operation < 2; operation++)
        {
            fixture.Calls.Clear();
            Assert.True((await fixture.Up(rebuild: rebuild, noCache: noCache, approveHostCommandsAsync: (review, _) =>
            {
                Assert.Empty(fixture.Calls);
                reviews.Add(review);
                return Task.FromResult(true);
            })).Success);
            Assert.Equal(["host", "host"], fixture.Calls.Take(2));
            Assert.Contains("wslc:PullImageAsync", fixture.Calls);
            Assert.Contains("wslc:RunContainerAsync", fixture.Calls);
        }

        Assert.Equal(2, reviews.Count);
        Assert.NotSame(reviews[0], reviews[1]);
        Assert.Equal(4, fixture.HostProcesses.Count);
        Assert.All(reviews, review =>
        {
            Assert.Equal(fixture.Config.WorkspacePath, review.WorkspacePath);
            Assert.Equal(["  first & second  ", "third\nfourth"], review.Commands);
        });
        for (var index = 0; index < fixture.HostProcesses.Count; index++)
        {
            var process = fixture.HostProcesses[index];
            Assert.Equal(fixture.Config.WorkspacePath, process.WorkingDirectory);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), process.FileName);
            Assert.Equal(new[] { "/d", "/c", reviews[0].Commands[index % 2] }, process.ArgumentList);
            Assert.False(process.UseShellExecute);
        }
        fixture.Calls.Clear();
        Assert.False((await fixture.Up(rebuild: rebuild, noCache: noCache)).Success);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task MutationDuringPendingHostReviewCannotChangeExecutedWorkspaceOrCommands()
    {
        var fixture = Fixture.SingleContainer();
        var originalCommands = new List<string> { "reviewed first", "reviewed second" };
        fixture.Config.Lifecycle.Initialize = originalCommands;
        var originalWorkspace = fixture.Config.WorkspacePath;
        var opened = new TaskCompletionSource<DevContainerHostCommandReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = fixture.Up(approveHostCommandsAsync: (review, _) =>
        {
            opened.SetResult(review);
            return decision.Task;
        });
        var review = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Calls);

        originalCommands[0] = "replacement command";
        originalCommands.Add("extra command");
        fixture.Config.Lifecycle = new() { Initialize = ["entirely different commands"] };
        fixture.Config.WorkspacePath = @"C:\different-workspace";
        Assert.Throws<NotSupportedException>(() => ((IList<string>)review.Commands)[0] = "changed through review");
        Assert.Equal(originalWorkspace, review.WorkspacePath);
        Assert.Equal(["reviewed first", "reviewed second"], review.Commands);
        decision.SetResult(true);

        Assert.True((await operation.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(["reviewed first", "reviewed second"], fixture.HostProcesses.Select(p => p.ArgumentList[2]));
        Assert.All(fixture.HostProcesses, process => Assert.Equal(originalWorkspace, process.WorkingDirectory));
    }

    [Fact]
    public async Task FailedApprovedHostCommandDoesNotPrepareOrMutateContainers()
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["first", "second"];
        fixture.HostResult = new() { ExitCode = 1, StandardError = "Host script failed." };

        var result = await fixture.Up(approveHostCommandsAsync: (_, _) => Task.FromResult(true));

        Assert.False(result.Success);
        Assert.Contains("initializeCommand failed", result.Detail);
        Assert.Equal(["host"], fixture.Calls);
        Assert.Empty(fixture.Compose.Engine.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoHostCommandsNeedNoApprovalAndPreserveSingleContainerBehavior(bool hasApprovalCallback)
    {
        var fixture = Fixture.SingleContainer();
        fixture.Config.Lifecycle.Initialize = ["", " \r\n\t"];
        // No host execution means an unused workspace need not be normalized for a review.
        fixture.Config.WorkspacePath = "";

        var result = await fixture.Up(approveHostCommandsAsync: hasApprovalCallback
            ? (_, _) => throw new InvalidOperationException("Must not request host approval.")
            : null);

        Assert.True(result.Success);
        Assert.Empty(fixture.HostProcesses);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.Contains("wslc:RunContainerAsync", fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposeStillBlocksHostCommandsAndFeaturesWithoutRequestingHostApproval(bool hasFeatures)
    {
        var fixture = new Fixture();
        if (hasFeatures)
            fixture.Config.Features =
            [
                new()
                {
                    Id = "blocked-feature",
                    RawOptions = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
                },
            ];
        else
            fixture.Config.Lifecycle.Initialize = ["blocked host command"];

        var hostReviews = 0;
        var result = await fixture.Up(approveHostCommandsAsync: (_, _) =>
        {
            hostReviews++;
            throw new InvalidOperationException("Compose must not request host execution.");
        });

        Assert.False(result.Success);
        Assert.Contains("Compatibility checks blocked deployment", result.Detail);
        var preview = Assert.Single(fixture.Compose.Reviews);
        Assert.False(preview.CanApply);
        var blocker = Assert.Single(preview.Settings, setting => setting.Disposition == ComposeSettingDisposition.Blocked);
        Assert.Equal("Project", blocker.Service);
        Assert.Equal("import diagnostic", blocker.Setting);
        Assert.Contains("Compose dev-container features and host initialize commands require externally prepared inputs",
            blocker.EffectiveValue);
        Assert.Equal("Importer diagnostic", blocker.Source);
        Assert.Equal(0, hostReviews);
        Assert.Empty(fixture.HostProcesses);
        Assert.Empty(fixture.Commands);
        Assert.Empty(fixture.Compose.Engine.Mutations);
        Assert.Empty(fixture.Compose.SavedSnapshots);
        // Existing dev-container bookkeeping still saves the blocked outcome; no execution service is called.
        Assert.Equal(["store:Get", "store:Save"], fixture.Calls);
    }

    [Fact]
    public async Task ScalingRetainedPrimaryDoesNotScheduleHooksForAdditionalReplicas()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Commands.Clear();
        fixture.ExecIds.Clear();
        fixture.Config.Compose!.Project.Services[0].Replicas = 3;

        Assert.True((await fixture.Up()).Success);

        Assert.Empty(fixture.Commands);
        Assert.Equal("instance-1", fixture.Config.ComposeLifecycleProgress!.ContainerId);
        fixture.Config.Compose.Project.Services[0].Options.EnvironmentVariables.Add("UPDATED=yes");
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds, id => Assert.Equal(fixture.Compose.Engine.ContainerIds["demo_web"], id));
    }

    [Fact]
    public async Task SuccessfulSecondaryCannotScheduleHooksWhenPrimaryCreationFailed()
    {
        var fixture = new Fixture();
        fixture.Config.Compose!.Project.Services[0].Replicas = 2;
        fixture.Compose.Engine.AfterRun = name =>
        {
            fixture.Compose.Engine.ContainerIds[name] = name;
            fixture.Compose.Engine.FailRunAfterCreate = name == "demo_web";
        };

        var result = await fixture.Up();

        Assert.False(result.Success);
        Assert.Empty(fixture.Commands);
        Assert.Null(fixture.Config.ComposeLifecycleProgress);
        Assert.Contains("demo_web_2", fixture.Compose.Engine.Containers.Keys);
    }

    [Fact]
    public async Task CancellationAfterPrimaryCreationKeepsOnlyItsDurableQueue()
    {
        var fixture = new Fixture();
        fixture.Config.Compose!.Project.Services[0].Replicas = 3;
        using var cancellation = new CancellationTokenSource();
        fixture.Compose.Engine.AfterRun = name =>
        {
            fixture.Compose.Engine.ContainerIds[name] = name;
            if (name == "demo_web_2") cancellation.Cancel();
        };

        var result = await fixture.Up(cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Commands);
        fixture.Reload();
        Assert.Equal("demo_web", fixture.Config.ComposeLifecycleProgress!.ContainerId);
        Assert.Equal(3, fixture.Config.ComposeLifecycleProgress.PendingCreate.Count);
        Assert.Single(fixture.Config.ComposeLifecycleProgress.PendingStart);
        fixture.Compose.Engine.AfterRun = null;
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds, id => Assert.Equal("demo_web", id));
    }

    [Fact]
    public async Task CreateThenKeepDoesNotExecuteAnyContainerHookAgain()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        fixture.Commands.Clear();
        fixture.Compose.Engine.Mutations.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Empty(fixture.Commands);
        Assert.DoesNotContain(fixture.Compose.Engine.Mutations, m => m.StartsWith("start:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartingStoppedPrimaryRunsOnlyPostStart()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Compose.Engine.States["demo_web"] = ContainerState.Stopped;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Equal(["started"], fixture.Commands);
    }

    [Fact]
    public async Task RecreatingPrimaryRunsCreationHooksForTheNewIdentity()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Config.Compose!.Project.Services[0].Options.EnvironmentVariables.Add("UPDATED=yes");
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds.TakeLast(4), id => Assert.Equal("instance-2", id));
    }

    [Fact]
    public async Task SiblingFailureDoesNotLosePrimaryHooksOrRepeatThemOnRetry()
    {
        var fixture = new Fixture();
        fixture.Config.Compose!.Project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        fixture.Compose.Engine.FailRunAfterCreate = true;
        fixture.Compose.Engine.AfterRun = name =>
        {
            fixture.Compose.Engine.ContainerIds[name] = name;
            fixture.Compose.Engine.FailRunAfterCreate = name == "demo_other";
        };

        Assert.False((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        fixture.Reload();
        fixture.Compose.Engine.AfterRun = null;
        fixture.Compose.Engine.FailRunAfterCreate = false;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Theory]
    [InlineData("create", 0)]
    [InlineData("content", 1)]
    [InlineData("created", 2)]
    [InlineData("started", 3)]
    public async Task HookFailureResumesWithoutRepeatingAcknowledgedCommands(string failed, int index)
    {
        var fixture = new Fixture { FailCommand = failed };
        Assert.False((await fixture.Up()).Success);
        Assert.Equal(new[] { "create", "content", "created", "started" }.Take(index + 1), fixture.Commands);
        fixture.Reload();
        fixture.FailCommand = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(new[] { "create", "content", "created", "started" }.Skip(index), fixture.Commands);
        fixture.Commands.Clear();
        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task CompletedHooksAreNotReplayedAfterDevContainerReimport()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        // A fresh import has no runtime checkpoint. The persisted record still owns it.
        fixture.Config = JsonSerializer.Deserialize<DevContainerConfig>(
            JsonSerializer.Serialize(new DevContainerConfig
            {
                Id = fixture.Config.Id, Name = fixture.Config.Name, Compose = fixture.Config.Compose,
                WorkspaceFolder = fixture.Config.WorkspaceFolder, Lifecycle = fixture.Config.Lifecycle,
            }))!;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task FailedCommandWithinCreationStepResumesFrozenCommandsAfterConfigEdit()
    {
        var fixture = new Fixture { FailCommand = "create-two" };
        fixture.Config.Lifecycle.OnCreate.Add("create-two");
        Assert.False((await fixture.Up()).Success);
        Assert.Equal(["create", "create-two"], fixture.Commands);
        fixture.Reload();
        fixture.Config.Lifecycle.OnCreate = ["replacement-command"];
        fixture.Config.WorkspaceFolder = "/edited";
        fixture.FailCommand = null;
        fixture.Commands.Clear();
        fixture.Scripts.Clear();

        Assert.True((await fixture.Up()).Success);

        Assert.Equal(["create-two", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.Scripts, script => Assert.StartsWith("cd '/work' && ", script));
    }

    [Fact]
    public async Task CancellationBetweenHooksRetainsAcknowledgedProgress()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.AfterExec = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Up(cancellation.Token));
        Assert.Equal(["create"], fixture.Commands);
        fixture.Reload();
        fixture.AfterExec = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["content", "created", "started"], fixture.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSiblingStartupDoesNotLosePrimaryHooks(bool existing)
    {
        var fixture = new Fixture();
        if (existing)
        {
            Assert.True((await fixture.Up()).Success);
            fixture.Compose.Engine.States["demo_web"] = ContainerState.Stopped;
            fixture.Commands.Clear();
        }
        using var cancellation = new CancellationTokenSource();
        fixture.Config.Compose!.Project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        fixture.Config.Compose.Project.Services.Add(new() { Name = "last", Options = new() { Image = "fixture" } });
        fixture.Persist();
        fixture.Compose.Engine.AfterRun = name =>
        {
            fixture.Compose.Engine.ContainerIds[name] = name;
            if (name == "demo_other") cancellation.Cancel();
        };

        var cancelled = await fixture.Up(cancellation.Token);
        Assert.False(cancelled.Success);
        Assert.Contains("cancelled", cancelled.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Commands);
        fixture.Reload();
        fixture.Compose.Engine.AfterRun = null;

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(existing ? ["started"] : new[] { "create", "content", "created", "started" }, fixture.Commands);
        fixture.Commands.Clear();
        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task FailedStartDoesNotExecuteHooksAndSuccessfulRetryRunsOnlyPostStart()
    {
        var fixture = new Fixture();
        Assert.True((await fixture.Up()).Success);
        fixture.Compose.Engine.States["demo_web"] = ContainerState.Stopped;
        fixture.Compose.Engine.FailStart = "demo_web";
        fixture.Commands.Clear();

        Assert.False((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
        fixture.Compose.Engine.FailStart = null;
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["started"], fixture.Commands);
    }

    [Fact]
    public async Task ReplacementDoesNotInheritOldIdentityPendingHooks()
    {
        var fixture = new Fixture { FailCommand = "content" };
        Assert.False((await fixture.Up()).Success);
        fixture.Reload();
        fixture.Config.Compose!.Project.Services[0].Options.EnvironmentVariables.Add("UPDATED=yes");
        fixture.FailCommand = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds.TakeLast(4), id => Assert.Equal("instance-2", id));
    }

    [Fact]
    public async Task ShortAndFullIdsShareTheSamePendingCheckpoint()
    {
        var fixture = new Fixture { FailCommand = "content" };
        var fullId = new string('a', 64);
        fixture.Compose.Engine.AfterRun = name => fixture.Compose.Engine.ContainerIds[name] = fullId;
        Assert.False((await fixture.Up()).Success);
        fixture.Reload();
        fixture.Compose.Engine.ContainerIds["demo_web"] = fullId[..12];
        fixture.FailCommand = null;
        fixture.Commands.Clear();

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["content", "created", "started"], fixture.Commands);
        Assert.All(fixture.ExecIds.TakeLast(3), id => Assert.Equal(fullId[..12], id));
    }

    [Fact]
    public async Task PersistenceFailureIsSurfacedBeforeExecutingHooks()
    {
        var fixture = new Fixture { FailSave = true };
        var result = await fixture.Up();
        Assert.False(result.Success);
        Assert.Contains("fixture save failure", result.Detail);
        Assert.Empty(fixture.Commands);
        Assert.Equal("instance-1", fixture.Compose.SavedProject!.AppliedServices["web"].ContainerId);
        Assert.Equal(ContainerState.Running, fixture.Compose.Engine.States["demo_web"]);
        fixture.FailSave = false;

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
    }

    [Fact]
    public async Task CheckpointAndSupervisionFailuresAreBothReportedWithoutStartingMoreServices()
    {
        var fixture = new Fixture { FailSave = true };
        fixture.Config.Compose!.Project.Services[0].Replicas = 2;
        fixture.Config.Compose!.Project.Services[0].Restart = RestartPolicyKind.Always;
        fixture.Config.Compose.Project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        var settingsAttempts = 0;
        fixture.Compose.BeforeSettingsSave = () =>
        {
            if (fixture.Compose.Engine.Containers.ContainsKey("demo_web") && ++settingsAttempts == 1)
                throw new InvalidOperationException("fixture supervision failure");
        };
        var refreshed = false;
        fixture.Compose.Monitor.RefreshRequested = () => refreshed = true;

        var result = await fixture.Up();

        Assert.False(result.Success);
        Assert.Contains("fixture save failure", result.Detail);
        Assert.Contains("fixture supervision failure", result.Detail);
        Assert.Equal(2, settingsAttempts);
        Assert.Equal("instance-1", fixture.Compose.SavedProject!.AppliedServices["web"].ContainerId);
        Assert.Equal("demo_web", Assert.Single(fixture.Compose.RestartPolicies).ContainerName);
        Assert.True(refreshed);
        Assert.Empty(fixture.Commands);
        Assert.DoesNotContain("run:demo_other", fixture.Compose.Engine.Mutations);
        Assert.DoesNotContain("run:demo_web_2", fixture.Compose.Engine.Mutations);
    }

    [Theory]
    [InlineData("applied-save")]
    [InlineData("supervision")]
    [InlineData("refresh")]
    public async Task PostStartCompletionFailureKeepsCreationHooksForReloadAndRetry(string failure)
    {
        var fixture = new Fixture();
        fixture.Config.Compose!.Project.Services[0].Replicas = 2;
        fixture.Config.Compose!.Project.Services.Add(new() { Name = "other", Options = new() { Image = "fixture" } });
        fixture.Persist();
        fixture.Compose.BeforeSave = project =>
        {
            if (failure == "applied-save" && project.AppliedServices.ContainsKey("web"))
                throw new InvalidOperationException("fixture applied-save failure");
        };
        fixture.Compose.BeforeSettingsSave = () =>
        {
            if (failure == "supervision" && fixture.Compose.Engine.Containers.ContainsKey("demo_web"))
                throw new InvalidOperationException("fixture supervision failure");
        };
        fixture.Compose.Monitor.RefreshRequested = () =>
        {
            if (failure == "refresh") throw new InvalidOperationException("fixture refresh failure");
        };

        var result = await fixture.Up();

        Assert.False(result.Success);
        Assert.Contains($"fixture {failure} failure", result.Detail);
        Assert.Empty(fixture.Commands);
        Assert.DoesNotContain("run:demo_other", fixture.Compose.Engine.Mutations);
        Assert.DoesNotContain("run:demo_web_2", fixture.Compose.Engine.Mutations);
        Assert.Equal(ContainerState.Running, fixture.Compose.Engine.States["demo_web"]);
        fixture.Reload();
        fixture.Compose.BeforeSave = null;
        fixture.Compose.BeforeSettingsSave = null;
        fixture.Compose.Monitor.RefreshRequested = null;

        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started"], fixture.Commands);
        Assert.Equal(1, fixture.Compose.Engine.Mutations.Count(m => m == "run:demo_web"));
        Assert.All(fixture.ExecIds, id => Assert.Equal("instance-1", id));
        fixture.Commands.Clear();
        Assert.True((await fixture.Up()).Success);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public async Task SingleContainerUpStillReplacesContainerAndRunsAllHooks()
    {
        var fixture = new Fixture();
        fixture.Config.Compose = null;
        fixture.Config.Image = "fixture";
        fixture.Config.RunOptions.Image = "fixture";
        Assert.True((await fixture.Up()).Success);
        Assert.True((await fixture.Up()).Success);
        Assert.Equal(["create", "content", "created", "started", "create", "content", "created", "started"], fixture.Commands);
        Assert.Equal(2, fixture.Compose.Engine.Mutations.Count(m => m == "run:devcontainer-test"));
        Assert.Contains("remove:instance-1", fixture.Compose.Engine.Mutations);
    }

    [Fact]
    public void RealStorePreservesPendingProgressThroughImportAndReload()
    {
        using var files = new StoreFiles();
        var store = new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File);
        store.Save(new()
        {
            Id = "test", ComposeLifecycleProgress = new()
            {
                ContainerId = "old-id",
                PendingCreate = [new() { Step = "updateContentCommand", Command = "pending" }],
                PendingStart = [new() { Step = "postStartCommand", Command = "start" }],
            },
        });
        store.Save(new() { Id = "test", Name = "reimported" });

        var reloaded = new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File).Get("test")!;
        Assert.Equal("reimported", reloaded.Name);
        Assert.Equal("old-id", reloaded.ComposeLifecycleProgress!.ContainerId);
        Assert.Equal("pending", Assert.Single(reloaded.ComposeLifecycleProgress.PendingCreate).Command);
        Assert.Equal("start", Assert.Single(reloaded.ComposeLifecycleProgress.PendingStart).Command);
        store.Save(new() { Id = "test", ComposeLifecycleProgress = new() { ContainerId = "new-id" } });
        Assert.Equal("new-id", new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File)
            .Get("test")!.ComposeLifecycleProgress!.ContainerId);
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    [Fact]
    public void RealStoreSurfacesWriteFailuresAndCleansTemporaryFile()
    {
        using var files = new StoreFiles();
        Directory.CreateDirectory(files.File);
        var store = new DevContainerStore(NullLogger<DevContainerStore>.Instance, files.File);
        Assert.Throws<InvalidOperationException>(() => store.Save(new() { Id = "test" }));
        Assert.Empty(Directory.GetFiles(files.Root, "*.tmp"));
    }

    private sealed class StoreFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "devcontainer-lifecycle-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "devcontainers.json");
        public StoreFiles() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Fixture
    {
        public ComposeNetworkSupervisorTests.Fixture Compose { get; } = new();
        public DevContainerConfig Config { get; set; }
        public List<string> Commands { get; } = new();
        public List<string> ExecIds { get; } = new();
        public List<string> Scripts { get; } = new();
        public List<string> Calls { get; } = new();
        public List<ProcessStartInfo> HostProcesses { get; } = new();
        public CommandResult HostResult { get; set; } = new();
        public Action<ProcessStartInfo>? AfterHostCommand { get; set; }
        public string? FailCommand { get; set; }
        public bool FailSave { get; set; }
        public Action<string>? AfterExec { get; set; }
        private DevContainerConfig? _saved;
        private readonly DevContainerSupervisor _supervisor;

        public Fixture()
        {
            Compose.Project.Services[0].Options = new() { Image = "fixture" };
            Compose.Engine.NextImageId = "sha256:fixture-v1";
            var generation = 0;
            Compose.Engine.AfterRun = name => Compose.Engine.ContainerIds[name] = $"instance-{++generation}";
            Config = new()
            {
                Id = "test", Name = "dev", WorkspaceFolder = "/work",
                Compose = new() { Service = "web", Project = Compose.Project },
                Lifecycle = new() { OnCreate = ["create"], UpdateContent = ["content"], PostCreate = ["created"], PostStart = ["started"] },
            };
            var wslc = NetworkTestProxy.Create<IWslcService>((method, args) =>
            {
                Calls.Add("wslc:" + method.Name);
                if (method.Name != nameof(IWslcService.ExecAsync))
                    return method.Invoke(Compose.Engine.Service, args);
                var command = ((string)args[1]!).Split(" && ", 2)[1];
                Commands.Add(command);
                Scripts.Add((string)args[1]!);
                ExecIds.Add((string)args[0]!);
                AfterExec?.Invoke(command);
                return Task.FromResult(new CommandResult
                {
                    ExitCode = command == FailCommand ? 1 : 0,
                    StandardError = command == FailCommand ? "hook failed" : "",
                });
            });
            var store = NetworkTestProxy.Create<IDevContainerStore>((method, args) =>
            {
                Calls.Add("store:" + method.Name);
                if (method.Name == nameof(IDevContainerStore.Get)) return _saved;
                if (method.Name != nameof(IDevContainerStore.Save)) throw new InvalidOperationException(method.Name);
                if (FailSave) throw new InvalidOperationException("fixture save failure");
                _saved = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize((DevContainerConfig)args[0]!))!;
                return null;
            });
            var features = NetworkTestProxy.Create<IDevContainerFeatureResolver>((method, _) =>
            {
                Calls.Add("features:" + method.Name);
                throw new InvalidOperationException(method.Name);
            });
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => throw new InvalidOperationException(method.Name));
            _supervisor = new(wslc, store, features, Compose.Supervisor, new ProcessRunner(settings),
                NullLogger<DevContainerSupervisor>.Instance, (psi, _) =>
                {
                    Calls.Add("host");
                    HostProcesses.Add(psi);
                    AfterHostCommand?.Invoke(psi);
                    return Task.FromResult(HostResult);
                });
        }

        public static Fixture SingleContainer()
        {
            var fixture = new Fixture();
            fixture.Config.Compose = null;
            fixture.Config.Image = "fixture";
            fixture.Config.RunOptions.Image = "fixture";
            fixture.Config.WorkspacePath = @"C:\reviewed-workspace";
            return fixture;
        }

        public Task<DevContainerOperationResult> Up(CancellationToken ct = default, bool rebuild = false, bool noCache = false,
            Func<DevContainerHostCommandReview, CancellationToken, Task<bool>>? approveHostCommandsAsync = null) =>
            _supervisor.UpAsync(Config, rebuild, noCache, ct, approveHostCommandsAsync);
        public void Persist() => _saved = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize(Config))!;
        public void Reload() => Config = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize(_saved))!;
    }
}
