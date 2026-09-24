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

public sealed class ChildProcessJobTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [WindowsFact]
    public void ClosingTheJob_KillsAssignedProcessesButNotOthers()
    {
        using var assigned = StartPing();
        using var control = StartPing();
        try
        {
            var job = ChildProcessJob.Create();
            Assert.NotNull(job);
            Assert.True(job!.TryAssign(assigned));
            Assert.True(job.Contains(assigned));
            Assert.False(job.Contains(control));

            // Same effect as the app exiting: the last job handle closes.
            job.Dispose();

            Assert.True(assigned.WaitForExit(10_000), "Assigned process survived the job closing.");
            Assert.False(control.WaitForExit(1_000), "An unassigned process must not be affected.");
        }
        finally
        {
            Kill(assigned);
            Kill(control);
        }
    }

    [WindowsFact]
    public void TryAssign_ReturnsFalseForAnExitedProcessWithoutThrowing()
    {
        using var job = ChildProcessJob.Create();
        using var exited = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0")
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        exited.WaitForExit();

        Assert.False(job!.TryAssign(exited));
    }

    [WindowsFact]
    public async Task WslcRunnerPaths_JoinTheSharedJob()
    {
        using var stub = new WslcStub();
        Assert.NotNull(ChildProcessJob.Shared);

        Assert.True(await RunsInSharedJob((script, pidFile) =>
            ProcessRunner.RunNonInteractiveAtPathAsync(script, [pidFile], TimeSpan.FromSeconds(20))));
        Assert.True(await RunsInSharedJob((script, pidFile) => ProcessRunner.RunAtPathAsync(script, [pidFile])));
        var settings = DispatchProxy.Create<ISettingsService, WslcCapabilitiesServiceTests.SettingsProxy>();
        Assert.True(await RunsInSharedJob((script, pidFile) =>
        {
            settings.WslcPath = script;
            return new ProcessRunner(settings).RunWithStdinAsync([pidFile], "secret");
        }));
    }

    [WindowsFact]
    public async Task OtherProcessExecutorCallers_AreNotAddedToTheJob()
    {
        // Installers, az, and devcontainer host commands may leave processes that must outlive the app.
        Assert.False(await RunsInSharedJob((script, pidFile) =>
            ProcessExecutor.RunAsync(ProcessRunner.CreateStartInfo(script, [pidFile], redirectInput: false))));
        Assert.True(await RunsInSharedJob((script, pidFile) =>
            ProcessExecutor.RunAsync(ProcessRunner.CreateStartInfo(script, [pidFile], redirectInput: false),
                killOnAppExit: true)));
    }

    /// <summary>Runs a child that records its PID and lingers, and reports whether it was in the shared job.</summary>
    private static async Task<bool> RunsInSharedJob(Func<string, string, Task<CommandResult>> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wcd-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidFile = Path.Combine(directory, "pid.txt");
        var script = Path.Combine(directory, "report.cmd");
        // The script is cmd.exe itself, so the PowerShell grandchild's parent is the process under test.
        File.WriteAllText(script,
            "@echo off\r\n" +
            "powershell -NoProfile -NonInteractive -Command \"$p = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID); Set-Content -LiteralPath '%~1' -Value $p.ParentProcessId\"\r\n" +
            "ping -n 4 127.0.0.1 >nul\r\n");
        try
        {
            var running = run(script, pidFile);
            var deadline = DateTime.UtcNow + Guard;
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "Child never reported its PID.");
                Assert.False(running.IsCompleted && !File.Exists(pidFile), "Child exited without reporting its PID.");
                await Task.Delay(50);
            }

            await Task.Delay(100);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim()));
            var inJob = ChildProcessJob.Shared!.Contains(child);
            var result = await running.WaitAsync(Guard);
            Assert.True(result.Success, result.ErrorText);
            return inJob;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A child may still be releasing the folder; the temp folder is harmless.
            }
        }
    }

    private static Process StartPing() =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
