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
using System.Security;

namespace WslContainerDesktop.Services;

internal sealed record WslcExecutableIdentity(
    string ConfiguredPath,
    string ExecutablePath,
    long Length = 0,
    long LastWriteTicks = 0,
    long CreationTicks = 0,
    string? FileVersion = null,
    string? Diagnostic = null)
{
    internal static WslcExecutableIdentity Read(string configuredPath)
    {
        try
        {
            var path = ResolvePath(configuredPath);
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new(configuredPath, path, Diagnostic:
                    $"WSLC executable not found at '{path}'. Check the WSLC path in Settings.");
            }

            if (file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
            {
                file = target;
            }

            return new(configuredPath, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks,
                file.CreationTimeUtc.Ticks, FileVersionInfo.GetVersionInfo(file.FullName).FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or
            ArgumentException or NotSupportedException or SecurityException)
        {
            return new(configuredPath, configuredPath, Diagnostic:
                $"Could not inspect WSLC executable '{configuredPath}': {ex.Message} Check the WSLC path in Settings.");
        }
    }

    private static string ResolvePath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(configuredPath)))
        {
            configuredPath += ".exe";
        }

        if (Path.IsPathRooted(configuredPath) ||
            configuredPath.Contains(Path.DirectorySeparatorChar) ||
            configuredPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.GetFullPath(configuredPath);
        }

        // Resolve bare names before launching so all help commands use the same binary.
        var directories = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var directory in directories.Where(directory => !string.IsNullOrWhiteSpace(directory)))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), configuredPath));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configuredPath);
    }
}
