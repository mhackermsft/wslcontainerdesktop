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

/// <summary>Why no WSL distribution can host the k3s cluster.</summary>
public enum KubernetesHostProblem
{
    /// <summary>A suitable distribution exists.</summary>
    None,

    /// <summary>No WSL distributions are installed.</summary>
    NoDistributions,

    /// <summary>The distribution configured for k3s isn't installed.</summary>
    PinnedMissing,

    /// <summary>Distributions exist but WSL has no default one.</summary>
    NoDefault,

    /// <summary>The distribution uses WSL 1; k3s needs WSL 2.</summary>
    Wsl1,

    /// <summary>The distribution belongs to another tool (Docker Desktop, Rancher Desktop, Podman).</summary>
    ManagedByTool,
}
