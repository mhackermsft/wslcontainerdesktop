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
/// Outcome of running the k3s installer script (install or in-place upgrade). Carries the
/// SHA-256 of the installer that was downloaded this run so the caller can pin it
/// (trust-on-first-use), and flags when a stored pin no longer matches so the caller can ask
/// the user to re-approve a changed script.
/// </summary>
public sealed class K3sInstallResult
{
    /// <summary>The underlying process result. On a hash mismatch the script is NOT run and this reflects the aborted attempt.</summary>
    public CommandResult Result { get; init; } = new();

    /// <summary>SHA-256 (lowercase hex) of the installer script downloaded this run, or null if the download failed.</summary>
    public string? InstallerHash { get; init; }

    /// <summary>True when an expected pin was supplied and the downloaded script did not match it (the script was not executed).</summary>
    public bool HashMismatch { get; init; }

    public bool Success => Result.Success;
}
