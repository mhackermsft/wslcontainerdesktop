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

/// <summary>Known authentication state of a registry, determined by a cheap probe.</summary>
public enum RegistryLoginState
{
    /// <summary>Not yet checked.</summary>
    Unknown,

    /// <summary>A check is in progress.</summary>
    Checking,

    /// <summary>Authenticated and reachable.</summary>
    LoggedIn,

    /// <summary>Reachable but not authenticated.</summary>
    LoggedOut,

    /// <summary>Could not reach the registry (network/DNS).</summary>
    Unreachable,

    /// <summary>Reachable anonymously; no credentials supplied (used for Docker Hub by default).</summary>
    Anonymous,
}

/// <summary>
/// A container registry the user has registered. Credentials are NOT stored here — the
/// actual login is delegated to `wslc login`, which keeps secrets in its own credential
/// store. Only the display name, host, and (optional) username are persisted.
/// </summary>
public sealed class RegistryEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private RegistryLoginState _loginState = RegistryLoginState.Unknown;

    /// <summary>Friendly display name, e.g. "Docker Hub" or "Company ACR".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Registry host used to qualify image references and as the `wslc login` server,
    /// e.g. "docker.io", "ghcr.io", "myregistry.azurecr.io". Empty means the engine
    /// default (Docker Hub) and references are passed through unchanged.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Optional username, kept only to prefill the login dialog.</summary>
    public string? Username { get; set; }

    /// <summary>True if this registry was added from Azure and can be token-refreshed via `az`.</summary>
    public bool IsAzure { get; set; }

    /// <summary>Azure subscription id backing this registry (for token refresh). Azure registries only.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>ACR resource name (for `az acr login`). Azure registries only.</summary>
    public string? AzureAcrName { get; set; }

    /// <summary>The built-in Docker Hub default entry, which cannot be edited or removed.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Transient (not persisted) authentication state shown in the UI.</summary>
    [JsonIgnore]
    public RegistryLoginState LoginState
    {
        get => _loginState;
        set
        {
            if (SetProperty(ref _loginState, value))
            {
                OnPropertyChanged(nameof(LoginStateText));
                OnPropertyChanged(nameof(LoginStateGlyph));
                OnPropertyChanged(nameof(LoginStateColor));
                OnPropertyChanged(nameof(ShowLoginButton));
                OnPropertyChanged(nameof(ShowLogoutButton));
            }
        }
    }

    /// <summary>Human-readable status label for the list.</summary>
    [JsonIgnore]
    public string LoginStateText => _loginState switch
    {
        RegistryLoginState.LoggedIn => "Logged in",
        RegistryLoginState.LoggedOut => "Not logged in",
        RegistryLoginState.Checking => "Checking…",
        RegistryLoginState.Unreachable => "Unreachable",
        RegistryLoginState.Anonymous => "Anonymous",
        _ => "Unknown",
    };

    /// <summary>Segoe Fluent glyph for the status dot/icon.</summary>
    [JsonIgnore]
    public string LoginStateGlyph => _loginState switch
    {
        RegistryLoginState.LoggedIn => "\uE73E",       // checkmark
        RegistryLoginState.LoggedOut => "\uE711",      // cancel
        RegistryLoginState.Unreachable => "\uE783",    // error/info
        RegistryLoginState.Anonymous => "\uE77B",      // contact (anonymous)
        _ => "\uE9CE",                                   // unknown / help
    };

    /// <summary>Status color (hex ARGB) for the dot/icon.</summary>
    [JsonIgnore]
    public string LoginStateColor => _loginState switch
    {
        RegistryLoginState.LoggedIn => "#2DC85F",      // green
        RegistryLoginState.LoggedOut => "#9AA0A6",     // grey
        RegistryLoginState.Unreachable => "#E6A23C",   // amber
        RegistryLoginState.Checking => "#5B9BD5",      // blue
        RegistryLoginState.Anonymous => "#9AA0A6",     // grey
        _ => "#9AA0A6",
    };

    /// <summary>Whether to offer the "Log in" button — hidden once we know we're logged in.</summary>
    [JsonIgnore]
    public bool ShowLoginButton => _loginState != RegistryLoginState.LoggedIn;

    /// <summary>
    /// Whether to offer a "Log out" button. Only the built-in Docker Hub entry (which can't be
    /// removed) needs this; for user-added registries, removing the entry logs out.
    /// </summary>
    [JsonIgnore]
    public bool ShowLogoutButton => IsDefault && _loginState == RegistryLoginState.LoggedIn;

    /// <summary>Whether the row can be removed. The built-in Docker Hub entry is permanent.</summary>
    [JsonIgnore]
    public bool ShowRemoveButton => !IsDefault;

    /// <summary>
    /// The server passed to `wslc login`/`logout`. For the built-in Docker Hub entry (whose
    /// <see cref="Host"/> is intentionally empty so bare image names pass through) this is the
    /// canonical Docker Hub endpoint; otherwise it is the registry host.
    /// </summary>
    [JsonIgnore]
    public string LoginServer => IsDefault ? "docker.io" : Host;

    /// <summary>True when this registry has a host that qualifies image references.</summary>
    public bool HasHost => !string.IsNullOrWhiteSpace(Host);

    /// <summary>
    /// Qualifies a bare image reference with this registry's host when needed. References
    /// that already specify an explicit registry (first path segment contains '.'/':' or is
    /// "localhost") are returned unchanged so the user's explicit intent always wins.
    /// </summary>
    public string Qualify(string reference)
    {
        var image = reference.Trim();
        if (!HasHost || string.IsNullOrEmpty(image))
        {
            return image;
        }

        if (ReferenceHasRegistry(image))
        {
            return image;
        }

        var host = Host.Trim().TrimEnd('/');
        return $"{host}/{image}";
    }

    /// <summary>Whether an image reference already begins with an explicit registry host.</summary>
    public static bool ReferenceHasRegistry(string reference)
    {
        var image = reference.Trim();
        var slash = image.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var first = image[..slash];
        return first.Contains('.') || first.Contains(':') ||
            string.Equals(first, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates the immutable built-in Docker Hub entry.</summary>
    public static RegistryEntry DockerHub() => new()
    {
        Name = "Docker Hub",
        Host = string.Empty,
        IsDefault = true,
        LoginState = RegistryLoginState.Anonymous,
    };
}

