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

using System.Diagnostics;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcCopyInputTests
{
    [Theory]
    [InlineData("space name")]
    [InlineData("%PATH%")]
    [InlineData("!PATH!")]
    [InlineData("& echo injection")]
    [InlineData("^caret")]
    [InlineData("(parentheses)")]
    [InlineData("\u00e9")]
    public void ShellMetacharactersRemainEnvironmentData(string component)
    {
        var executable = $@"C:\{component}\wslc.exe";
        var archive = $@"C:\{component}\payload.tar";
        var info = WslcCopyInput.CreateStartInfo(executable, ["container", "cp", "-", "id:/"], archive);
        var baseline = WslcCopyInput.CreateStartInfo(@"C:\wslc.exe", ["container", "cp", "-", "id:/"], @"C:\payload.tar");
        Assert.Equal(baseline.Arguments, info.Arguments);
        Assert.Equal(executable, info.Environment["WCD_CP_EXE"]);
        Assert.Equal(archive, info.Environment["WCD_CP_ARCHIVE"]);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), info.FileName);
        Assert.False(info.RedirectStandardInput);
        Assert.Contains("/d /v:off /s /c", info.Arguments);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    public void UnsafeDelimitersAreRejectedInEveryValue(string delimiter)
    {
        Assert.Throws<ArgumentException>(() => WslcCopyInput.CreateStartInfo(
            $"C:\\{delimiter}\\wslc.exe", ["container", "cp", "-", "id:/"], @"C:\input.tar"));
        Assert.Throws<ArgumentException>(() => WslcCopyInput.CreateStartInfo(
            @"C:\wslc.exe", ["container", "cp", "-", $"id{delimiter}:/"], @"C:\input.tar"));
        Assert.Throws<ArgumentException>(() => WslcCopyInput.CreateStartInfo(
            @"C:\wslc.exe", ["container", "cp", "-", "id:/"], $"C:\\{delimiter}\\input.tar"));
    }

    [Fact]
    public void GenericShellCommandsAreNotAccepted()
    {
        Assert.Throws<ArgumentException>(() => WslcCopyInput.CreateStartInfo(
            @"C:\wslc.exe", ["exec", "id", "sh", "-c"], @"C:\input.tar"));
    }

    [Fact]
    public async Task CancellationKillsAndWaitsForHostProcess()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("[Console]::WriteLine($PID); Start-Sleep -Seconds 60");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int? pid = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessExecutor.RunAsync(info, onLine: line =>
            {
                if (int.TryParse(line, out var parsed))
                {
                    pid = parsed;
                    cancellation.Cancel();
                }
            }, ct: cancellation.Token));
        Assert.NotNull(pid);
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // An exited process may already have been removed from the process table.
        }
    }
}
