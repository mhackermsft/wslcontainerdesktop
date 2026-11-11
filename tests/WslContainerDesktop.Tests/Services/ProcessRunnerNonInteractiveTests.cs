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
using System.Reflection;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Covers process execution safeguards that close stdin and fail fast when an engine unexpectedly prompts.
/// </summary>
public sealed class ProcessRunnerNonInteractiveTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [Fact]
    public void NonInteractiveStartInfo_RedirectsStdinWhileOrdinaryCallsStayUnchanged()
    {
        var nonInteractive = ProcessRunner.CreateStartInfo(@"C:\wslc.exe", ["container", "prune"], redirectInput: true);
        var ordinary = ProcessRunner.CreateStartInfo(@"C:\wslc.exe", ["list"], redirectInput: false);

        Assert.True(nonInteractive.RedirectStandardInput);
        // Every other wslc call keeps its previous console-stdin behavior.
        Assert.False(ordinary.RedirectStandardInput);
        foreach (var psi in new[] { nonInteractive, ordinary })
        {
            Assert.True(psi.RedirectStandardOutput);
            Assert.True(psi.RedirectStandardError);
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.CreateNoWindow);
        }

        Assert.Equal(["container", "prune"], nonInteractive.ArgumentList);
    }

    [WindowsFact]
    public async Task RunnerTargetsSelectedSessionButLeavesGlobalInfoUnscoped()
    {
        using var stub = new WslcStub();
        var settings = DispatchProxy.Create<ISettingsService, WslcCapabilitiesServiceTests.SettingsProxy>();
        settings.WslcPath = stub.Quiet;
        var session = new WslcSessionContext();
        session.Select("SDKSession");
        var runner = new ProcessRunner(settings, session);

        Assert.True((await runner.RunAsync(["list"])).Success);
        Assert.True((await runner.RunAsync(["system", "info"])).Success);

        Assert.Equal("--session SDKSession list", stub.Invocations(stub.Quiet)[0]);
        Assert.Equal("system info", stub.Invocations(stub.Quiet)[1]);
    }

    [WindowsFact]
    public async Task BoundRunnersRemainIndependentOfLaterSelectionChanges()
    {
        using var stub = new WslcStub();
        var settings = DispatchProxy.Create<ISettingsService, WslcCapabilitiesServiceTests.SettingsProxy>();
        settings.WslcPath = stub.Quiet;
        var context = new WslcSessionContext();
        var runner = new ProcessRunner(settings, context);
        var defaultRunner = runner.ForSession(context.Capture());
        context.Select("First");
        var firstRunner = runner.ForSession(context.Capture());
        context.Select("Second");
        settings.WslcPath = stub.Fail;

        Assert.True((await defaultRunner.RunAsync(["list"])).Success);
        Assert.True((await firstRunner.RunNonInteractiveAsync(["container", "prune"])).Success);
        Assert.True((await firstRunner.RunStreamingAsync(["pull", "example"], _ => { })).Success);
        Assert.False((await runner.RunAsync(["list"])).Success);
        Assert.True((await firstRunner.RunAsync(["system", "info"])).Success);

        Assert.Equal(["list", "--session First container prune", "--session First pull example", "system info"],
            stub.Invocations(stub.Quiet));
        Assert.Equal(["--session Second list"], stub.Invocations(stub.Fail));
    }

    [WindowsFact]
    public async Task IntentionalDefaultStopBlocksScopedCommandsButNotDiagnosticsOrOtherSessions()
    {
        using var stub = new WslcStub();
        var settings = DispatchProxy.Create<ISettingsService, WslcCapabilitiesServiceTests.SettingsProxy>();
        settings.WslcPath = stub.Quiet;
        var context = new WslcSessionContext();
        var runner = new ProcessRunner(settings, context);
        var capturedDefault = runner.ForSession(WslcSessionTarget.Default);
        context.PauseDefaultSession(stub.Quiet);

        await Assert.ThrowsAsync<InvalidOperationException>(() => capturedDefault.RunAsync(["list"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capturedDefault.RunNonInteractiveAsync(["container", "prune"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capturedDefault.RunStreamingAsync(["pull", "image"], _ => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capturedDefault.RunWithStdinAsync(["login"], "secret"));
        Assert.Throws<InvalidOperationException>(() => capturedDefault.RunInteractive(["exec"]));
        Assert.Throws<InvalidOperationException>(() => { _ = capturedDefault.VerifyScopeAsync(CancellationToken.None); });

        Assert.True((await capturedDefault.RunAsync(["system", "info"])).Success);
        Assert.True((await runner.ForSession(new WslcSessionTarget("another")).RunAsync(["list"])).Success);
        Assert.Equal(["system info", "--session another list"], stub.Invocations(stub.Quiet));
    }

    [Fact]
    public async Task GuardedRunnerRejectsStaleScopeBeforeAnyProcessAndCannotBeRetargeted()
    {
        var settings = DispatchProxy.Create<ISettingsService, WslcCapabilitiesServiceTests.SettingsProxy>();
        settings.WslcPath = "must-not-run";
        var checks = 0;
        var runner = new ProcessRunner(settings, fixedSession: new("owner"), fixedExecutablePath: "must-not-run",
            scopeVerifier: _ =>
            {
                checks++;
                throw new InvalidOperationException("session changed");
            });
        var bound = runner.ForSession(new("owner"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.RunAsync(["list"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.RunNonInteractiveAsync(["remove", "name"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.RunStreamingAsync(["pull", "image"], _ => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.RunWithStdinAsync(["login"], "secret"));
        Assert.Throws<InvalidOperationException>(() => runner.ForSession(new("another")));
        Assert.Throws<InvalidOperationException>(() => runner.RunInteractive(["exec"]));
        Assert.Equal(4, checks);
    }

    [WindowsFact]
    public async Task PromptingEngine_ReadsEofAndIsReportedAsFailureInsteadOfHanging()
    {
        using var stub = new WslcStub();

        var result = await ProcessRunner.RunNonInteractiveAtPathAsync(stub.Prompt, ["container", "prune"],
            TimeSpan.FromSeconds(20)).WaitAsync(Guard);

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        // DECLINED proves the prompt read EOF and the engine changed nothing.
        Assert.Contains("DECLINED", result.StandardOutput);
        Assert.Contains("asked for confirmation", result.StandardError);
        Assert.Contains("[y/N]", result.StandardError);
        Assert.Contains("WARNING! This will remove", result.StandardError);
        Assert.Equal(["container prune"], stub.Invocations(stub.Prompt));
    }

    [WindowsFact]
    public async Task ForceSkipsThePrompt_AndSucceeds()
    {
        using var stub = new WslcStub();

        var result = await ProcessRunner.RunNonInteractiveAtPathAsync(stub.Prompt, ["container", "prune", "--force"],
            TimeSpan.FromSeconds(20)).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Contains("PRUNED", result.StandardOutput);
        Assert.DoesNotContain("[y/N]", result.StandardOutput);
    }

    [WindowsFact]
    public async Task EngineFailure_IsPassedThroughUnchanged()
    {
        using var stub = new WslcStub();

        var result = await ProcessRunner.RunNonInteractiveAtPathAsync(stub.Fail, ["volume", "remove", "x"],
            TimeSpan.FromSeconds(20)).WaitAsync(Guard);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Volume not found", result.ErrorText);
    }

    [WindowsFact]
    public async Task Timeout_KillsTheEngineAndReportsFailure()
    {
        using var stub = new WslcStub();
        var clock = Stopwatch.StartNew();

        var result = await ProcessRunner.RunNonInteractiveAtPathAsync(stub.Sleep, ["image", "prune"],
            TimeSpan.FromSeconds(1)).WaitAsync(Guard);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.StandardError);
        // The stub sleeps ~60 s; returning well before that proves the timeout, not the stub, ended it.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed}.");
    }

    [WindowsFact]
    public async Task CallerCancellation_StillThrowsRatherThanReportingATimeout()
    {
        using var stub = new WslcStub();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunNonInteractiveAtPathAsync(
            stub.Sleep, ["image", "prune"], TimeSpan.FromMinutes(5), cancel.Token).WaitAsync(Guard));
    }

    [Theory]
    [InlineData("Are you sure you want to continue? [y/N] ", "")]
    [InlineData("", "Are you sure you want to continue? [y/N] ")]
    [InlineData("Continue? [Y/n]", "")]
    public void RejectDeclinedPrompt_FailsASuccessfulExitThatOnlyPrompted(string stdout, string stderr)
    {
        var result = ProcessRunner.RejectDeclinedPrompt(new CommandResult { StandardOutput = stdout, StandardError = stderr });

        Assert.False(result.Success);
        Assert.Equal(stdout, result.StandardOutput);
    }

    [Fact]
    public void RejectDeclinedPrompt_LeavesOrdinaryResultsUntouched()
    {
        var success = new CommandResult { StandardOutput = "Deleted: 3f2a\nTotal reclaimed space: 12MB\n" };
        var failure = new CommandResult { ExitCode = 2, StandardOutput = "[y/N]", StandardError = "boom" };

        Assert.Same(success, ProcessRunner.RejectDeclinedPrompt(success));
        Assert.Same(failure, ProcessRunner.RejectDeclinedPrompt(failure));
    }

    [WindowsFact]
    public async Task ProcessExecutor_ClosesRedirectedStdinSoReadersSeeEof()
    {
        // With stdin redirected the child's input is our pipe, so only closing it can end ReadToEnd.
        var psi = PowerShell("$text = [Console]::In.ReadToEnd(); [Console]::WriteLine('EOF:' + $text.Length)");
        psi.RedirectStandardInput = true;

        var result = await ProcessExecutor.RunAsync(psi).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal("EOF:0", result.StandardOutput.Trim());
    }

    [WindowsFact]
    public Task StatusTimeoutReapsItsProcessBeforeReturning() => VerifyStatusProcessCleanupAsync(false);

    [WindowsFact]
    public Task StatusCancellationReapsItsProcessBeforeReturning() => VerifyStatusProcessCleanupAsync(true);

    private static async Task VerifyStatusProcessCleanupAsync(bool callerCancellation)
    {
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var run = ProcessExecutor.RunAsync(
            PowerShell("[Console]::WriteLine($PID); Start-Sleep -Seconds 60"),
            onLine: line =>
            {
                if (int.TryParse(line, out var id)) started.TrySetResult(id);
            },
            timeout: TimeSpan.FromSeconds(5),
            ct: cancel.Token);
        int pid;
        try
        {
            pid = await started.Task.WaitAsync(Guard);
            if (callerCancellation)
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Guard));
            }
            else
            {
                var result = await run.WaitAsync(Guard);
                Assert.False(result.Success);
                Assert.Contains("timed out", result.ErrorText);
            }
        }
        finally
        {
            cancel.Cancel();
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, "The observation must finish process cleanup before releasing the lifecycle gate.");
        }
        catch (ArgumentException)
        {
            // A reaped process no longer has an entry in the process table.
        }
    }

    internal static ProcessStartInfo PowerShell(string command)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return psi;
    }
}
