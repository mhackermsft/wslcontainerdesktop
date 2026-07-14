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

/// <summary>
/// A per-container restart policy the desktop app enforces for containers that have <em>no</em>
/// health check (health-checked containers are supervised by the <c>HealthWatchdog</c> instead).
/// Keyed by container name so it survives stop/start and recreation; persisted by <c>SettingsService</c>.
/// Enforced only while the app is running — see <c>RestartPolicyWatchdog</c>.
/// </summary>
public sealed class RestartPolicyConfig
{
    /// <summary>Upper bound for how many auto-restarts are attempted before giving up.</summary>
    public const int MaxRestartLimit = 20;

    public string ContainerName { get; set; } = string.Empty;

    /// <summary>The compose restart policy to enforce.</summary>
    public RestartPolicyKind Policy { get; set; } = RestartPolicyKind.No;

    /// <summary>Restart budget (rolling): attempts before the watchdog gives up. 0 disables restarts.</summary>
    public int MaxRestarts { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    /// <summary>True when the policy is actionable (a named container and a policy other than "no").</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ContainerName) && Policy != RestartPolicyKind.No;
}
