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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Checks whether a newer image exists upstream for a given tag by comparing the local manifest
/// digest against the registry's current digest for that tag, using the registry v2 HTTP API with
/// anonymous (Bearer) token auth. Works for Docker Hub and any registry that allows anonymous
/// pulls; registries that require credentials we don't have return <see cref="ImageUpdateState.CheckFailed"/>.
/// </summary>
public interface IImageUpdateService
{
    Task<ImageUpdateState> CheckAsync(
        string reference,
        IReadOnlyList<string> localRepoDigests,
        CancellationToken ct = default);
}

public sealed class ImageUpdateService : IImageUpdateService
{
    private static readonly string[] ManifestAcceptTypes =
    {
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
    };

    private readonly HttpClient _http;
    private readonly ILogger<ImageUpdateService> _logger;

    public ImageUpdateService(HttpClient http, ILogger<ImageUpdateService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ImageUpdateState> CheckAsync(
        string reference,
        IReadOnlyList<string> localRepoDigests,
        CancellationToken ct = default)
    {
        // No local digest means the image was built locally or never pulled from a registry, so
        // there is nothing meaningful to compare against.
        if (localRepoDigests is null || localRepoDigests.Count == 0)
        {
            return ImageUpdateState.Unknown;
        }

        var parsed = ParseReference(reference);
        if (parsed is null)
        {
            return ImageUpdateState.Unknown;
        }

        var (host, repository, tag) = parsed.Value;

        try
        {
            var upstream = await ResolveUpstreamDigestAsync(host, repository, tag, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(upstream))
            {
                return ImageUpdateState.CheckFailed;
            }

            // Compare only the "sha256:…" portion; the local RepoDigest is "repo@sha256:…" and its
            // repository already corresponds to the image we inspected.
            foreach (var local in localRepoDigests)
            {
                var at = local.IndexOf('@');
                var localDigest = at >= 0 ? local[(at + 1)..] : local;
                if (string.Equals(localDigest, upstream, StringComparison.OrdinalIgnoreCase))
                {
                    return ImageUpdateState.UpToDate;
                }
            }

            return ImageUpdateState.UpdateAvailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed for {Reference}.", reference);
            return ImageUpdateState.CheckFailed;
        }
    }

    /// <summary>Queries the registry for the current digest of a tag, performing the anonymous
    /// Bearer-token dance if the registry challenges with 401.</summary>
    private async Task<string?> ResolveUpstreamDigestAsync(
        string host,
        string repository,
        string tag,
        CancellationToken ct)
    {
        var url = $"https://{host}/v2/{repository}/manifests/{Uri.EscapeDataString(tag)}";

        var response = await SendManifestRequestAsync(url, token: null, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var token = await TryGetBearerTokenAsync(response, repository, ct).ConfigureAwait(false);
            response.Dispose();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            response = await SendManifestRequestAsync(url, token, ct).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Headers.TryGetValues("Docker-Content-Digest", out var values))
            {
                var digest = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(digest))
                {
                    return digest.Trim();
                }
            }

            return null;
        }
    }

    private Task<HttpResponseMessage> SendManifestRequestAsync(string url, string? token, CancellationToken ct)
    {
        // HEAD is enough to read the Docker-Content-Digest header and avoids downloading the body.
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var accept in ManifestAcceptTypes)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Parses the WWW-Authenticate Bearer challenge and fetches an anonymous pull token.</summary>
    private async Task<string?> TryGetBearerTokenAsync(
        HttpResponseMessage challenge,
        string repository,
        CancellationToken ct)
    {
        var header = challenge.Headers.WwwAuthenticate.FirstOrDefault(h =>
            string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        if (header is null || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return null;
        }

        var parameters = ParseChallengeParameters(header.Parameter);
        if (!parameters.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
        {
            return null;
        }

        var service = parameters.GetValueOrDefault("service");
        var scope = parameters.GetValueOrDefault("scope");
        if (string.IsNullOrWhiteSpace(scope))
        {
            scope = $"repository:{repository}:pull";
        }

        var tokenUrl = realm + "?scope=" + Uri.EscapeDataString(scope);
        if (!string.IsNullOrWhiteSpace(service))
        {
            tokenUrl += "&service=" + Uri.EscapeDataString(service);
        }

        using var tokenResponse = await _http.GetAsync(tokenUrl, ct).ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await tokenResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
        {
            return t.GetString();
        }

        if (root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
        {
            return at.GetString();
        }

        return null;
    }

    private static Dictionary<string, string> ParseChallengeParameters(string parameter)
    {
        // e.g. realm="https://auth.docker.io/token",service="registry.docker.io",scope="repository:library/nginx:pull"
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

    /// <summary>Normalizes a Docker image reference into (registry host, repository, tag), or null
    /// if it is digest-pinned or cannot be parsed.</summary>
    private static (string Host, string Repository, string Tag)? ParseReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var remainder = reference.Trim();

        // A digest-pinned reference can't be "updated" — skip it.
        if (remainder.Contains('@'))
        {
            return null;
        }

        string host;
        string path;

        var firstSlash = remainder.IndexOf('/');
        var firstSegment = firstSlash >= 0 ? remainder[..firstSlash] : string.Empty;
        var looksLikeHost = firstSegment.Contains('.') || firstSegment.Contains(':') ||
            string.Equals(firstSegment, "localhost", StringComparison.OrdinalIgnoreCase);

        if (firstSlash >= 0 && looksLikeHost)
        {
            host = firstSegment;
            path = remainder[(firstSlash + 1)..];
        }
        else
        {
            // Docker Hub. Official images live under the "library/" namespace.
            host = "registry-1.docker.io";
            path = remainder.Contains('/') ? remainder : "library/" + remainder;
        }

        // Split the tag (a ':' in the final path segment).
        var tag = "latest";
        var lastSlash = path.LastIndexOf('/');
        var lastColon = path.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            tag = path[(lastColon + 1)..];
            path = path[..lastColon];
        }

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        return (host, path, tag);
    }
}
