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

using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Keeps Azure-backed registry logins fresh. Shared by the view models (refresh right before
/// an app-initiated pull/run) and the background monitor (periodic refresh).
/// </summary>
public sealed class RegistryAuthRefresher
{
    private readonly IWslcService _wslc;
    private readonly IAzureCliService _azure;
    private readonly ISettingsService _settings;
    private readonly ILogger<RegistryAuthRefresher> _logger;

    public RegistryAuthRefresher(IWslcService wslc, IAzureCliService azure, ISettingsService settings, ILogger<RegistryAuthRefresher> logger)
    {
        _wslc = wslc;
        _azure = azure;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// If the given image reference targets an Azure-backed registry, silently re-mints its
    /// token from the cached az session and re-logs the engine in. No-ops for non-Azure
    /// references or when the az session has expired. Best-effort — never throws.
    /// </summary>
    public async Task EnsureFreshForReferenceAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        // The registry host is the first path segment when it looks like a host.
        var slash = reference.IndexOf('/');
        if (slash <= 0)
        {
            return;
        }

        var host = reference[..slash];
        var registry = _settings.Registries.FirstOrDefault(r =>
            r.IsAzure && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
        if (registry is not null)
        {
            await RefreshAsync(registry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Silently re-authenticates one Azure registry. Returns true on success.</summary>
    public async Task<bool> RefreshAsync(RegistryEntry registry, CancellationToken ct = default)
    {
        if (!registry.IsAzure || string.IsNullOrWhiteSpace(registry.AzureAcrName) ||
            string.IsNullOrWhiteSpace(registry.SubscriptionId))
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(await _azure.GetSignedInUserAsync(ct).ConfigureAwait(false)))
            {
                return false; // az session gone; needs interactive sign-in.
            }

            var token = await _azure.GetAcrTokenAsync(registry.AzureAcrName, registry.SubscriptionId, ct)
                .ConfigureAwait(false);
            if (token is null)
            {
                return false;
            }

            var login = await _wslc.LoginRegistryAsync(registry.Host, registry.Username ?? string.Empty, token.Value.Token, ct)
                .ConfigureAwait(false);
            return login.Success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Azure registry login for {Host}.", registry.Host);
            return false;
        }
    }
}
