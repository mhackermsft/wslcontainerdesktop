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

/// <summary>
/// Checks GitHub for a newer release of the app and installs it in place: downloads the MSIX,
/// verifies it is a newer build of this package signed by the same certificate, then asks Windows
/// to close the app, install the update and relaunch it.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Installed package version, or null when the app runs without package identity.</summary>
    Version? CurrentVersion { get; }

    /// <summary>
    /// True when this installation can replace itself: it is a signed release install rather than a
    /// development registration of loose files.
    /// </summary>
    bool CanInstallInPlace { get; }

    /// <summary>Returns the newest release when it is newer than this installation, otherwise null.</summary>
    /// <exception cref="AppUpdateException">GitHub could not be asked or the app has no package identity.</exception>
    Task<AppUpdateRelease?> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads, verifies and installs <paramref name="release"/>. Once installation starts Windows
    /// closes this process, so on success this method normally never returns; it returns or throws
    /// only if the update could not be applied. <paramref name="beforeInstall"/> runs on the caller's
    /// synchronization context after every check has passed, immediately before the app is closed.
    /// </summary>
    /// <exception cref="AppUpdateException">Any step failed; the running app is unchanged.</exception>
    Task InstallAsync(AppUpdateRelease release, IProgress<AppUpdateProgress>? progress, Action? beforeInstall, CancellationToken ct = default);

    /// <summary>
    /// Reports, once, what became of an update the previous session was closed to install, and
    /// removes any downloaded packages left behind. Null when no update was pending.
    /// </summary>
    AppUpdateOutcome? CompleteLaunch();
}
