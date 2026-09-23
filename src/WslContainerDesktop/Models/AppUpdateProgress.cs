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

public enum AppUpdatePhase
{
    Downloading,

    /// <summary>Downloaded; waiting until Windows will relaunch the app after installing.</summary>
    Preparing,
    Verifying,
    Installing,
}

/// <summary>Progress of an in-app update; <see cref="Fraction"/> is 0–1 while downloading, else null.</summary>
public sealed record AppUpdateProgress(AppUpdatePhase Phase, double? Fraction = null);

/// <summary>
/// What became of an update the previous session started and was closed to install, reported once
/// on the next launch.
/// </summary>
public sealed record AppUpdateOutcome(bool Succeeded, Version TargetVersion, Version CurrentVersion);
