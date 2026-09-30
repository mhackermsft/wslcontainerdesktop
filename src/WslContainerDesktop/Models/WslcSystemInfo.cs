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

using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>Model object that stores wslc system info information used by services, view models, or dialogs.</summary>
public sealed class WslcSystemInfo
{
    /// <summary>Gets or sets the client.</summary>
    public WslcClientInfo Client { get; init; } = new();

    /// <summary>Gets or sets the server.</summary>
    public WslcServerInfo Server { get; init; } = new();

    /// <summary>Performs the sanitized helper used by this model or dialog.</summary>
    /// <returns>The requested value for the caller.</returns>
    public WslcSystemInfo Sanitized()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new WslcSystemInfo
        {
            Client = Client with
            {
                SettingsFile = SanitizePath(Client.SettingsFile, profile),
            },
            Server = Server,
        };
    }

    private static string SanitizePath(string value, string profile)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(profile))
        {
            return value;
        }

        return value.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + value[profile.Length..]
            : value;
    }
}

/// <summary>Immutable or init-only data model that carries wslc client info information between services and view models.</summary>
public sealed record WslcClientInfo
{
    /// <summary>Gets or sets the version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets or sets the settings file.</summary>
    public string SettingsFile { get; init; } = string.Empty;

    /// <summary>Gets or sets the kernel version.</summary>
    public string KernelVersion { get; init; } = string.Empty;

    /// <summary>Gets or sets the windows version.</summary>
    public string WindowsVersion { get; init; } = string.Empty;

    /// <summary>Gets or sets the direct3 d version.</summary>
    public string Direct3DVersion { get; init; } = string.Empty;

    /// <summary>Gets or sets the dx core version.</summary>
    public string DxCoreVersion { get; init; } = string.Empty;
}

/// <summary>Immutable or init-only data model that carries wslc server info information between services and view models.</summary>
public sealed record WslcServerInfo
{
    /// <summary>Gets or sets the session manager version.</summary>
    public string SessionManagerVersion { get; init; } = string.Empty;

    /// <summary>Gets or sets the sessions.</summary>
    public IReadOnlyList<WslcSessionInfo> Sessions { get; init; } = Array.Empty<WslcSessionInfo>();
}

/// <summary>Immutable or init-only data model that carries wslc session info information between services and view models.</summary>
public sealed record WslcSessionInfo
{
    /// <summary>Gets or sets the id.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ID { get; init; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the creator pid.</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int CreatorPid { get; init; }
}

/// <summary>Model object that stores wslc settings file information used by services, view models, or dialogs.</summary>
public sealed class WslcSettingsFile
{
    /// <summary>Gets or sets the settings file path.</summary>
    public string SettingsFilePath { get; init; } = string.Empty;

    /// <summary>Gets or sets the storage path.</summary>
    public string? StoragePath { get; init; }

    /// <summary>Gets or sets the default storage path.</summary>
    public string DefaultStoragePath { get; init; } = string.Empty;

    /// <summary>Gets the effective storage path.</summary>
    public string EffectiveStoragePath =>
        string.IsNullOrWhiteSpace(StoragePath) || string.Equals(StoragePath, "default", StringComparison.OrdinalIgnoreCase)
            ? DefaultStoragePath
            : StoragePath!;

    /// <summary>Gets a value indicating whether the app uses s default storage.</summary>
    public bool UsesDefaultStorage =>
        string.IsNullOrWhiteSpace(StoragePath) || string.Equals(StoragePath, "default", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets or sets the cpu count.</summary>
    public string? CpuCount { get; init; }

    /// <summary>Gets or sets the memory size.</summary>
    public string? MemorySize { get; init; }

    /// <summary>Gets or sets the max storage size.</summary>
    public string? MaxStorageSize { get; init; }

    /// <summary>Gets or sets the default binding address.</summary>
    public string? DefaultBindingAddress { get; init; }

    /// <summary>Gets or sets the credential store.</summary>
    public string? CredentialStore { get; init; }

    /// <summary>Gets or sets a value indicating whether the settings file exists flag is set.</summary>
    public bool SettingsFileExists { get; init; }

    /// <summary>Gets or sets a value indicating whether the direct edit available flag is set.</summary>
    public bool DirectEditAvailable { get; init; }
}
