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
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Host-level WSL virtual-machine operations. Reads distro/engine virtual disks straight from the
/// filesystem and the Lxss registry, and shells out to <c>wsl.exe</c> for platform info, distro
/// disk-usage measurement, and shutdown. All calls go through <see cref="ProcessExecutor"/>.
/// </summary>
public sealed class WslSystemService(ILogger<WslSystemService> logger) : IWslSystemService
{
    private const string LxssKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    // ---- Configuration -------------------------------------------------

    public Task<WslConfigInfo> ReadConfigAsync(CancellationToken ct = default) =>
        Task.Run(() => ReadConfig(), ct);

    private WslConfigInfo ReadConfig()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wslconfig");

        if (!File.Exists(path))
        {
            return new WslConfigInfo { ConfigPath = path, Exists = false };
        }

        string? memory = null, processors = null, swap = null;
        try
        {
            var section = string.Empty;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim().ToLowerInvariant();
                    continue;
                }

                if (section != "wsl2")
                {
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = line[..eq].Trim().ToLowerInvariant();
                var value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "memory":
                        memory = value;
                        break;
                    case "processors":
                        processors = value;
                        break;
                    case "swap":
                        swap = value;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse .wslconfig at {Path}.", path);
        }

        return new WslConfigInfo
        {
            ConfigPath = path,
            Exists = true,
            Memory = memory,
            Processors = processors,
            Swap = swap,
        };
    }

    // ---- Platform info -------------------------------------------------

    public async Task<WslPlatformInfo> GetPlatformInfoAsync(CancellationToken ct = default)
    {
        var wslVersion = string.Empty;
        var kernel = string.Empty;
        var distros = new List<WslDistroStatus>();

        try
        {
            var version = await RunWslAsync(ct, "--version").ConfigureAwait(false);
            foreach (var line in version.StandardOutput.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var label = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (label.Contains("wsl") && wslVersion.Length == 0)
                {
                    wslVersion = value;
                }
                else if (label.Contains("kernel") && kernel.Length == 0)
                {
                    kernel = value;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "wsl --version failed.");
        }

        try
        {
            var list = await RunWslAsync(ct, "-l", "-v").ConfigureAwait(false);
            distros = ParseDistroList(list.StandardOutput);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "wsl -l -v failed.");
        }

        return new WslPlatformInfo { WslVersion = wslVersion, KernelVersion = kernel, Distros = distros };
    }

    /// <summary>Parses the tabular output of <c>wsl -l -v</c> into distro rows.</summary>
    internal static List<WslDistroStatus> ParseDistroList(string output)
    {
        var result = new List<WslDistroStatus>();
        var lines = output.Split('\n');
        var seenHeader = false;

        foreach (var raw in lines)
        {
            var line = raw.Replace('\r', ' ').TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            // The header row starts with NAME (after an optional leading marker column).
            if (!seenHeader)
            {
                seenHeader = true;
                if (line.Contains("NAME", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var isDefault = line.TrimStart().StartsWith('*');
            var tokens = line.Replace("*", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            var version = int.TryParse(tokens[^1], out var v) ? v : 0;
            var state = tokens[^2];
            var name = string.Join(' ', tokens[..^2]);

            result.Add(new WslDistroStatus
            {
                Name = name,
                State = state,
                Version = version,
                IsDefault = isDefault,
            });
        }

        return result;
    }

    // ---- Shutdown ------------------------------------------------------

    public Task<CommandResult> ShutdownWslAsync(CancellationToken ct = default) =>
        RunWslAsync(ct, "--shutdown");

    // ---- wsl.exe plumbing ---------------------------------------------

    private static Task<CommandResult> RunWslAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Force wsl.exe to emit UTF-8 rather than its default UTF-16LE.
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return ProcessExecutor.RunAsync(psi, launchErrorContext: "Could not launch wsl.exe.", ct: ct);
    }
}
