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
public sealed class ProcessRunner
{
    private readonly ISettingsService _settings;

    public ProcessRunner(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Runs wslc with the given arguments and returns captured output.</summary>
    public async Task<CommandResult> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _settings.WslcPath,
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

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StandardError = "Failed to start the wslc process.",
                };
            }
        }
        catch (Exception ex)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StandardError =
                    $"Could not launch '{_settings.WslcPath}'. {ex.Message}",
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

            throw;
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }

    /// <summary>Convenience overload accepting a params array.</summary>
    public Task<CommandResult> RunAsync(params string[] arguments) =>
        RunAsync((IEnumerable<string>)arguments);

    /// <summary>
    /// Runs wslc with the given arguments, writing <paramref name="stdin"/> to the process's
    /// standard input (used for `login --password-stdin` so secrets never appear on a command line).
    /// </summary>
    public async Task<CommandResult> RunWithStdinAsync(
        IEnumerable<string> arguments,
        string stdin,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _settings.WslcPath,
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

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
            {
                return new CommandResult { ExitCode = -1, StandardError = "Failed to start the wslc process." };
            }
        }
        catch (Exception ex)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StandardError = $"Could not launch '{_settings.WslcPath}'. {ex.Message}",
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch
        {
            // If the process already exited, the pipe write can throw; the exit code still tells the story.
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
            throw;
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }

    /// <summary>
    /// Starts wslc detached in its own console window (used for interactive
    /// sessions such as `exec -it ... bash` or streaming `logs -f`).
    /// </summary>
    public void RunInteractive(IEnumerable<string> arguments)
    {
        var argLine = string.Join(' ', arguments.Select(Quote));

        var psi = new ProcessStartInfo
        {
            FileName = _settings.WslcPath,
            Arguments = argLine,
            UseShellExecute = true,
            CreateNoWindow = false,
        };

        Process.Start(psi);
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;
}
