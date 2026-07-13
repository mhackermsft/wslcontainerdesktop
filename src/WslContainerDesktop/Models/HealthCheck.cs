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

/// <summary>How the watchdog probes a container's in-workload health.</summary>
public enum HealthProbeKind
{
    /// <summary>Run a command inside the container (`wslc exec`); exit 0 means healthy.</summary>
    Command = 0,

    /// <summary>Connect to a published host port; a successful TCP connect means healthy.</summary>
    Tcp = 1,
}

/// <summary>
/// Rolled-up health of a container's workload as evaluated by the watchdog. The numeric order
/// is significant: a higher value is "worse", so the tray/list roll-up can take the maximum.
/// </summary>
public enum ContainerHealthState
{
    /// <summary>No probe result yet, or the container is not running.</summary>
    Unknown = 0,

    /// <summary>The last probe succeeded.</summary>
    Healthy = 1,

    /// <summary>A probe failed and the watchdog is auto-restarting within its budget.</summary>
    Degraded = 2,

    /// <summary>Probing keeps failing after the restart budget was exhausted (or alert-only).</summary>
    Down = 3,
}

/// <summary>
/// Per-container health probe and restart policy. Keyed by container <see cref="ContainerName"/>
/// so it survives stop/start and recreation. Persisted by <c>SettingsService</c>.
/// </summary>
public sealed class HealthCheckConfig
{
    /// <summary>Smallest allowed probe interval, in seconds.</summary>
    public const int MinIntervalSeconds = 5;

    /// <summary>Largest allowed probe interval, in seconds.</summary>
    public const int MaxIntervalSeconds = 3600;

    /// <summary>Upper bound for the restart budget offered in the UI.</summary>
    public const int MaxRestartLimit = 20;

    public string ContainerName { get; set; } = string.Empty;

    public HealthProbeKind Kind { get; set; } = HealthProbeKind.Command;

    /// <summary>Shell command run inside the container for <see cref="HealthProbeKind.Command"/>.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Published host port dialed for <see cref="HealthProbeKind.Tcp"/>.</summary>
    public int TcpPort { get; set; }

    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Number of auto-restarts on unhealthy before giving up. 0 = alert only.</summary>
    public int MaxRestarts { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    /// <summary>Clamped probe interval, guarding against out-of-range persisted values.</summary>
    public int EffectiveIntervalSeconds =>
        Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);

    /// <summary>True when the probe is fully specified for its kind.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ContainerName) &&
        (Kind == HealthProbeKind.Command
            ? !string.IsNullOrWhiteSpace(Command)
            : TcpPort is > 0 and <= 65535);
}

/// <summary>Immutable health result for a single container, published by the watchdog.</summary>
public sealed class ContainerHealthSnapshot
{
    public string ContainerName { get; init; } = string.Empty;
    public ContainerHealthState State { get; init; }
    public int RestartCount { get; init; }
    public int MaxRestarts { get; init; }
    public string Detail { get; init; } = string.Empty;
}
