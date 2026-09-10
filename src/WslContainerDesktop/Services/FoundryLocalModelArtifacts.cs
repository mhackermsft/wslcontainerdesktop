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

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslContainerDesktop.Services;

/// <summary>
/// Consent-gated, origin-conditional staging only. Local SHA256 receipts detect subsequent cache
/// corruption; they are NOT independent publisher digests. Nothing is registered, loaded or executed.
/// </summary>
public sealed class FoundryLocalModelArtifacts : IDisposable
{
    public const string DisplayName = "Qwen2.5-0.5B-Instruct (generic CPU), version 4";
    public const string AssetId = "azureml://registries/azureml/models/qwen2.5-0.5b-instruct-generic-cpu/versions/4";
    public const string VersionId = "qwen2.5-0.5b-instruct-generic-cpu-v4";
    public const long TotalBytes = 877988985;
    public const string License = "Apache-2.0";
    public const string LicenseUrl = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/blob/main/LICENSE";
    public const string VerificationNotice = "Staged using pinned official-origin ETags, dates and sizes. Local SHA256 receipts provide subsequent cache integrity, not independent publisher verification. Not registered or loaded.";
    public const string RetentionGuidance = "Interrupted .partial files are retained and never resumed. Repeated staging rehashes completed files; corrupt or unreceipted cache requires manual recovery before retrying.";
    public static string ConsentSummary =>
        $"Download model files only: {DisplayName}\n"
        + $"Identity: {AssetId}\n"
        + $"Exact download: {TotalBytes.ToString(CultureInfo.InvariantCulture)} bytes across nine files.\n"
        + $"License: {License} — {LicenseUrl}\n"
        + "Pinned model and all nine files were published November 14, 2025; the seven-day age policy is enforced before acquisition.\n"
        + "This operation contacts the official Azure model registry and pinned Azure Blob container only when completed files are missing. It does not import into the Foundry cache, register, load or execute a model or acquire execution providers.\n"
        + VerificationNotice + "\n" + RetentionGuidance;
    internal const string BlobContainer = "https://amlwlrt4usc01.blob.core.windows.net/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3";
    internal static readonly Uri RegistryUri = new("https://centralus.api.azureml.ms/modelregistry/v1.0/registry/models/nonazureaccount?assetId=" + Uri.EscapeDataString(AssetId));
    private static readonly DateTimeOffset Created = new DateTimeOffset(2025, 11, 14, 7, 39, 18, TimeSpan.Zero).AddTicks(7283746);
    private static readonly DateTimeOffset BlobDate = new(2025, 11, 14, 7, 37, 27, TimeSpan.Zero);
    internal static IReadOnlyList<FoundryLocalModelFile> PinnedFiles { get; } = Array.AsReadOnly(new[]
    {
        new FoundryLocalModelFile("added_tokens.json", 605, "0x8DE2350A9009EF0", BlobDate),
        new FoundryLocalModelFile("genai_config.json", 1517, "0x8DE2350A90F3495", BlobDate),
        new FoundryLocalModelFile("merges.txt", 1671853, "0x8DE2350A94CE2CF", BlobDate),
        new FoundryLocalModelFile("model.onnx", 169136, "0x8DE2350A91B3519", BlobDate),
        new FoundryLocalModelFile("model.onnx.data", 861939200, "0x8DE2350AA9E9C1E", BlobDate.AddSeconds(3)),
        new FoundryLocalModelFile("special_tokens_map.json", 613, "0x8DE2350A91327D1", BlobDate),
        new FoundryLocalModelFile("tokenizer.json", 11421894, "0x8DE2350A96AF775", BlobDate),
        new FoundryLocalModelFile("tokenizer_config.json", 7334, "0x8DE2350A9121792", BlobDate),
        new FoundryLocalModelFile("vocab.json", 2776833, "0x8DE2350A9346D32", BlobDate),
    });
    private static readonly JsonSerializerOptions ReceiptOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly FoundryLocalModelFile[] _files;
    private readonly string _manifestId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _timeProvider;
    public string CacheLocation => _root;

    public FoundryLocalModelArtifacts(string cacheRoot)
        : this(cacheRoot, FoundryLocalDownloader.CreateHandler(), PinnedFiles, () => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(45)) { }

