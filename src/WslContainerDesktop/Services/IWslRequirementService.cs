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

namespace WslContainerDesktop.Services;

/// <summary>
/// High-level readiness state for the installed WSL and <c>wslc.exe</c> command-line tools.
/// </summary>
public enum WslRequirementState
{
    /// <summary>Required WSL and WSLC versions are present.</summary>
    Ok,
    /// <summary>A required WSL component or <c>wslc.exe</c> was not found.</summary>
    NotInstalled,
    /// <summary>WSL or WSLC is installed but older than the app requires.</summary>
    TooOld,
    /// <summary>WSL is present but policy or configuration prevents use.</summary>
    DisabledByPolicy,
    /// <summary>The app could not determine the WSL requirement state.</summary>
    Unknown,
}

/// <summary>
/// Result of checking whether the local machine satisfies the WSL container preview requirement.
/// </summary>
public sealed record WslRequirementStatus(
    WslRequirementState State,
    string? FoundVersion = null,
    string? Diagnostic = null)
{
    /// <summary>Creates a successful requirement status for the detected WSLC version.</summary>
    public static WslRequirementStatus Ok(string foundVersion) => new(WslRequirementState.Ok, foundVersion);
}

/// <summary>
/// Checks the installed WSL and WSLC versions before features attempt to run engine commands.
/// </summary>
public interface IWslRequirementService
{
    /// <summary>Most recent requirement status, which may still be unknown before the first check completes.</summary>
    WslRequirementStatus Current { get; }

    /// <summary>True once the startup requirement check has finished at least once.</summary>
    bool HasCompletedInitialCheck { get; }

    /// <summary>Raised when a requirement check produces a new status.</summary>
    event EventHandler<WslRequirementStatus>? Changed;

    /// <summary>Runs the requirement check again and updates <see cref="Current"/>.</summary>
    Task<WslRequirementStatus> RecheckAsync(CancellationToken ct = default);
}
