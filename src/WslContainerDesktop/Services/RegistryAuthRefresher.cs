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
public sealed class RegistryAuthRefresher(IWslcService wslc, IAzureCliService azure, ISettingsService settings, ILogger<RegistryAuthRefresher> logger)
{
    /// <summary>
    /// If the given image reference targets an Azure-backed registry, silently re-mints its
    /// token from the cached az session and re-logs the engine in. No-ops for non-Azure
    /// references or when the az session has expired. Best-effort — never throws.
    /// </summary>
    public async Task EnsureFreshForReferenceAsync(string reference, CancellationToken ct = default,
        IWslcService? sessionClient = null, Func<CancellationToken, Task>? verifyTarget = null)
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
        var registry = settings.Registries.FirstOrDefault(r =>
            r.IsAzure && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
        if (registry is not null)
        {
            await RefreshAsync(registry, ct, sessionClient, verifyTarget).ConfigureAwait(false);
        }
    }

    /// <summary>Silently re-authenticates one Azure registry. Returns true on success.</summary>
    public async Task<bool> RefreshAsync(RegistryEntry registry, CancellationToken ct = default,
        IWslcService? sessionClient = null, Func<CancellationToken, Task>? verifyTarget = null)
    {
        if (!registry.IsAzure || string.IsNullOrWhiteSpace(registry.AzureAcrName) ||
            string.IsNullOrWhiteSpace(registry.SubscriptionId))
        {
            return false;
        }

        (string LoginServer, string Token)? token;
        try
        {
            if (string.IsNullOrWhiteSpace(await azure.GetSignedInUserAsync(ct).ConfigureAwait(false)))
            {
                return false; // az session gone; needs interactive sign-in.
            }

            token = await azure.GetAcrTokenAsync(registry.AzureAcrName, registry.SubscriptionId, ct)
                .ConfigureAwait(false);
            if (token is null)
            {
                return false;
            }

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh Azure registry login for {Host}.", registry.Host);
            return false;
        }

        if (verifyTarget is not null)
            await verifyTarget(ct).ConfigureAwait(false);
        try
        {
            var login = await (sessionClient ?? wslc).LoginRegistryAsync(registry.Host, registry.Username ?? string.Empty, token.Value.Token, ct)
                .ConfigureAwait(false);
            return login.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh Azure registry login for {Host}.", registry.Host);
            return false;
        }
    }
}