    // Only deterministic tests may inject synthetic metadata; callers cannot supply provenance.
    internal FoundryLocalModelArtifacts(string cacheRoot, HttpMessageHandler handler,
        IReadOnlyList<FoundryLocalModelFile> files, Func<DateTimeOffset> utcNow, TimeSpan deadline, TimeProvider? timeProvider = null)
    {
        if (!Path.IsPathFullyQualified(cacheRoot) || cacheRoot.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("An absolute local model cache is required.", nameof(cacheRoot));
        _root = Path.Combine(Path.GetFullPath(cacheRoot), VersionId);
        _files = files.ToArray();
        if (_files.Length != 9 || !_files.Select(f => f.Name).SequenceEqual(PinnedFiles.Select(f => f.Name))
            || _files.Any(f => f.Bytes <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(f.ETag, "^0x[0-9A-F]+$")))
            throw new ArgumentException("An exact nine-file model manifest is required.", nameof(files));
        _manifestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            AssetId + "\n" + BlobContainer + "\n" + string.Join("\n", _files.Select(f =>
                string.Join("|", f.Name, f.Bytes.ToString(CultureInfo.InvariantCulture), f.ETag, f.Modified.ToString("O", CultureInfo.InvariantCulture)))))));
        _utcNow = utcNow;
        _deadline = deadline;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Call only after explicit model-staging consent. The returned directory contains the nine model files, not receipts.</summary>
    public async Task<FoundryLocalStagedModel> StageAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_deadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await StageCoreAsync(progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Never retain HTTP/stream exception chains: they can contain the secret SAS URI.
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Model staging cancelled; partial files retained.", ct);
            throw new TimeoutException("Model staging deadline exceeded; partial files retained. No automatic retry.");
        }
        catch (StagingFailure failure)
        {
            throw new InvalidDataException(failure.Message);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException or CryptographicException)
        {
            // Includes registry JSON, transport, stream and filesystem errors; do not echo response bodies or URIs.
            throw new InvalidDataException("Model staging failed. Check cache access and connectivity; remove corrupt cache files and their receipts manually before retrying. Partial files retained; no automatic retry.");
        }
    }

    private async Task<FoundryLocalStagedModel> StageCoreAsync(IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = _utcNow().Subtract(TimeSpan.FromDays(7));
        if (Created > cutoff || _files.Any(f => f.Modified > cutoff))
            throw new StagingFailure("Pinned model publication dates do not satisfy the seven-day dependency age policy.");
        EnsureDirectory(_root);
        var payload = Path.Combine(_root, "v4");
        var receipts = Path.Combine(_root, "receipts");
        var partials = Path.Combine(_root, "partials");
        var lockPath = Path.Combine(_root, ".stage.lock");
        CheckIfExists(lockPath);
        using var stageLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureDirectory(payload);
        EnsureDirectory(receipts);
        EnsureDirectory(partials);
        var expected = _files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (Directory.EnumerateFileSystemEntries(payload).Any(p => !expected.Contains(Path.GetFileName(p))))
            throw new StagingFailure("Model payload contains unexpected entries. Remove unexpected cache entries manually before retrying.");

        // Validate every existing entry before resolving a SAS or making any request.
        var missing = new List<FoundryLocalModelFile>();
        foreach (var file in _files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(payload, file.Name);
            var receipt = Path.Combine(receipts, file.Name + ".json");
            CheckIfExists(target);
            CheckIfExists(receipt);
            if (File.Exists(target) && File.Exists(receipt))
            {
                await VerifyCachedAsync(target, receipt, file, ct).ConfigureAwait(false);
                progress?.Report($"Rehashed cached {file.Name}; receipt matches the pinned manifest.");
            }
            else if (Path.Exists(target) || Path.Exists(receipt))
                throw new StagingFailure("Incomplete model cache receipt pair. Remove the affected file and receipt manually before retrying; unreceipted bytes are never reused.");
            else missing.Add(file);
        }

        if (missing.Count > 0)
        {
            var sas = await ResolveAsync(ct).ConfigureAwait(false);
            foreach (var file in missing)
            {
                ct.ThrowIfCancellationRequested();
                var uri = new Uri(BlobContainer + "/v4/" + file.Name + sas.Query);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + file.ETag + '"'));
                using var response = await SendAsync(request, ct).ConfigureAwait(false);
                if (response.Headers.ETag is not { IsWeak: false } etag || etag.Tag != '"' + file.ETag + '"'
                    || response.Content.Headers.ContentLength != file.Bytes
                    || response.Content.Headers.LastModified != file.Modified)
                    throw new StagingFailure("Model response ETag, Last-Modified or Content-Length is missing or differs from the pinned manifest. No retry.");
                if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentRange is not null)
                    throw new StagingFailure("Encoded or partial model responses are not accepted.");
                var partial = Path.Combine(partials, file.Name + "." + Guid.NewGuid().ToString("N") + ".partial");
                string hash;
                await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    hash = await HashStreamAsync(source, destination, file.Bytes, file.Name, progress, ct).ConfigureAwait(false);
                    await destination.FlushAsync(ct).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                }
                ct.ThrowIfCancellationRequested();
                var receiptPartial = Path.Combine(partials, file.Name + ".receipt." + Guid.NewGuid().ToString("N") + ".partial");
                var receipt = new Receipt(_manifestId, AssetId, file.Name, file.Bytes, file.ETag, file.Modified, hash);
                await using (var stream = new FileStream(receiptPartial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, receipt, ReceiptOptions, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                ct.ThrowIfCancellationRequested();
                FoundryLocalDownloader.RequireNoReparsePoints(payload);
                FoundryLocalDownloader.RequireNoReparsePoints(receipts);
                File.Move(partial, Path.Combine(payload, file.Name), overwrite: false);
                // A crash between these renames fails closed as an incomplete receipt pair.
                File.Move(receiptPartial, Path.Combine(receipts, file.Name + ".json"), overwrite: false);
                progress?.Report($"Staged {file.Name} with origin-conditional retrieval and a local integrity receipt.");
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(payload, AssetId, _files.Sum(f => f.Bytes));
    }

    private async Task VerifyCachedAsync(string target, string receiptPath, FoundryLocalModelFile file, CancellationToken ct)
    {
        await using var receiptStream = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (receiptStream.Length > 4096) throw new StagingFailure("Model cache receipt is oversized; manual cache recovery is required.");
        var bytes = await ReadBoundedAsync(receiptStream, 4096, ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || json.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 7)
            throw new StagingFailure("Malformed model cache receipt; manual cache recovery is required.");
        if (json.RootElement.EnumerateObject().Count() != 7)
            throw new StagingFailure("Duplicate model cache receipt fields; manual cache recovery is required.");
        var receipt = JsonSerializer.Deserialize<Receipt>(bytes, ReceiptOptions);
        if (receipt is null || receipt.ManifestId != _manifestId || receipt.Asset != AssetId || receipt.Name != file.Name
            || receipt.Bytes != file.Bytes || receipt.ETag != file.ETag || receipt.Modified != file.Modified
            || receipt.Sha256 is null || !System.Text.RegularExpressions.Regex.IsMatch(receipt.Sha256, "^[0-9A-F]{64}$"))
            throw new StagingFailure("Model cache receipt does not match the pinned manifest; remove the affected file and receipt manually before retrying.");
        await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length != file.Bytes || await HashStreamAsync(stream, null, file.Bytes, file.Name, null, ct).ConfigureAwait(false) != receipt.Sha256)
            throw new StagingFailure("Model cache integrity failed; remove the affected file and receipt manually before retrying.");
    }

    private async Task<Uri> ResolveAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RegistryUri);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > 65536)
            throw new StagingFailure("Model registry metadata exceeds the 64 KiB limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(stream, 65536, ct).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new StagingFailure("Model registry metadata has an unknown shape.");
        var properties = json.RootElement.EnumerateObject().Where(p => p.Name == "blobSasUri").ToArray();
        if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(properties[0].Value.GetString(), UriKind.Absolute, out var sas)
            || sas.Scheme != "https" || sas.Port != 443 || sas.UserInfo.Length != 0 || sas.Fragment.Length != 0
            || sas.GetLeftPart(UriPartial.Path) != BlobContainer || sas.Query.Length <= 1)
            throw new StagingFailure("Model registry blobSasUri is missing, ambiguous or outside the exact approved HTTPS origin/container.");
        return sas;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new StagingFailure($"Model staging requires HTTP 200; received HTTP {status}. Redirects, credential refresh and automatic retries are disabled.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1L - memory.Length)), ct).ConfigureAwait(false);
            if (read == 0) return memory.ToArray();
            if (memory.Length + read > limit) throw new StagingFailure("Model metadata or receipt exceeds its bounded size.");
            memory.Write(buffer, 0, read);
        }
    }

    private static async Task<string> HashStreamAsync(Stream source, Stream? destination, long expected,
        string name, IProgress<string>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long count = 0;
        var lastPercent = -1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - count + 1)), ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > expected) throw new StagingFailure("Model stream exceeds its pinned size; partial retained, never completed.");
            hash.AppendData(buffer, 0, read);
            if (destination is not null) await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            var percent = (int)Math.Min(99, count * 100 / expected);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress?.Report($"{name}: {percent}% (not complete).");
            }
        }
        if (count != expected) throw new StagingFailure("Model stream is shorter than its pinned size; partial retained, never completed.");
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void EnsureDirectory(string path)
    {
        var ancestor = path;
        while (!Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new StagingFailure("Model cache requires an existing local parent.");
        FoundryLocalDownloader.RequireNoReparsePoints(ancestor);
        Directory.CreateDirectory(path);
        FoundryLocalDownloader.RequireNoReparsePoints(path);
    }

    private static void CheckIfExists(string path)
    {
        if (Path.Exists(path)) FoundryLocalDownloader.RequireNoReparsePoints(path);
    }

    public void Dispose() => _http.Dispose();
    private sealed class StagingFailure(string message) : Exception(message);
    private sealed record Receipt(string ManifestId, string Asset, string Name, long Bytes, string ETag, DateTimeOffset Modified, string Sha256);
}

internal sealed record FoundryLocalModelFile(string Name, long Bytes, string ETag, DateTimeOffset Modified);

/// <summary>Staged payload only; this does not establish Foundry cache registration or model readiness.</summary>
public sealed record FoundryLocalStagedModel(string DirectoryPath, string AssetId, long Bytes);
