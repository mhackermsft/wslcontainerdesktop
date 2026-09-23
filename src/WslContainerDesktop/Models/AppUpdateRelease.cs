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
/// A published GitHub release of the app that carries an installable MSIX for this machine's
/// architecture. Produced only by <see cref="Services.AppUpdateReleaseParser"/>, which has
/// already rejected drafts, pre-releases, unexpected asset names and non-GitHub download hosts.
/// </summary>
public sealed record AppUpdateRelease
{
    /// <summary>Four-part package version (<c>X.Y.Z.0</c>) the release's MSIX must carry.</summary>
    public required Version Version { get; init; }

    /// <summary>Release tag, e.g. <c>v1.9.0</c>.</summary>
    public required string Tag { get; init; }

    /// <summary>Release page on GitHub, for "view release notes".</summary>
    public required Uri ReleasePage { get; init; }

    public required string AssetName { get; init; }

    public required Uri AssetDownloadUrl { get; init; }

    /// <summary>Asset size in bytes as reported by GitHub; the download must match it exactly.</summary>
    public required long AssetSize { get; init; }

    /// <summary>Lower-case hex SHA-256 GitHub computed for the asset, when the API reports one.</summary>
    public string? AssetSha256 { get; init; }

    /// <summary>User-facing version, e.g. <c>1.9.0</c>.</summary>
    public string DisplayVersion => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}
