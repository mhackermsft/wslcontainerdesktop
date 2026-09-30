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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

namespace WslContainerDesktop.Services;

/// <summary>Probe failures and startup grace, deliberately independent of the autoheal restart budget.</summary>
public sealed class HealthProbeProgress
{
    /// <summary>Number of failing probe results since the last successful probe.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Whether this container has ever reported healthy during the current run.</summary>
    public bool HasSucceeded { get; private set; }

    /// <summary>When the current run began; used to honor Compose's startup grace period.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Starts a fresh observation window for a newly started container.</summary>
    public void Reset(DateTimeOffset startedAt)
    {
        StartedAt = startedAt;
        ConsecutiveFailures = 0;
        HasSucceeded = false;
    }

    /// <summary>Records one probe result and decides whether it should count as unhealthy.</summary>
    /// <returns>True only when an unhealthy threshold has been reached.</returns>
    public bool Record(bool healthy, bool native, int retries, TimeSpan startPeriod, DateTimeOffset now)
    {
        if (healthy)
        {
            HasSucceeded = true;
            ConsecutiveFailures = 0;
            return false;
        }
        if (!native && !HasSucceeded && now - StartedAt < startPeriod)
            return false;
        ConsecutiveFailures++;
        return native || ConsecutiveFailures >= Math.Max(1, retries);
    }
}
