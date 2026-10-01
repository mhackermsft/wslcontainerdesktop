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

namespace WslContainerDesktop.Models;

/// <summary>Lifecycle state of the local k3s single-node cluster.</summary>
public enum ClusterState
{
    /// <summary>Could not determine (e.g. WSL not reachable).</summary>
    Unknown,

    /// <summary>k3s is not installed in the WSL distro.</summary>
    NotInstalled,

    /// <summary>An install or uninstall operation is in progress.</summary>
    Working,

    /// <summary>k3s is installed but the service is not running.</summary>
    Stopped,

    /// <summary>k3s is running and the node is ready.</summary>
    Running,

    /// <summary>
    /// No WSL distribution can host k3s (see <see cref="ClusterStatus.HostProblem"/>). Containers
    /// don't need a distribution; only k3s does.
    /// </summary>
    NoDistribution,
}

/// <summary>Snapshot of the cluster's install/run status.</summary>
public sealed class ClusterStatus
{
    /// <summary>Gets or sets the state.</summary>
    public ClusterState State { get; init; } = ClusterState.Unknown;
    /// <summary>Gets or sets the node name.</summary>
    public string NodeName { get; init; } = "-";
    /// <summary>Gets or sets the kubernetes version.</summary>
    public string KubernetesVersion { get; init; } = "-";
    /// <summary>Gets or sets the distro.</summary>
    public string Distro { get; init; } = "-";
    /// <summary>Gets or sets the message.</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>Why no distribution can host k3s when <see cref="State"/> is <see cref="ClusterState.NoDistribution"/>.</summary>
    public KubernetesHostProblem HostProblem { get; init; }

    /// <summary>Gets a value indicating whether this value is installed.</summary>
    public bool IsInstalled => State is ClusterState.Stopped or ClusterState.Running;
    /// <summary>Gets a value indicating whether this value is running.</summary>
    public bool IsRunning => State == ClusterState.Running;
}

/// <summary>Lightweight cluster snapshot for the nav footer indicator.</summary>
public sealed class K8sFooterStatus
{
    /// <summary>Gets or sets the state.</summary>
    public ClusterState State { get; init; } = ClusterState.Unknown;
    /// <summary>Gets or sets the pods running.</summary>
    public int PodsRunning { get; init; }
    /// <summary>Gets or sets the pods total.</summary>
    public int PodsTotal { get; init; }
}
