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

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Outcome of a catalog browse call.</summary>
public enum CatalogStatus
{
    /// <summary>The list was retrieved successfully (it may still be empty).</summary>
    Ok,

    /// <summary>The registry does not support browsing (e.g. Docker Hub's global catalog).</summary>
    NotSupported,

    /// <summary>The registry requires credentials that have not been stored — the user must log in first.</summary>
    NotLoggedIn,

    /// <summary>A required tool (the Azure CLI) is not installed.</summary>
    Unavailable,

    /// <summary>The browse failed (network error, unexpected response, etc.).</summary>
    Failed,
}

/// <summary>Result of a catalog browse call: a status plus the list on success or a message on failure.</summary>
public sealed class CatalogResult
{
    public CatalogStatus Status { get; init; }

    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();

    public string? Message { get; init; }

    public bool IsOk => Status == CatalogStatus.Ok;

    public static CatalogResult Ok(IReadOnlyList<string> items) => new() { Status = CatalogStatus.Ok, Items = items };

    public static CatalogResult Fail(CatalogStatus status, string message) => new() { Status = status, Message = message };
}

/// <summary>Browses a registry's available repositories and tags so a user can pick what to pull.</summary>
public interface IRegistryCatalogService
{
    /// <summary>True if this registry can be browsed (an ACR, or a generic registry with a host).</summary>
    bool CanBrowse(RegistryEntry registry);

    /// <summary>Lists the repositories available in the registry.</summary>
    Task<CatalogResult> ListRepositoriesAsync(RegistryEntry registry, CancellationToken ct = default);

    /// <summary>Lists the tags of a repository in the registry, newest first where known.</summary>
    Task<CatalogResult> ListTagsAsync(RegistryEntry registry, string repository, CancellationToken ct = default);
}

/// <summary>
/// Unifies two browse strategies behind one interface:
/// <list type="bullet">
/// <item>Azure Container Registries use the signed-in <c>az</c> session
/// (<c>az acr repository list/show-tags</c>).</item>
/// <item>Generic private registries use the Docker Registry v2 HTTP API
/// (<c>/v2/_catalog</c> and <c>/v2/&lt;repo&gt;/tags/list</c>), authenticating with the credentials
/// the user stored via <c>wslc login</c> (Bearer-token challenge or direct Basic auth).</item>
/// </list>
/// Docker Hub's global catalog is intentionally not browsable, matching the registry's own API.
/// </summary>
public sealed class RegistryCatalogService : IRegistryCatalogService
{
    private const int CatalogPageSize = 200;

    private readonly HttpClient _http;
    private readonly IAzureCliService _azure;
    private readonly IRegistryCredentialStore _credentials;
    private readonly ILogger<RegistryCatalogService> _logger;

    public RegistryCatalogService(
        HttpClient http,
        IAzureCliService azure,
        IRegistryCredentialStore credentials,
        ILogger<RegistryCatalogService> logger)
    {
        _http = http;
        _azure = azure;
        _credentials = credentials;
        _logger = logger;
    }

    public bool CanBrowse(RegistryEntry registry)
    {
        if (registry is null)
        {
            return false;
        }

        if (IsAcr(registry))
        {
            return true;
        }

        // Generic registries need an explicit host and must not be Docker Hub (no global catalog).
        return registry.HasHost && !IsDockerHubHost(registry.Host);
    }

    public async Task<CatalogResult> ListRepositoriesAsync(RegistryEntry registry, CancellationToken ct = default)
    {
        if (!CanBrowse(registry))
        {
            return CatalogResult.Fail(CatalogStatus.NotSupported,
                "Browsing isn't supported for this registry. Enter the image reference manually.");
        }

        if (IsAcr(registry))
        {
            if (!await _azure.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                return CatalogResult.Fail(CatalogStatus.Unavailable,
                    "The Azure CLI (az) is required to browse this registry but isn't installed.");
            }

            var repos = await _azure.ListAcrRepositoriesAsync(registry.AzureAcrName!, registry.SubscriptionId!, ct)
                .ConfigureAwait(false);
            return CatalogResult.Ok(repos);
        }

        return await ListV2Async(registry, $"https://{registry.Host}/v2/_catalog?n={CatalogPageSize}",
            "registry:catalog:*", "repositories", ct).ConfigureAwait(false);
    }

    public async Task<CatalogResult> ListTagsAsync(RegistryEntry registry, string repository, CancellationToken ct = default)
    {
        if (!CanBrowse(registry) || string.IsNullOrWhiteSpace(repository))
        {
            return CatalogResult.Fail(CatalogStatus.NotSupported, "Browsing isn't supported for this registry.");
        }

        if (IsAcr(registry))
        {
            var tags = await _azure.ListAcrTagsAsync(registry.AzureAcrName!, repository, registry.SubscriptionId!, ct)
                .ConfigureAwait(false);
            return CatalogResult.Ok(tags);
        }

        return await ListV2Async(registry, $"https://{registry.Host}/v2/{repository}/tags/list",
            $"repository:{repository}:pull", "tags", ct).ConfigureAwait(false);
    }

