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

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Host-level WSL virtual-machine operations. Reads distro/engine virtual disks straight from the
/// filesystem and the Lxss registry, and shells out to <c>wsl.exe</c> and <c>diskpart.exe</c> for
/// platform info, shutdown, and vhdx compaction. All calls go through <see cref="ProcessExecutor"/>.
/// </summary>
public sealed class WslSystemService(IWslcService wslc, ILogger<WslSystemService> logger) : IWslSystemService
{
    private const string LxssKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED — user declined the UAC prompt.

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

    // ---- Virtual disks -------------------------------------------------

    public Task<IReadOnlyList<WslVirtualDisk>> ListVirtualDisksAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<WslVirtualDisk>>(() => ListVirtualDisks(), ct);

    private List<WslVirtualDisk> ListVirtualDisks()
    {
        var disks = new List<WslVirtualDisk>();

        // Registered distros: HKCU\...\Lxss\<guid> → DistributionName + BasePath\ext4.vhdx.
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKeyPath);
            if (lxss is not null)
            {
                foreach (var guid in lxss.GetSubKeyNames())
                {
                    using var entry = lxss.OpenSubKey(guid);
                    var name = entry?.GetValue("DistributionName") as string;
                    var basePath = entry?.GetValue("BasePath") as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(basePath))
                    {
                        continue;
                    }

                    // BasePath is often prefixed with the \\?\ extended-length marker.
                    if (basePath.StartsWith(@"\\?\", StringComparison.Ordinal))
                    {
                        basePath = basePath[4..];
                    }

                    var vhdx = Path.Combine(basePath, "ext4.vhdx");
                    if (TryGetSize(vhdx, out var size))
                    {
                        disks.Add(new WslVirtualDisk
                        {
                            Name = name,
                            Kind = WslDiskKind.Distro,
                            VhdxPath = vhdx,
                            SizeBytes = size,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate WSL distro disks from the registry.");
        }

        // wslc container engine storage: %LOCALAPPDATA%\wslc\sessions\<session>\storage.vhdx.
        try
        {
            var sessionsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wslc", "sessions");
            if (Directory.Exists(sessionsRoot))
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(sessionsRoot))
                {
                    var vhdx = Path.Combine(sessionDir, "storage.vhdx");
                    if (TryGetSize(vhdx, out var size))
                    {
                        var sessionName = Path.GetFileName(sessionDir);
                        disks.Add(new WslVirtualDisk
                        {
                            Name = $"Container engine storage ({sessionName})",
                            Kind = WslDiskKind.EngineStorage,
                            VhdxPath = vhdx,
                            SizeBytes = size,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate wslc engine storage disks.");
        }

        return disks
            .OrderByDescending(d => d.Kind == WslDiskKind.EngineStorage)
            .ThenByDescending(d => d.SizeBytes)
            .ToList();
    }

    private static bool TryGetSize(string path, out long size)
    {
        size = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            size = info.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- Shutdown / compaction ----------------------------------------

    public Task<CommandResult> ShutdownWslAsync(CancellationToken ct = default) =>
        RunWslAsync(ct, "--shutdown");

    public async Task<WslCompactResult> CompactAsync(WslVirtualDisk disk, CancellationToken ct = default)
    {
        if (!TryGetSize(disk.VhdxPath, out var before))
        {
            return new WslCompactResult { Success = false, Message = "The virtual disk file was not found." };
        }

        // Release the file: terminate the wslc session (engine storage) and shut WSL down (distros).
        // Both are best-effort; the compaction reports a clear diskpart error if the file is still held.
        try
        {
            await wslc.RestartSessionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "wslc session terminate before compaction failed (continuing).");
        }

        var shutdown = await ShutdownWslAsync(ct).ConfigureAwait(false);
        if (!shutdown.Success)
        {
            logger.LogDebug("wsl --shutdown before compaction reported: {Error}", shutdown.ErrorText);
        }

        // Give the VM host a moment to release the vhdx handles before diskpart attaches it.
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        var scriptPath = WriteDiskpartScript(disk.VhdxPath);
        try
        {
            var (exitCode, cancelled) = await RunDiskpartElevatedAsync(scriptPath, ct).ConfigureAwait(false);
            if (cancelled)
            {
                return new WslCompactResult
                {
                    Success = false,
                    Cancelled = true,
                    Message = "Compaction was cancelled at the administrator prompt.",
                };
            }

            TryGetSize(disk.VhdxPath, out var after);
            var freed = Math.Max(0, before - after);

            if (exitCode != 0)
            {
                return new WslCompactResult
                {
                    Success = false,
                    FreedBytes = freed,
                    Message = $"diskpart exited with code {exitCode}. The disk may have been in use; " +
                        "try closing running containers and retrying.",
                };
            }

            return new WslCompactResult { Success = true, FreedBytes = freed, Message = "Compaction complete." };
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    /// <summary>Writes a diskpart script that attaches the vhdx read-only, compacts it, and detaches.</summary>
    private static string WriteDiskpartScript(string vhdxPath)
    {
        var script = string.Join(Environment.NewLine,
            $"select vdisk file=\"{vhdxPath}\"",
            "attach vdisk readonly",
            "compact vdisk",
            "detach vdisk",
            "exit",
            string.Empty);

        var dir = ResolveScratchDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"wcd-compact-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// A real (non-redirected) directory the elevated diskpart child can read. Mirrors the log
    /// directory resolution so the packaged app hands out the concrete container path.
    /// </summary>
    private static string ResolveScratchDirectory()
    {
        try
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "WslContainerDesktop");
        }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WslContainerDesktop");
        }
    }

    /// <summary>
    /// Runs diskpart with the given script under elevation (UAC). Output cannot be captured through
    /// the shell-execute/runas launch, so the caller measures the vhdx size delta instead. Returns
    /// the exit code and whether the user declined the elevation prompt.
    /// </summary>
    private async Task<(int ExitCode, bool Cancelled)> RunDiskpartElevatedAsync(string scriptPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "diskpart.exe",
            Arguments = $"/s \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, false);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (-1, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "diskpart elevated launch failed.");
            return (-1, false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of a scratch file; ignore.
        }
    }

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
