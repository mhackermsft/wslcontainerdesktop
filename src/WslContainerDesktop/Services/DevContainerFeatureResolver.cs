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


using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Resolves Dev Container Features and stages a derived Dockerfile build context.</summary>
public sealed partial class DevContainerFeatureResolver(
    HttpClient http,
    ISettingsService settings,
    ILogger<DevContainerFeatureResolver> logger) : IDevContainerFeatureResolver
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public async Task<DevContainerDerivedImage?> PrepareDerivedImageAsync(
        DevContainerConfig config,
        string baseImage,
        CancellationToken ct = default)
    {
        if (config.Features.Count == 0)
        {
            return null;
        }

        var warnings = new List<string>();
        var root = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "devcontainer-features", config.Id);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        var resolved = new List<ResolvedDevContainerFeature>();
        foreach (var feature in OrderFeatures(config))
        {
            var item = await ResolveFeatureAsync(feature, root, warnings, ct).ConfigureAwait(false);
            if (item is not null)
            {
                resolved.Add(item);
            }
        }

        if (resolved.Count == 0)
        {
            return null;
        }

        var dockerfile = Path.Combine(root, "Dockerfile");
        await File.WriteAllTextAsync(dockerfile, BuildDockerfile(baseImage, config, resolved, settings.DevContainerNpmRegistry), ct).ConfigureAwait(false);
        return new DevContainerDerivedImage
        {
            ContextPath = root,
            DockerfilePath = dockerfile,
            ImageTag = DevContainerConfig.DevContainerImageTag(config.Id),
            ContainerEnv = MergeEnv(resolved),
            RemoteUser = resolved.LastOrDefault(f => !string.IsNullOrWhiteSpace(f.RemoteUser))?.RemoteUser,
            Warnings = warnings,
        };
    }

    private static IReadOnlyList<DevContainerFeature> OrderFeatures(DevContainerConfig config)
    {
        if (config.OverrideFeatureInstallOrder.Count == 0)
        {
            return config.Features;
        }

        var ordered = new List<DevContainerFeature>();
        foreach (var id in config.OverrideFeatureInstallOrder)
        {
            var feature = config.Features.FirstOrDefault(f => FeatureMatches(f.Id, id));
            if (feature is not null && !ordered.Contains(feature))
            {
                ordered.Add(feature);
            }
        }

        ordered.AddRange(config.Features.Where(f => !ordered.Contains(f)));
        return ordered;
    }

    private async Task<ResolvedDevContainerFeature?> ResolveFeatureAsync(
        DevContainerFeature feature,
        string root,
        List<string> warnings,
        CancellationToken ct)
    {
        var target = Path.Combine(root, Sanitize(feature.Id));
        Directory.CreateDirectory(target);

        try
        {
            if (TryParseDevcontainersFeature(feature.Id, out var wellKnown, out var version))
            {
                return await ResolveWellKnownFeatureAsync(feature, wellKnown, version, target, warnings, ct).ConfigureAwait(false);
            }

            var resolved = await ResolveOciFeatureAsync(feature, target, warnings, ct).ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resolving Dev Container Feature {Feature} failed.", feature.Id);
        }

        warnings.Add($"Feature '{feature.Id}' could not be resolved from its OCI artifact and was skipped.");
        return null;
    }

    private async Task<ResolvedDevContainerFeature?> ResolveWellKnownFeatureAsync(
        DevContainerFeature feature,
        string name,
        string? version,
        string target,
        List<string> warnings,
        CancellationToken ct)
    {
        var branch = string.IsNullOrWhiteSpace(version) || version == "1" ? "main" : version;
        var baseUrl = $"https://raw.githubusercontent.com/devcontainers/features/{branch}/src/{name}";
        var install = await TryGetStringAsync($"{baseUrl}/install.sh", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(install) && branch != "main")
        {
            baseUrl = $"https://raw.githubusercontent.com/devcontainers/features/main/src/{name}";
            install = await TryGetStringAsync($"{baseUrl}/install.sh", ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(install))
        {
            return await ResolveOciFeatureAsync(feature, target, warnings, ct).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(target, "install.sh"), install, ct).ConfigureAwait(false);
        var metadata = await TryGetStringAsync($"{baseUrl}/devcontainer-feature.json", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            await File.WriteAllTextAsync(Path.Combine(target, "devcontainer-feature.json"), metadata, ct).ConfigureAwait(false);
        }

        return ReadResolvedMetadata(feature, target);
    }

    private async Task<ResolvedDevContainerFeature?> ResolveOciFeatureAsync(
        DevContainerFeature feature,
        string target,
        List<string> warnings,
        CancellationToken ct)
    {
        var oci = OciReference.Parse(feature.Id);
        if (oci is null)
        {
            return null;
        }

        var manifestJson = await GetOciAsync(oci, $"manifests/{oci.Tag}", OciManifestAccept, ct).ConfigureAwait(false);
        if (manifestJson is null)
        {
            return null;
        }

        using var manifestDoc = JsonDocument.Parse(manifestJson, JsonOptions);
        var manifest = manifestDoc.RootElement;
        if (manifest.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
        {
            var first = manifests.EnumerateArray().FirstOrDefault();
            var digest = GetString(first, "digest");
            if (!string.IsNullOrWhiteSpace(digest))
            {
                manifestJson = await GetOciAsync(oci, $"manifests/{digest}", OciManifestAccept, ct).ConfigureAwait(false);
                if (manifestJson is null)
                {
                    return null;
                }

                using var doc2 = JsonDocument.Parse(manifestJson, JsonOptions);
                manifest = doc2.RootElement.Clone();
            }
        }

        var layer = SelectLayer(manifest);
        if (layer is null)
        {
            return null;
        }

        var bytes = await GetOciBytesAsync(oci, $"blobs/{layer}", ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        ExtractTarGz(bytes, target);
        if (!File.Exists(Path.Combine(target, "install.sh")))
        {
            var install = Directory.GetFiles(target, "install.sh", SearchOption.AllDirectories).FirstOrDefault();
            if (install is not null)
            {
                File.Copy(install, Path.Combine(target, "install.sh"), overwrite: true);
            }
        }

        if (!File.Exists(Path.Combine(target, "install.sh")))
        {
            warnings.Add($"Feature '{feature.Id}' did not contain install.sh and was skipped.");
            return null;
        }

        return ReadResolvedMetadata(feature, target);
    }

    private async Task<string?> GetOciAsync(OciReference oci, string path, string accept, CancellationToken ct)
    {
        var bytes = await GetOciBytesAsync(oci, path, ct, accept).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]?> GetOciBytesAsync(OciReference oci, string path, CancellationToken ct, string? accept = null)
    {
        var requestUri = $"https://{oci.Registry}/v2/{oci.Repository}/{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(accept))
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && response.Headers.WwwAuthenticate.Count > 0)
        {
            var token = await GetBearerTokenAsync(response.Headers.WwwAuthenticate, oci, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                using var authed = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (!string.IsNullOrWhiteSpace(accept))
                {
                    authed.Headers.Accept.ParseAdd(accept);
                }
                authed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var authedResponse = await http.SendAsync(authed, ct).ConfigureAwait(false);
                return authedResponse.IsSuccessStatusCode ? await authedResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
            }
        }

        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
    }

    private async Task<string?> GetBearerTokenAsync(
        HttpHeaderValueCollection<AuthenticationHeaderValue> challenges,
        OciReference oci,
        CancellationToken ct)
    {
        var challenge = challenges.FirstOrDefault(h => string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        if (challenge?.Parameter is null)
        {
            return null;
        }

        var parts = ParseChallenge(challenge.Parameter);
        if (!parts.TryGetValue("realm", out var realm))
        {
            return null;
        }

        var service = parts.TryGetValue("service", out var svc) ? svc : oci.Registry;
        var scope = parts.TryGetValue("scope", out var scp) ? scp : $"repository:{oci.Repository}:pull";
        var uri = $"{realm}?service={Uri.EscapeDataString(service)}&scope={Uri.EscapeDataString(scope)}";
        var json = await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json, JsonOptions);
        return GetString(doc.RootElement, "token") ?? GetString(doc.RootElement, "access_token");
    }

    private static ResolvedDevContainerFeature ReadResolvedMetadata(DevContainerFeature feature, string target)
    {
        var result = new ResolvedDevContainerFeature { Id = feature.Id, DirectoryPath = target };
        var metadataPath = Path.Combine(target, "devcontainer-feature.json");
        if (!File.Exists(metadataPath))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath), JsonOptions);
            var root = doc.RootElement;
            if (root.TryGetProperty("containerEnv", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in env.EnumerateObject())
                {
                    result.ContainerEnv[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : prop.Value.GetRawText();
                }
            }

            result.RemoteUser = GetString(root, "remoteUser");
            if (root.TryGetProperty("installsAfter", out var after) && after.ValueKind == JsonValueKind.Array)
            {
                result.InstallsAfter.AddRange(after.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s))!);
            }
        }
        catch
        {
            // Bad metadata should not block a feature whose install script is present.
        }

        return result;
    }

    private static string BuildDockerfile(string baseImage, DevContainerConfig config, IReadOnlyList<ResolvedDevContainerFeature> features, string? npmRegistry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FROM {baseImage}");
        sb.AppendLine("USER root");

        // When a corporate npm registry/proxy is configured, point every npm-based feature
        // install at it so builds work behind networks that block the public registry.
        // npm, pnpm and yarn all honour npm_config_registry.
        if (!string.IsNullOrWhiteSpace(npmRegistry))
        {
            var registry = npmRegistry.Trim();
            sb.AppendLine($"ENV NPM_CONFIG_REGISTRY={DockerQuote(registry)}");
            sb.AppendLine($"ENV npm_config_registry={DockerQuote(registry)}");
        }

        for (var i = 0; i < features.Count; i++)
        {
            var feature = config.Features.First(f => string.Equals(f.Id, features[i].Id, StringComparison.OrdinalIgnoreCase));
            var dirName = Sanitize(features[i].Id);
            sb.AppendLine($"COPY {dirName}/ /tmp/devcontainer-features/{i}/");
            foreach (var env in FeatureOptionEnv(feature))
            {
                sb.AppendLine($"ENV {env.Key}={DockerQuote(env.Value)}");
            }
            sb.AppendLine($"RUN chmod +x /tmp/devcontainer-features/{i}/install.sh && /tmp/devcontainer-features/{i}/install.sh");
        }

        if (!string.IsNullOrWhiteSpace(config.ContainerUser ?? config.RemoteUser))
        {
            sb.AppendLine($"USER {config.ContainerUser ?? config.RemoteUser}");
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> FeatureOptionEnv(DevContainerFeature feature)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = Regex.Replace(FeatureShortName(feature.Id), "[^a-zA-Z0-9]", "_").ToUpperInvariant();
        foreach (var option in feature.Options)
        {
            var key = Regex.Replace(option.Key, "[^a-zA-Z0-9]", "_").ToUpperInvariant();
            result[key] = option.Value;
            result[$"{prefix}_{key}"] = option.Value;
        }

        return result;
    }

    private async Task<string?> TryGetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> MergeEnv(IEnumerable<ResolvedDevContainerFeature> features)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            foreach (var kv in feature.ContainerEnv)
            {
                env[kv.Key] = kv.Value;
            }
        }
        return env;
    }

    private static string? SelectLayer(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var layer in layers.EnumerateArray())
        {
            var media = GetString(layer, "mediaType") ?? string.Empty;
            if (media.Contains("tar", StringComparison.OrdinalIgnoreCase) || media.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                return GetString(layer, "digest");
            }
        }

        return layers.EnumerateArray().Select(l => GetString(l, "digest")).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
    }

    private static void ExtractTarGz(byte[] bytes, string target)
    {
        using var mem = new MemoryStream(bytes);
        using var gzip = new GZipStream(mem, CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
    }

    private static bool TryParseDevcontainersFeature(string id, out string name, out string? version)
    {
        name = string.Empty;
        version = null;
        var text = id;
        if (text.StartsWith("ghcr.io/devcontainers/features/", StringComparison.OrdinalIgnoreCase))
        {
            text = text["ghcr.io/devcontainers/features/".Length..];
        }
        else if (text.StartsWith("ghcr.io/devcontainers-contrib/features/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        else if (text.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            version = text[(colon + 1)..];
            text = text[..colon];
        }

        name = text;
        return !string.IsNullOrWhiteSpace(name);
    }

    private static bool FeatureMatches(string featureId, string requested) =>
        string.Equals(featureId, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(FeatureShortName(featureId), FeatureShortName(requested), StringComparison.OrdinalIgnoreCase);

    private static string FeatureShortName(string id)
    {
        var noTag = id.Split(':')[0];
        var slash = noTag.LastIndexOf('/');
        return slash < 0 ? noTag : noTag[(slash + 1)..];
    }

    private static Dictionary<string, string> ParseChallenge(string parameter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ChallengePartRegex().Matches(parameter))
        {
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return result;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(c => invalid.Contains(c) || c is '/' or ':' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "feature" : sanitized;
    }

    private static string DockerQuote(string value) => JsonSerializer.Serialize(value);

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private const string OciManifestAccept = "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json";

    [GeneratedRegex("(\\w+)=\\\"([^\\\"]*)\\\"")]
    private static partial Regex ChallengePartRegex();

    private sealed record OciReference(string Registry, string Repository, string Tag)
    {
        public static OciReference? Parse(string value)
        {
            var slash = value.IndexOf('/');
            if (slash <= 0)
            {
                return null;
            }

            var registry = value[..slash];
            var rest = value[(slash + 1)..];
            var tag = "latest";
            var colon = rest.LastIndexOf(':');
            if (colon > rest.LastIndexOf('/'))
            {
                tag = rest[(colon + 1)..];
                rest = rest[..colon];
            }

            return string.IsNullOrWhiteSpace(rest) ? null : new OciReference(registry, rest, tag);
        }
    }
}
