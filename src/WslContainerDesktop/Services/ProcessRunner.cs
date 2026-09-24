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
using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Runs the wslc.exe CLI and captures its output. All members are thread-safe and async.
/// Every wslc child is added to <see cref="ChildProcessJob.Shared"/> so none outlives the app.
/// </summary>
public sealed class ProcessRunner(ISettingsService settings)
{
    /// <summary>Upper bound for a non-interactive mutation; a hung engine call ends instead of blocking forever.</summary>
    internal static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(10);

    // wslc's ConfirmAction() prompt ends in "[y/N]"; with stdin closed it reads EOF, declines, and exits 0.
    private static readonly Regex ConfirmationPrompt = new(@"\[y/n\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Runs wslc with the given arguments and returns captured output.</summary>
    public Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunAtPathAsync(settings.WslcPath, arguments, cancellationToken);

    /// <summary>
    /// Runs a wslc mutation that must never wait for user input: see <see cref="RunNonInteractiveAtPathAsync"/>.
    /// </summary>
    public Task<CommandResult> RunNonInteractiveAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunNonInteractiveAtPathAsync(settings.WslcPath, arguments, MutationTimeout, cancellationToken);

    /// <summary>
    /// Runs wslc with stdin redirected and immediately closed, so a confirmation prompt reads EOF
    /// instead of waiting on the hidden console nobody can type into. Because a declined prompt still
    /// exits 0 without doing anything, prompt text in the output is reported as a failure. The call
    /// is bounded by <paramref name="timeout"/> (the process tree is killed on expiry).
    /// </summary>
    internal static async Task<CommandResult> RunNonInteractiveAtPathAsync(
        string executablePath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessExecutor.RunAsync(
            CreateStartInfo(executablePath, arguments, redirectInput: true),
            timeout: timeout,
            launchErrorContext: $"Could not launch '{executablePath}'.",
            killOnAppExit: true,
            ct: cancellationToken).ConfigureAwait(false);
        return RejectDeclinedPrompt(result);
    }

    /// <summary>Turns a successful exit that only printed a confirmation prompt into a failure.</summary>
    internal static CommandResult RejectDeclinedPrompt(CommandResult result)
    {
        if (!result.Success ||
            !(ConfirmationPrompt.IsMatch(result.StandardOutput) || ConfirmationPrompt.IsMatch(result.StandardError)))
        {
            return result;
        }

        return new CommandResult
        {
            ExitCode = -1,
            StandardOutput = result.StandardOutput,
            StandardError = "wslc asked for confirmation, which the app cannot answer, so nothing was changed. " +
                "Update WSL Container Desktop or run the command from a terminal.\n\n" +
                (result.StandardError + result.StandardOutput).Trim(),
        };
    }

    /// <summary>
    /// Runs wslc and additionally reports each output line as it arrives (on a reader thread), for
    /// long operations such as pulls. Captured output is still returned.
    /// </summary>
    public Task<CommandResult> RunStreamingAsync(
        IEnumerable<string> arguments,
        Action<string> onLine,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(settings.WslcPath, arguments, onLine, cancellationToken);

    /// <summary>Runs against a captured executable path so a shared probe cannot mix engines.</summary>
    internal static Task<CommandResult> RunAtPathAsync(
        string executablePath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(executablePath, arguments, onLine: null, cancellationToken);

    private static Task<CommandResult> RunCoreAsync(
        string executablePath,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        return ProcessExecutor.RunAsync(
            CreateStartInfo(executablePath, arguments, redirectInput: false),
            onLine: onLine,
            launchErrorContext: $"Could not launch '{executablePath}'.",
            killOnAppExit: true,
            ct: cancellationToken);
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IEnumerable<string> arguments, bool redirectInput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = redirectInput,
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

        return psi;
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
        return ProcessExecutor.RunAsync(
            CreateStartInfo(settings.WslcPath, arguments, redirectInput: true),
            stdin: stdin,
            launchErrorContext: $"Could not launch '{settings.WslcPath}'.",
            killOnAppExit: true,
            ct: cancellationToken);
    }

    /// <summary>Supplies a seekable archive handle, as required by native WSLC copy.</summary>
    internal static Task<CommandResult> RunCopyWithInputFileAtPathAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var psi = WslcCopyInput.CreateStartInfo(executablePath, arguments, inputPath);
        return ProcessExecutor.RunAsync(psi,
            launchErrorContext: $"Could not launch '{executablePath}'.",
            killOnAppExit: true,
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
