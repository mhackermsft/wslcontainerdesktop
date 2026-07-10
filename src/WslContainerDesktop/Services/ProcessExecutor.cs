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
/// Single, shared implementation of the "spawn a child process, capture stdout/stderr, and wait
/// for exit" pattern used by every service that shells out (wslc, wsl.exe, az). Centralizing it
/// here removes the near-identical copies that had drifted apart (only some had a timeout, only
/// some killed the process on cancellation) and gives one place to get encoding, output draining,
/// timeout, and cancel-kill semantics right.
/// </summary>
public static class ProcessExecutor
{
    /// <summary>
    /// Runs the process described by <paramref name="psi"/> to completion and returns its exit
    /// code and captured output.
    /// </summary>
    /// <param name="psi">
    /// A fully-configured start info. The caller is responsible for redirection flags; when
    /// <see cref="ProcessStartInfo.RedirectStandardInput"/> is set, this method writes
    /// <paramref name="stdin"/> (if any) and then closes the input stream.
    /// </param>
    /// <param name="stdin">Text to write to standard input, or null to write nothing.</param>
    /// <param name="onLine">
    /// Optional callback invoked on the reader thread for each stdout/stderr line as it arrives
    /// (used for streaming progress). Captured output is always returned regardless.
    /// </param>
    /// <param name="timeout">Optional hard timeout; on expiry the process tree is killed.</param>
    /// <param name="launchErrorContext">
    /// Prefix for the error text when the process fails to start (e.g. "Could not launch az.").
    /// </param>
    /// <param name="ct">Caller cancellation. On external cancellation the process tree is killed and the exception rethrown.</param>
    public static async Task<CommandResult> RunAsync(
        ProcessStartInfo psi,
        string? stdin = null,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        string launchErrorContext = "Could not launch the process.",
        CancellationToken ct = default)
    {
        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
                onLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
                onLine?.Invoke(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new CommandResult { ExitCode = -1, StandardError = launchErrorContext };
            }
        }
        catch (Exception ex)
        {
            return new CommandResult { ExitCode = -1, StandardError = $"{launchErrorContext} {ex.Message}" };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (psi.RedirectStandardInput)
        {
            try
            {
                if (!string.IsNullOrEmpty(stdin))
                {
                    await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }
            catch
            {
                // The process may have already exited and closed the pipe; the exit code still
                // reflects the outcome, so ignore the write/close race.
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            linked.CancelAfter(t);
        }

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            // Distinguish a caller-requested cancellation (rethrow) from a timeout (report it).
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new CommandResult { ExitCode = -1, StandardError = "The command timed out." };
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }
}
