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

// Supervisor tests exercise the real orchestration with controlled snapshots, not WinUI pollers.
public sealed class HealthWatchdog
{
    public HealthSnapshot Latest { get; set; } = new([]);
    public sealed record HealthSnapshot(IReadOnlyList<ContainerHealthSnapshot> Containers);
}

public sealed class StatusMonitor
{
    public StatusSnapshot? Latest { get; set; }
    public Action? RefreshRequested { get; set; }
    public void RequestRefresh() => RefreshRequested?.Invoke();
    public sealed record StatusSnapshot(IReadOnlyList<ContainerInfo> Containers);
}
