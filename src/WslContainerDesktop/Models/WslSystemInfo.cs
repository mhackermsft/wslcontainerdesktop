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

/// <summary>One registered WSL distro and its current run state (from <c>wsl -l -v</c>).</summary>
public sealed class WslDistroStatus
{
    /// <summary>Distro name, e.g. "Ubuntu".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Run state as reported by WSL, e.g. "Running" or "Stopped".</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>WSL version backing the distro (1 or 2).</summary>
    public int Version { get; init; }

    /// <summary>True when this is the default distro (the one marked <c>*</c>).</summary>
    public bool IsDefault { get; init; }

    /// <summary>True when the distro is currently running.</summary>
    public bool IsRunning => State.Equals("Running", System.StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of checking for an available WSL update by comparing the installed WSL app version
/// against the latest release published on the <c>microsoft/WSL</c> GitHub repository. There is no
/// offline "check only" mode in <c>wsl --update</c>, so availability is inferred from that release
/// feed.
/// </summary>
public sealed class WslUpdateInfo
{
    /// <summary>The installed WSL app version (e.g. "2.9.4.0"), or empty when it can't be read.</summary>
    public string InstalledVersion { get; init; } = string.Empty;

    /// <summary>The latest available version for the selected channel (e.g. "2.9.5"), or empty when unknown.</summary>
    public string LatestVersion { get; init; } = string.Empty;

    /// <summary>True when a newer WSL version is available than the one installed.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>True when the check considered pre-release versions.</summary>
    public bool IncludedPreRelease { get; init; }

    /// <summary>True when the availability check could not be completed (e.g. no network).</summary>
    public bool CheckFailed { get; init; }

    /// <summary>Human-readable reason the check failed, when <see cref="CheckFailed"/> is true.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Host-level WSL platform information (<c>wsl --version</c>): the WSL app version and the
/// Linux kernel version. Empty strings mean the value could not be determined.
/// </summary>
public sealed class WslPlatformInfo
{
    /// <summary>WSL app version, e.g. "2.9.3.0".</summary>
    public string WslVersion { get; init; } = string.Empty;

    /// <summary>Linux kernel version, e.g. "6.18.35.2-1".</summary>
    public string KernelVersion { get; init; } = string.Empty;

    /// <summary>The registered distros and their run state.</summary>
    public IReadOnlyList<WslDistroStatus> Distros { get; init; } = System.Array.Empty<WslDistroStatus>();
}
