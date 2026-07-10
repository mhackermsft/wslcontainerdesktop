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
using System.Text;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Runs the wslc.exe CLI and captures its output. All members are thread-safe and async.
/// </summary>
public sealed class ProcessRunner(ISettingsService settings)
{

    /// <summary>Runs wslc with the given arguments and returns captured output.</summary>
    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = settings.WslcPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        return ProcessExecutor.RunAsync(
            psi,
            launchErrorContext: $"Could not launch '{settings.WslcPath}'.",
            ct: cancellationToken);
    }

    /// <summary>Convenience overload accepting a params array.</summary>
    public Task<CommandResult> RunAsync(params string[] arguments) =>
        RunAsync((IEnumerable<string>)arguments);

    /// <summary>
    /// Runs wslc with the given arguments, writing <paramref name="stdin"/> to the process's
    /// standard input (used for `login --password-stdin` so secrets never appear on a command line).
    /// </summary>
    public Task<CommandResult> RunWithStdinAsync(
        IEnumerable<string> arguments,
        string stdin,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = settings.WslcPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        return ProcessExecutor.RunAsync(
            psi,
            stdin: stdin,
            launchErrorContext: $"Could not launch '{settings.WslcPath}'.",
            ct: cancellationToken);
    }

    /// <summary>
    /// Starts wslc detached in its own console window (used for interactive
    /// sessions such as `exec -it ... bash` or streaming `logs -f`). Launched with
    /// UseShellExecute=false and ArgumentList so arguments are passed as an argv vector
    /// (no hand-rolled command-line quoting); a console-subsystem child spawned from this
    /// GUI process is given its own console window automatically.
    /// </summary>
    public void RunInteractive(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = settings.WslcPath,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process.Start(psi);
    }
}

