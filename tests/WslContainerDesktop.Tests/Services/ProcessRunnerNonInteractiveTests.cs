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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

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
