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

using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads the user's registered WSL distributions from the Lxss registry key. WSL containers
/// (wslc) run in their own VM and never need a distribution; only the k3s cluster does, so this
/// gates the Kubernetes feature without launching <c>wsl.exe</c>. The registry is language-neutral,
/// unlike <c>wsl -l</c> output.
/// </summary>
public sealed class WslDistroInventory(ISettingsService settings, ILogger<WslDistroInventory> logger)
{
    private const string LxssKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    /// <summary>Lxss <c>State</c> value for a fully installed distribution.</summary>
    private const int InstalledState = 1;

    /// <summary>Lxss <c>Flags</c> bit (<c>LXSS_DISTRO_FLAGS_VM_MODE</c>) set for WSL 2 distributions.</summary>
    private const int VmModeFlag = 0x8;

    /// <summary>
    /// Chooses the distribution that hosts k3s (the pinned one, or WSL's default) and reports why
    /// it can't be used, if it can't. Returns null when the registry can't be read, so callers
    /// fall back to probing.
    /// </summary>
    public KubernetesHost? ResolveKubernetesHost()
    {
        var distros = Read();
        return distros is null ? null : KubernetesHostCheck.Resolve(distros, settings.WslDistro);
    }

    /// <summary>Names of fully installed distributions, or null when the registry can't be read.</summary>
    public IReadOnlyList<string>? GetInstalledNames() => Read()?.Select(d => d.Name).ToList();

    private List<WslDistroRecord>? Read()
    {
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKeyPath);
            if (lxss is null)
            {
                return [];
            }

            var defaultId = lxss.GetValue("DefaultDistribution") as string;
            var distros = new List<WslDistroRecord>();
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var distro = lxss.OpenSubKey(id);
                if (distro?.GetValue("DistributionName") is not string name || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Skip distributions that are mid-install or mid-uninstall, as `wsl -l` does.
                if (distro.GetValue("State") is int state && state != InstalledState)
                {
                    continue;
                }

                // A missing Flags value is unexpected; don't block k3s on a guess.
                var isWsl2 = distro.GetValue("Flags") is not int flags || (flags & VmModeFlag) != 0;
                var isDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                distros.Add(new WslDistroRecord(name, isWsl2, isDefault));
            }

            return distros;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read registered WSL distributions from the registry.");
            return null;
        }
    }
}
