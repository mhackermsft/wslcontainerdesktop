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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Formats.Tar;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Selects a transfer backend before any mutation; native failures are never retried.</summary>
internal sealed class WslcFileTransfer(
    IWslcCapabilitiesService capabilities,
    Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>> run,
    Func<string, IEnumerable<string>, string, CancellationToken, Task<CommandResult>> runWithInput,
    Func<string> stagingRoot)
{
    public Task<CommandResult> CopyFromAsync(
        string id, string containerPath, string hostDirectory,
        Func<CancellationToken, Task<CommandResult>> legacy, CancellationToken ct = default) =>
        SelectAsync(legacy, async (executable, token) =>
        {
            ValidateContainerPath(containerPath);
            var source = containerPath.TrimEnd('/');
            if (source.Length == 0)
            {
                source = "/";
            }
            var name = source[(source.LastIndexOf('/') + 1)..];
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("The source basename cannot be represented as a Windows filename.");
            }

            var destination = Path.GetFullPath(hostDirectory);
            // WSLC strips trailing separators; a drive root would become drive-relative.
            if (string.Equals(
                destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetPathRoot(destination)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Select a destination folder below the drive or share root.");
            }
            Directory.CreateDirectory(destination);
            if (name.Length > 0)
            {
                PrepareOverwrite(Path.Combine(destination, name), token);
            }
            // WSLC uses host tar.exe to extract. An existing directory prevents rename semantics.
            var result = await run(executable, ["container", "cp", $"{id}:{source}", destination], token)
                .ConfigureAwait(false);
            return WithNativeDiagnostic(result);
        }, ct);

    public Task<CommandResult> CopyToAsync(
        string id, string hostPath, string containerDirectory,
        Func<CancellationToken, Task<CommandResult>> legacy, CancellationToken ct = default) =>
        SelectAsync(legacy, async (executable, token) =>
        {
            ValidateContainerPath(containerDirectory);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostPath));
            var basename = Path.GetFileName(source);
            if (string.IsNullOrEmpty(basename))
            {
                throw new ArgumentException("Select a file or directory with a basename, not a drive root.");
            }
            if (!Path.Exists(source))
            {
                throw new FileNotFoundException("Host source was not found.", source);
            }

            var prefix = containerDirectory.Trim('/');
            var member = prefix.Length == 0 ? basename : $"{prefix}/{basename}";
            var staging = stagingRoot();
            Directory.CreateDirectory(staging);
            var archivePath = Path.Combine(staging, $"{Guid.NewGuid():N}.tar");
            var archiveCreated = false;
            try
            {
                await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    archiveCreated = true;
                    await using var writer = new TarWriter(archive, TarEntryFormat.Pax);
                    await WriteSourceAsync(writer, source, member, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                // Retain a read-only handle to prevent modification during transfer. cmd's
                // redirection does not share delete access, so DeleteOnClose cannot be used.
                using var guard = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                // 2.9.11 accepts a seekable tar on stdin. Relative, traversal-free member names
                // preserve mkdir-p and basename semantics without an in-container shell.
                var result = await runWithInput(executable, ["container", "cp", "-", $"{id}:/"], archivePath, token)
                    .ConfigureAwait(false);
                return WithNativeDiagnostic(result);
            }
            finally
            {
                if (archiveCreated)
                {
                    await DeleteArchiveAsync(archivePath, token).ConfigureAwait(false);
                }
            }
        }, ct);

    private async Task<CommandResult> SelectAsync(
        Func<CancellationToken, Task<CommandResult>> legacy,
        Func<string, CancellationToken, Task<CommandResult>> native, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = await capabilities.GetAsync(ct).ConfigureAwait(false);
        var capability = snapshot[WslcFeature.ContainerCp];
        if (capability.Support == WslcCapabilitySupport.Unsupported)
        {
            var result = await legacy(ct).ConfigureAwait(false);
            return result.Success ? result : new CommandResult
            {
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = $"{result.ErrorText}\nThis engine uses legacy transfer: the container must be running with sh, base64 and (for directories) tar.",
            };
        }
        if (capability.Support != WslcCapabilitySupport.Supported)
        {
            return new CommandResult
            {
                ExitCode = -1,
                StandardError = $"Could not determine native container copy availability: {capability.Diagnostic}",
            };
        }

        try
        {
            return await native(snapshot.ExecutablePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new CommandResult { ExitCode = -1, StandardError = $"Could not copy files: {ex.Message}" };
        }
    }

    private static void ValidateContainerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') ||
            path.Contains('\0') || path.Split('/').Any(part => part is "." or ".."))
        {
            throw new ArgumentException("The container path must be absolute and must not contain '.' or '..' segments.");
        }
    }

    private static async Task DeleteArchiveAsync(string archivePath, CancellationToken ct)
    {
        // The session service briefly retains its duplicated input handle after client
        // cancellation. Wait only for sharing violations, and never retry the transfer.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(archivePath);
                return;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && attempt < 30)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"Copy cancelled, but the staged archive could not be deleted: {archivePath}", ex, ct);
            }
        }
    }

    internal static async Task WriteSourceAsync(
        TarWriter writer, string source, string member, CancellationToken ct,
        Func<string, FileAttributes>? readAttributes = null)
    {
        ct.ThrowIfCancellationRequested();
        var attributes = (readAttributes ?? File.GetAttributes)(source);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = isDirectory ? new DirectoryInfo(source) : new FileInfo(source);
        var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        var isLink = isReparsePoint && info.LinkTarget is not null;
        if (isReparsePoint && !isLink)
        {
            // TarWriter's path overload assumes every Windows reparse point is a link.
            // Cloud-backed files/directories have no link target; read their normal contents.
            var entry = new PaxTarEntry(isDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, member)
            {
                ModificationTime = info.LastWriteTimeUtc,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            };
            await using var input = isDirectory ? null : new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (input is not null)
            {
                entry.DataStream = input;
            }
            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        }
        else
        {
            await writer.WriteEntryAsync(source, member, ct).ConfigureAwait(false);
        }
        // Preserve links in the archive, but never enumerate their targets.
        if (isDirectory && !isLink)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                await WriteSourceAsync(writer, child, $"{member}/{Path.GetFileName(child)}", ct, readAttributes).ConfigureAwait(false);
            }
        }
    }

    internal static void PrepareOverwrite(
        string path, CancellationToken ct, Func<string, FileAttributes>? readAttributes = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.Exists(path))
        {
            return;
        }
        var attributes = (readAttributes ?? File.GetAttributes)(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
        {
            throw new IOException("The download would overwrite a host symbolic link. Choose another destination.");
        }
        if (isDirectory)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                PrepareOverwrite(child, ct, readAttributes);
            }
        }
        else if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static CommandResult WithNativeDiagnostic(CommandResult result) => result.Success ? result : new CommandResult
    {
        ExitCode = result.ExitCode,
        StandardOutput = result.StandardOutput,
        StandardError = $"{result.ErrorText}\nNative copy failed; no legacy retry was attempted. Check host tar.exe, staging/destination access and free space. Links and metadata follow the native archive implementation; ownership preservation is not guaranteed.",
    };
}
