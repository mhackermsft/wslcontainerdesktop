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

/// <summary>Reads and edits the <c>wslc</c> settings file used by the WSL containers CLI.</summary>
public interface IWslcSettingsFileService
{
    /// <summary>Reads the current CLI settings file, returning defaults when it is absent.</summary>
    Task<WslcSettingsFile> ReadAsync(CancellationToken ct = default);

    /// <summary>Updates the CLI storage path setting, or clears it when <paramref name="path"/> is null.</summary>
    Task SetStoragePathAsync(string? path, CancellationToken ct = default);

    /// <summary>Validates a proposed host storage path before writing it to the CLI settings file.</summary>
    WslcStoragePathValidation ValidateStoragePath(string path);

    /// <summary>Returns the size of the storage disk file at a configured storage path when it exists.</summary>
    Task<long?> GetStorageDiskBytesAsync(string storagePath, CancellationToken ct = default);
}

/// <summary>Result of validating a candidate <c>wslc</c> storage directory.</summary>
/// <param name="IsValid">Whether the path is safe for the CLI settings file.</param>
/// <param name="Message">User-facing reason when the path is invalid.</param>
public sealed record WslcStoragePathValidation(bool IsValid, string? Message)
{
    /// <summary>Shared success result to avoid allocating for the common valid case.</summary>
    public static WslcStoragePathValidation Valid { get; } = new(true, null);
}
