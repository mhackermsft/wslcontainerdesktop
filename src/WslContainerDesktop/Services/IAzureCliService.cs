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

/// <summary>Thin wrapper over the Azure CLI (`az`) for discovering and authenticating to ACRs.</summary>
public interface IAzureCliService
{
    /// <summary>True if the `az` CLI is installed and resolvable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Returns the signed-in Azure account (user name), or null if not signed in.</summary>
    Task<string?> GetSignedInUserAsync(CancellationToken ct = default);

    /// <summary>Runs an interactive `az login` (opens a browser). Returns success.</summary>
    Task<CommandResult> LoginAsync(CancellationToken ct = default);

    /// <summary>Lists the caller's Azure subscriptions.</summary>
    Task<IReadOnlyList<AzureSubscription>> ListSubscriptionsAsync(CancellationToken ct = default);

    /// <summary>Lists Azure Container Registries in a subscription.</summary>
    Task<IReadOnlyList<AzureRegistry>> ListRegistriesAsync(string subscriptionId, CancellationToken ct = default);

    /// <summary>
    /// Acquires a short-lived ACR refresh token using the signed-in identity
    /// (`az acr login --expose-token`). Returns (loginServer, token) or null on failure.
    /// </summary>
    Task<(string LoginServer, string Token)?> GetAcrTokenAsync(string acrName, string subscriptionId, CancellationToken ct = default);

    /// <summary>Lists the repositories in an ACR (`az acr repository list`).</summary>
    Task<IReadOnlyList<string>> ListAcrRepositoriesAsync(string acrName, string subscriptionId, CancellationToken ct = default);

    /// <summary>Lists the tags of a repository in an ACR (`az acr repository show-tags`), newest first.</summary>
    Task<IReadOnlyList<string>> ListAcrTagsAsync(string acrName, string repository, string subscriptionId, CancellationToken ct = default);
}