    private static bool IsAcr(RegistryEntry registry) =>
        registry.IsAzure &&
        !string.IsNullOrWhiteSpace(registry.AzureAcrName) &&
        !string.IsNullOrWhiteSpace(registry.SubscriptionId);

    private static bool IsDockerHubHost(string host) =>
        host.Contains("docker.io", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("index.docker.io", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Performs a Docker Registry v2 GET that returns a JSON object with a string-array property,
    /// handling the 401 auth challenge (Bearer token exchange or direct Basic auth) using the
    /// stored credential for the registry.
    /// </summary>
    private async Task<CatalogResult> ListV2Async(
        RegistryEntry registry,
        string url,
        string scope,
        string jsonArrayProperty,
        CancellationToken ct)
    {
        try
        {
            _credentials.TryGetCredential(registry.LoginServer, out var username, out var password);
            var hasCredential = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);

            var response = await SendV2Async(url, authorization: null, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
                var authorization = await BuildAuthorizationAsync(challenge, scope, username, password, ct)
                    .ConfigureAwait(false);
                response.Dispose();

                if (authorization is null)
                {
                    return CatalogResult.Fail(CatalogStatus.NotLoggedIn,
                        hasCredential
                            ? "The stored credentials were rejected by the registry. Log in again from the Registries page."
                            : "You need to log in to this registry first. Open the Registries page and log in.");
                }

                response = await SendV2Async(url, authorization, ct).ConfigureAwait(false);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return CatalogResult.Fail(CatalogStatus.NotLoggedIn,
                        "The registry rejected the request. Log in to this registry from the Registries page.");
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                {
                    return CatalogResult.Fail(CatalogStatus.NotSupported,
                        "This registry doesn't support browsing its catalog.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return CatalogResult.Fail(CatalogStatus.Failed,
                        $"The registry returned an error ({(int)response.StatusCode}).");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var items = new List<string>();
                if (doc.RootElement.TryGetProperty(jsonArrayProperty, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                items.Add(s);
                            }
                        }
                    }
                }

                return CatalogResult.Ok(items);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry v2 browse failed for {Host}.", registry.Host);
            return CatalogResult.Fail(CatalogStatus.Failed,
                "Could not reach the registry. Check your network connection and that the host is correct.");
        }
    }

    private Task<HttpResponseMessage> SendV2Async(string url, AuthenticationHeaderValue? authorization, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authorization is not null)
        {
            request.Headers.Authorization = authorization;
        }

        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// Turns a 401 <c>WWW-Authenticate</c> challenge into an Authorization header. For a Bearer
    /// challenge it exchanges the stored credential (Basic) at the token realm for an access token;
    /// for a Basic challenge it returns the Basic header directly. Returns null when no usable
    /// credential is available or the exchange fails.
    /// </summary>
    private async Task<AuthenticationHeaderValue?> BuildAuthorizationAsync(
        AuthenticationHeaderValue? challenge,
        string scope,
        string? username,
        string? password,
        CancellationToken ct)
    {
        var basic = BuildBasic(username, password);

        if (challenge is null)
        {
            return basic;
        }

        if (string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            return basic;
        }

        if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(challenge.Parameter))
        {
            return basic;
        }

        var parameters = ParseChallengeParameters(challenge.Parameter);
        if (!parameters.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
        {
            return basic;
        }

        var service = parameters.GetValueOrDefault("service");
        var effectiveScope = parameters.GetValueOrDefault("scope");
        if (string.IsNullOrWhiteSpace(effectiveScope))
        {
            effectiveScope = scope;
        }

        var tokenUrl = realm + "?scope=" + Uri.EscapeDataString(effectiveScope);
        if (!string.IsNullOrWhiteSpace(service))
        {
            tokenUrl += "&service=" + Uri.EscapeDataString(service);
        }

        var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        if (basic is not null)
        {
            tokenRequest.Headers.Authorization = basic;
        }

        using var tokenResponse = await _http.SendAsync(tokenRequest, ct).ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await tokenResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var token = (root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
            ? t.GetString()
            : (root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
                ? at.GetString()
                : null;

        return string.IsNullOrWhiteSpace(token) ? null : new AuthenticationHeaderValue("Bearer", token);
    }

    private static AuthenticationHeaderValue? BuildBasic(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return null;
        }

        var raw = $"{username}:{password}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        return new AuthenticationHeaderValue("Basic", encoded);
    }

    private static Dictionary<string, string> ParseChallengeParameters(string parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitTopLevel(parameter))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim().Trim('"');
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var parts = new List<string>();
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }
}
