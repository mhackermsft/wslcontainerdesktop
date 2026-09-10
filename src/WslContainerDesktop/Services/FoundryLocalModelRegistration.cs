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

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace WslContainerDesktop.Services;

/// <summary>
/// Local-only registration of the pinned CPU model in the Foundry 0.10.3 scanner layout.
/// Receipts establish local integrity, not independent publisher authentication. Foreign or
/// corrupt entries fail closed; interruption retains the scanner sentinel and partial copies.
/// </summary>
public sealed class FoundryLocalModelRegistration
{
    public const string OwnedDirectoryName = "WslContainerDesktop-qwen-cpu-v4";
    internal const string MarkerName = ".wslcontainerdesktop-registration.json";
    private const string MetadataName = "inference_model.json";
    private const string SentinelName = "download.tmp";
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly byte[] Metadata = JsonSerializer.SerializeToUtf8Bytes(new
    {
        Name = FoundryLocalModelArtifacts.ModelId,
        PromptTemplate = new
        {
            system = "<|im_start|>system\n{Content}<|im_end|>",
            user = "<|im_start|>user\n{Content}<|im_end|>",
            assistant = "<|im_start|>assistant\n{Content}<|im_end|>",
            prompt = "<|im_start|>user\n{Content}<|im_end|>\n<|im_start|>assistant",
        },
    });
    private readonly FoundryLocalModelFile[] _files;
    private readonly string _manifestId;

    public FoundryLocalModelRegistration() : this(FoundryLocalModelArtifacts.PinnedFiles) { }

