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
using System.Security.Cryptography;

namespace WslContainerDesktop.Services;

/// <summary>Only immutable catalog URLs/bytes; never executes downloaded content or extracts a whole archive.</summary>
public sealed class FoundryLocalDownloader : IDisposable
{
    public const string RetentionGuidance = "Verified setup cache is retained for offline reuse. Any interrupted .partial files are retained, never reused or installed; they may be removed manually from the setup cache.";
    private readonly string _cacheRoot;
    private readonly HttpClient _http;
    public string CacheLocation => _cacheRoot;

    public FoundryLocalDownloader(string cacheRoot) : this(cacheRoot, CreateHandler()) { }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        UseDefaultCredentials = false, Credentials = null,
    };

    internal FoundryLocalDownloader(string cacheRoot, HttpMessageHandler handler)
    {
        if (!Path.IsPathFullyQualified(cacheRoot) || cacheRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("An absolute local setup cache is required.");
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<FoundryLocalStagedPackages> StageAsync(FoundryLocalAuditedPackageSet package,
        bool includePrerequisite, IProgress<string>? progress, CancellationToken ct)
    {
        if (!package.IsDownloadable(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("No approved download manifest is available.");
        ct.ThrowIfCancellationRequested();
        var existingAncestor = _cacheRoot;
        while (!Directory.Exists(existingAncestor))
            existingAncestor = Path.GetDirectoryName(existingAncestor)
                ?? throw new IOException("Setup cache has no existing local parent.");
        RequireNoReparsePoints(existingAncestor);
        Directory.CreateDirectory(_cacheRoot);
        RequireNoReparsePoints(_cacheRoot);
        var runtime = await DownloadAsync(package.RuntimeDownloadUri!, package.Runtime.Bytes,
            package.Runtime.Sha256, ".msix", "Runtime MSIX", progress, ct).ConfigureAwait(false);
        if (!includePrerequisite) return new(runtime, null);
        var target = CachePath(package.VcLibs.Sha256, ".appx");
        if (File.Exists(target))
        {
            await using var cached = await OpenVerifiedAsync(target, package.VcLibs.Bytes, package.VcLibs.Sha256, ct).ConfigureAwait(false);
            progress?.Report("Reusing verified prerequisite APPX; no archive download needed.");
            return new(runtime, target);
        }
        var archive = package.VcLibsArchive!;
        var zipPath = await DownloadAsync(archive.DownloadUri, archive.Bytes, archive.Sha256,
            ".zip", "Prerequisite archive", progress, ct).ConfigureAwait(false);
        // Hash the same locked archive handle before ZIP parsing/extraction. A verified name
        // or a prior successful download never substitutes for verifying its current bytes.
        await using var zipFile = await OpenVerifiedAsync(zipPath, archive.Bytes, archive.Sha256, ct).ConfigureAwait(false);
        zipFile.Position = 0;
        using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.Where(entry => entry.FullName == archive.EntryPath).ToArray();
        if (entries.Length != 1 || entries[0].Length != package.VcLibs.Bytes)
            throw new InvalidDataException("The approved prerequisite entry is missing, duplicated or has a different size.");
        var partial = PartialPath(target);
        await using (var source = entries[0].Open())
        await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true))
            await CopyVerifiedAsync(source, destination, package.VcLibs.Bytes, package.VcLibs.Sha256,
                "Prerequisite extraction", progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(partial, target, overwrite: false);
        progress?.Report("Exact prerequisite APPX extracted and verified; other archive entries were not extracted.");
        return new(runtime, target);
    }

    private async Task<string> DownloadAsync(Uri approvedUri, long bytes, string sha256,
        string extension, string label, IProgress<string>? progress, CancellationToken ct)
    {
        var target = CachePath(sha256, extension);
        if (File.Exists(target))
        {
            await using var cached = await OpenVerifiedAsync(target, bytes, sha256, ct).ConfigureAwait(false);
            progress?.Report($"Reusing verified {label}; no network request.");
            return target;
        }
        using var response = await GetAsync(approvedUri, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != bytes)
            throw new InvalidDataException("Download Content-Length differs from the approved exact size.");
        var partial = PartialPath(target);
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true))
            await CopyVerifiedAsync(source, destination, bytes, sha256, label, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(partial, target, overwrite: false);
        progress?.Report($"{label} verified (100%).");
        return target;
    }

    private async Task<HttpResponseMessage> GetAsync(Uri approvedUri, CancellationToken ct)
    {
        var uri = approvedUri;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (!AllowedDownloadUri(uri))
                throw new InvalidDataException("Download redirect is outside the approved HTTPS release-asset hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WslContainerDesktop-FoundrySetup");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 3)
                    throw new InvalidDataException("Download redirect could not be resolved within its limit.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Approved artifact download failed with HTTP {code}. No retry was attempted.");
            }
            return response;
        }
        throw new InvalidDataException("Download redirect limit exceeded.");
    }

    internal static bool AllowedDownloadUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static async Task CopyVerifiedAsync(Stream source, Stream destination, long expectedBytes,
        string expectedHash, string label, IProgress<string>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long count = 0;
        var lastPercent = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > expectedBytes) throw new InvalidDataException("Artifact exceeds its approved size; download/extraction stopped.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            var percent = (int)Math.Min(99, count * 100 / expectedBytes);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress?.Report($"{label}: {percent}% (not verified until complete).");
            }
        }
        if (count != expectedBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact size/SHA256 does not match the approved manifest; partial retained, never installed.");
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<FileStream> OpenVerifiedAsync(string path, long bytes, string hash, CancellationToken ct)
    {
        RequireNoReparsePoints(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            if (stream.Length != bytes ||
                !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing setup cache failed verification and was retained. Remove the corrupt cached artifact manually before retrying.");
            return stream;
        }
        catch
        {
            // Preserve verification/cancellation failure while releasing its file handle.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string CachePath(string hash, string extension) => Path.Combine(_cacheRoot, hash.ToUpperInvariant() + extension);
    private static string PartialPath(string target) => target + "." + Guid.NewGuid().ToString("N") + ".partial";
    internal static void RequireNoReparsePoints(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not allowed in the setup cache.");
    }
    public void Dispose() => _http.Dispose();
}

internal sealed record FoundryLocalStagedPackages(string RuntimePath, string? PrerequisitePath);
