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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Outcome of choosing the WSL distribution that hosts k3s.</summary>
/// <param name="Problem">Why no distribution can host k3s, or <see cref="KubernetesHostProblem.None"/>.</param>
/// <param name="DistroName">The distribution k3s would run in (or the one that was rejected), if any.</param>
public sealed record KubernetesHost(KubernetesHostProblem Problem, string? DistroName)
{
    /// <summary>True when <see cref="DistroName"/> can host k3s.</summary>
    public bool CanHost => Problem == KubernetesHostProblem.None;
}

/// <summary>
/// Pure rules for which WSL distribution hosts k3s. k3s commands run in the pinned distribution
/// when one is configured, otherwise in WSL's default distribution (what <c>wsl.exe</c> uses with
/// no <c>-d</c>). That distribution must be WSL 2, and must not belong to another tool.
/// </summary>
public static class KubernetesHostCheck
{
    /// <summary>
    /// Distributions created and managed by other tools. Installing k3s into one could break that
    /// tool, and they are minimal images without systemd.
    /// </summary>
    private static readonly string[] ManagedNames =
    [
        "docker-desktop",
        "docker-desktop-data",
        "rancher-desktop",
        "rancher-desktop-data",
    ];

    /// <summary>Picks the host distribution for k3s and reports why it can't be used, if it can't.</summary>
    /// <param name="distros">Installed distributions.</param>
    /// <param name="pinnedDistro">The distribution configured for k3s, or null/empty for WSL's default.</param>
    public static KubernetesHost Resolve(IReadOnlyCollection<WslDistroRecord> distros, string? pinnedDistro)
    {
        var pinned = string.IsNullOrWhiteSpace(pinnedDistro) ? null : pinnedDistro.Trim();
        WslDistroRecord? host;
        if (pinned is not null)
        {
            host = distros.FirstOrDefault(d => string.Equals(d.Name, pinned, StringComparison.OrdinalIgnoreCase));
            if (host is null)
            {
                return new KubernetesHost(KubernetesHostProblem.PinnedMissing, pinned);
            }
        }
        else
        {
            if (distros.Count == 0)
            {
                return new KubernetesHost(KubernetesHostProblem.NoDistributions, null);
            }

            host = distros.FirstOrDefault(d => d.IsDefault);
            if (host is null)
            {
                return new KubernetesHost(KubernetesHostProblem.NoDefault, null);
            }
        }

        if (IsManagedByTool(host.Name))
        {
            return new KubernetesHost(KubernetesHostProblem.ManagedByTool, host.Name);
        }

        return host.IsWsl2
            ? new KubernetesHost(KubernetesHostProblem.None, host.Name)
            : new KubernetesHost(KubernetesHostProblem.Wsl1, host.Name);
    }

    /// <summary>True for a distribution that Docker Desktop, Rancher Desktop or Podman owns.</summary>
    public static bool IsManagedByTool(string name) =>
        ManagedNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith("podman-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Headline for the Kubernetes page when no distribution can host k3s.</summary>
    public static string Title(KubernetesHostProblem problem) => problem switch
    {
        KubernetesHostProblem.Wsl1 or KubernetesHostProblem.ManagedByTool =>
            "Kubernetes can't use this WSL distribution",
        _ => "Kubernetes needs a WSL distribution",
    };

    /// <summary>One-line status for the page header.</summary>
    public static string Summary(KubernetesHostProblem problem, string? distro) => problem switch
    {
        KubernetesHostProblem.PinnedMissing => $"Kubernetes (k3s) is set to use WSL distribution \"{distro}\", which isn't installed.",
        KubernetesHostProblem.NoDefault => "Kubernetes (k3s) needs a default WSL distribution.",
        KubernetesHostProblem.Wsl1 => $"Kubernetes (k3s) needs WSL 2, and \"{distro}\" uses WSL 1.",
        KubernetesHostProblem.ManagedByTool => $"Kubernetes (k3s) can't run in \"{distro}\", which another tool manages.",
        _ => "Kubernetes (k3s) needs a WSL distribution, and none is installed.",
    };

    /// <summary>Full explanation, including what to do, for the Kubernetes page.</summary>
    /// <param name="problem">Why the distribution can't host k3s.</param>
    /// <param name="distro">The rejected or missing distribution, if any.</param>
    /// <param name="pinned">True when the distribution was configured for k3s rather than WSL's default.</param>
    public static string Explain(KubernetesHostProblem problem, string? distro, bool pinned)
    {
        var otherwise = pinned
            ? "or use WSL's default distribution instead"
            : "or make a different WSL 2 distribution the default (wsl --set-default <name>)";
        var reason = problem switch
        {
            KubernetesHostProblem.PinnedMissing =>
                $"The k3s cluster is set to run in the WSL distribution \"{distro}\", which isn't installed. " +
                "Reinstall it, or use WSL's default distribution instead.",
            KubernetesHostProblem.NoDefault =>
                "k3s runs in WSL's default distribution, and none is set. Choose one with " +
                "wsl --set-default <name>, then refresh.",
            KubernetesHostProblem.Wsl1 =>
                $"k3s needs WSL 2, and \"{distro}\" uses WSL 1. Convert it with " +
                $"wsl --set-version {distro} 2, {otherwise}, then refresh.",
            KubernetesHostProblem.ManagedByTool =>
                $"\"{distro}\" is managed by another tool (such as Docker Desktop) and can't host k3s. " +
                $"Install a distribution such as Ubuntu (wsl --install Ubuntu), {otherwise}, then refresh.",
            _ =>
                "The k3s cluster runs inside a WSL Linux distribution, and none is installed. To use " +
                "Kubernetes, install one (for example, run wsl --install Ubuntu), then refresh.",
        };

        return reason + " Containers aren't affected: WSL containers run in their own VM and don't need a distribution.";
    }
}