    // Synthetic sizes are available only to deterministic, offline tests.
    internal FoundryLocalModelRegistration(IReadOnlyList<FoundryLocalModelFile> files)
    {
        _files = files.ToArray();
        if (_files.Length != 9 || !_files.Select(f => f.Name).SequenceEqual(FoundryLocalModelArtifacts.PinnedFiles.Select(f => f.Name))
            || _files.Any(f => f.Bytes <= 0))
            throw new ArgumentException("The exact nine-file pinned manifest is required.", nameof(files));
        _manifestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            FoundryLocalModelArtifacts.AssetId + "\n" + FoundryLocalModelArtifacts.BlobContainer + "\n"
            + string.Join("\n", _files.Select(f => string.Join("|", f.Name, f.Bytes.ToString(CultureInfo.InvariantCulture),
                f.ETag, f.Modified.ToString("O", CultureInfo.InvariantCulture)))))));
    }

    /// <summary>
    /// Copies only locally staged, receipt-verified bytes. Explicit initial preparation may
    /// create a missing CLI-reported cache root. Returns the registered v4 directory, not load proof.
    /// Cancellation is recoverable by retry; corrupt/unowned entries require manual recovery.
    /// </summary>
    public async Task<string> RegisterAsync(FoundryLocalStagedModel staged, string externalCacheRoot,
        IProgress<string>? progress, CancellationToken ct, bool createCacheRoot = false)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Foundry cache registration requires Windows.");
        if (staged.AssetId != FoundryLocalModelArtifacts.AssetId || staged.Bytes != _files.Sum(f => f.Bytes))
            throw new InvalidDataException("Staged model identity or byte count differs from the pinned manifest.");
        var source = LocalPath(staged.DirectoryPath);
        var cache = LocalPath(externalCacheRoot);
        if (Path.GetFileName(source) != "v4"
            || Path.GetFileName(Path.GetDirectoryName(source)) != FoundryLocalModelArtifacts.VersionId)
            throw new InvalidDataException("The staged model must have the stager's pinned v4 layout.");
        var stageRoot = Path.GetDirectoryName(source)!;
        if (cache.Equals(stageRoot, StringComparison.OrdinalIgnoreCase)
            || cache.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The external cache must not be inside the staged model directory.", nameof(externalCacheRoot));
        var owned = Path.Combine(cache, OwnedDirectoryName);
        var payload = Path.Combine(owned, "v4");
        var receipts = Path.Combine(stageRoot, "receipts");

        using var leases = new Leases();
        leases.PinDirectories(source);
        leases.PinDirectories(receipts);
        leases.PinDirectories(cache, createCacheRoot);
        RequireEntries(source, _files.Select(f => f.Name));
        var sources = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in _files)
        {
            ct.ThrowIfCancellationRequested();
            using var receiptStream = OpenFile(Path.Combine(receipts, file.Name + ".json"));
            var receipt = await ReadJsonAsync<StageReceipt>(receiptStream, 4096, ct).ConfigureAwait(false);
            if (receipt.ManifestId != _manifestId || receipt.Asset != staged.AssetId || receipt.Name != file.Name
                || receipt.Bytes != file.Bytes || receipt.ETag != file.ETag || receipt.Modified != file.Modified
                || receipt.Sha256 is null || receipt.Sha256.Length != 64)
                throw new InvalidDataException("A source receipt does not match the pinned model manifest.");
            var stream = leases.Add(OpenFile(Path.Combine(source, file.Name)));
            var hash = await HashAsync(stream, file.Bytes, ct).ConfigureAwait(false);
            if (hash != receipt.Sha256) throw new InvalidDataException("Staged model content no longer matches its integrity receipt.");
            sources.Add(file.Name, stream);
            hashes.Add(file.Name, hash);
        }

        ct.ThrowIfCancellationRequested();
        // CreateDirectoryW, unlike Directory.CreateDirectory, distinguishes a new reservation from
        // a pre-existing empty directory. Ancestor handles deny rename/delete for the whole operation.
        var created = CreateDirectory(owned, IntPtr.Zero);
        if (!created && Marshal.GetLastWin32Error() != 183) ThrowIo("Cannot reserve the model cache directory.");
        leases.PinDirectories(owned);
        var markerPath = Path.Combine(owned, MarkerName);
        RegistrationReceipt marker;
        using var markerStream = OpenFile(markerPath, create: created, exclusive: true, writable: created);
        if (created)
        {
            marker = new("WslContainerDesktop.FoundryRegistration", 1, Guid.NewGuid().ToString("N"), owned,
                staged.AssetId, _manifestId, hashes);
            // Commit ownership without cancellation before creating any scanner-visible payload.
            await JsonSerializer.SerializeAsync(markerStream, marker, JsonOptions, CancellationToken.None).ConfigureAwait(false);
            markerStream.Flush(flushToDisk: true);
        }
        else
        {
            marker = await ReadJsonAsync<RegistrationReceipt>(markerStream, 16384, ct).ConfigureAwait(false);
            if (marker.Owner != "WslContainerDesktop.FoundryRegistration" || marker.Schema != 1
                || !Guid.TryParseExact(marker.Reservation, "N", out _)
                || marker.Directory != owned || marker.Asset != staged.AssetId || marker.ManifestId != _manifestId
                || marker.Hashes is null || marker.Hashes.Count != hashes.Count
                || hashes.Any(pair => !marker.Hashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
                throw new InvalidDataException("Existing registration is not a compatible app-owned reservation.");
        }
        RequireEntries(owned, [MarkerName, "v4", "partials"]);
        var newPayload = CreateDirectory(payload, IntPtr.Zero);
        if (!newPayload && Marshal.GetLastWin32Error() != 183) ThrowIo("Cannot create the owned model payload.");
        leases.PinDirectories(payload);
        RequireEntries(payload, _files.Select(f => f.Name).Concat([MetadataName, SentinelName]));
        var partials = Path.Combine(owned, "partials");
        var newPartials = CreateDirectory(partials, IntPtr.Zero);
        if (!newPartials && Marshal.GetLastWin32Error() != 183) ThrowIo("Cannot create owned partial storage.");
        leases.PinDirectories(partials);
        foreach (var entry in Directory.EnumerateFileSystemEntries(partials))
        {
            if (!Path.GetFileName(entry).EndsWith(".partial", StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(entry), "N", out _))
                throw new InvalidDataException("Owned partial storage contains an unexpected entry.");
            using var check = OpenFile(entry);
        }

        var sentinelPath = Path.Combine(payload, SentinelName);
        // Every incomplete or re-verifying registration remains hidden from the scanner.
        var sentinelExists = EntryExists(sentinelPath);
        using var sentinel = OpenFile(sentinelPath, create: !sentinelExists, exclusive: true, writable: true, deleteAccess: true);
        var sentinelBytes = Encoding.UTF8.GetBytes(marker.Reservation);
        if (sentinelExists)
        {
            if (!(await ReadBytesAsync(sentinel, 64, CancellationToken.None).ConfigureAwait(false)).SequenceEqual(sentinelBytes))
                throw new InvalidDataException("The registration sentinel is not owned by this reservation.");
        }
        else
        {
            await sentinel.WriteAsync(sentinelBytes, CancellationToken.None).ConfigureAwait(false);
            sentinel.Flush(flushToDisk: true);
        }
        progress?.Report("Registering local model files; download.tmp retained until verification completes.");
        ct.ThrowIfCancellationRequested();

        foreach (var file in _files)
        {
            var target = Path.Combine(payload, file.Name);
            if (!EntryExists(target))
            {
                sources[file.Name].Position = 0;
                await CopyNewAsync(sources[file.Name], target, partials, ct).ConfigureAwait(false);
            }
            var registered = leases.Add(OpenFile(target));
            if (await HashAsync(registered, file.Bytes, ct).ConfigureAwait(false) != hashes[file.Name])
                throw new InvalidDataException("Registered model content differs from verified staging; manual recovery is required.");
            progress?.Report($"Verified registered {file.Name}.");
        }
        var metadataPath = Path.Combine(payload, MetadataName);
        if (!EntryExists(metadataPath))
        {
            using var input = new MemoryStream(Metadata, writable: false);
            await CopyNewAsync(input, metadataPath, partials, ct).ConfigureAwait(false);
        }
        var metadataStream = leases.Add(OpenFile(metadataPath));
        if (!(await ReadBytesAsync(metadataStream, Metadata.Length, ct).ConfigureAwait(false)).SequenceEqual(Metadata))
            throw new InvalidDataException("Registered inference metadata differs from the pinned scanner metadata.");
        RequireEntries(payload, _files.Select(f => f.Name).Concat([MetadataName, SentinelName]));
        ct.ThrowIfCancellationRequested();
        // Delete by the verified, exclusive sentinel handle, never by a possibly replaced pathname.
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(sentinel.SafeFileHandle, 4, ref disposition, 1))
            ThrowIo("Cannot complete model registration; download.tmp retained.");
        return payload;
    }

    private static async Task CopyNewAsync(Stream source, string target, string partials, CancellationToken ct)
    {
        var partial = Path.Combine(partials, Guid.NewGuid().ToString("N") + ".partial");
        using (var output = OpenFile(partial, create: true, exclusive: true, writable: true))
        {
            await source.CopyToAsync(output, ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        File.Move(partial, target, overwrite: false);
    }

    private static async Task<string> HashAsync(FileStream stream, long expected, CancellationToken ct)
    {
        if (stream.Length != expected) throw new InvalidDataException("Model file size differs from the pinned manifest.");
        stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task<T> ReadJsonAsync<T>(FileStream stream, int limit, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(stream, limit, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Missing registration receipt.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate receipt property.");
            RejectDuplicateProperties(property.Value);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(FileStream stream, int limit, CancellationToken ct)
    {
        if (stream.Length > limit) throw new InvalidDataException("Oversized registration metadata.");
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('/')
            || path.Split('\\').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))
            || path[2..].Contains(':'))
            throw new ArgumentException("An absolute local path without traversal or device syntax is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool EntryExists(string path)
    {
        var attributes = GetFileAttributes(path);
        if (attributes != uint.MaxValue) return true;
        var error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return false;
        throw new IOException("Cannot inspect a registration entry.", new Win32Exception(error));
    }

    private static void RequireEntries(string directory, IEnumerable<string> allowed)
    {
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        if (Directory.EnumerateFileSystemEntries(directory).Any(p => !names.Contains(Path.GetFileName(p))))
            throw new InvalidDataException("Model directory contains unexpected entries; nothing was overwritten.");
    }

    private static FileStream OpenFile(string path, bool create = false, bool exclusive = false,
        bool writable = false, bool deleteAccess = false)
    {
        var handle = OpenHandle(path, 0x80000000u | (writable ? 0x40000000u : 0) | (deleteAccess ? 0x10000u : 0),
            exclusive ? 0u : 1u, create ? 1u : 3u, directory: false);
        try { return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenHandle(string path, uint access, uint share, uint creation, bool directory)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, creation, 0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Cannot exclusively access an ordinary local registration entry.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var info, 8)
            || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
        {
            handle.Dispose();
            throw new InvalidDataException("Reparse points and unexpected entry types are not allowed in model registration.");
        }
        return handle;
    }

    private static void ThrowIo(string message) => throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed class Leases : IDisposable
    {
        private readonly List<IDisposable> _handles = [];
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        public T Add<T>(T handle) where T : IDisposable { _handles.Add(handle); return handle; }
        public void PinDirectories(string path, bool createMissing = false)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is not null) PinDirectories(parent, createMissing);
            if (_directories.Add(path))
            {
                if (createMissing && !EntryExists(path)
                    && !CreateDirectory(path, IntPtr.Zero) && Marshal.GetLastWin32Error() != 183)
                    ThrowIo("Cannot create the explicitly approved model cache directory.");
                Add(OpenHandle(path, 0, 3, 3, directory: true));
            }
        }
        public void Dispose()
        {
            for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        }
    }

    private sealed record StageReceipt(string ManifestId, string Asset, string Name, long Bytes, string ETag, DateTimeOffset Modified, string Sha256);
    private sealed record RegistrationReceipt(string Owner, int Schema, string Reservation, string Directory, string Asset,
        string ManifestId, Dictionary<string, string> Hashes);
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr securityAttributes,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition info, uint size);
}
