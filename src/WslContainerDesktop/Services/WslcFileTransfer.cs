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

/// <summary>Uses native <c>wslc container cp</c>; native failures are never retried through another backend.</summary>
internal sealed class WslcFileTransfer(
    Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>> run,
    Func<string, IEnumerable<string>, string, CancellationToken, Task<CommandResult>> runWithInput,
    Func<string> executablePath,
    Func<string> stagingRoot)
{
    /// <summary>Copies a file or directory from a container to the Windows file system.</summary>
    public async Task<CommandResult> CopyFromAsync(
        string id, string containerPath, string hostDirectory, CancellationToken ct = default, bool followSymlinks = false)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var destination = ContainerDownloadPath.Create(containerPath, hostDirectory);
            destination.Prepare(ct);
            var args = new List<string> { "container", "cp", "--quiet" };
            if (followSymlinks)
            {
                args.Add("--follow-link");
            }

            args.Add($"{id}:{destination.Source}");
            args.Add(destination.Directory);
            var result = await run(executablePath(), args, ct)
                .ConfigureAwait(false);
            return WithNativeDiagnostic(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new CommandResult { ExitCode = -1, StandardError = $"Could not copy files: {ex.Message}" };
        }
    }

    /// <summary>Copies a Windows file or directory into a container path.</summary>
    public async Task<CommandResult> CopyToAsync(
        string id, string hostPath, string containerDirectory, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
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
                    await using var writer = new System.Formats.Tar.TarWriter(archive, System.Formats.Tar.TarEntryFormat.Pax);
                    await WriteSourceAsync(writer, source, member, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                // Retain a read-only handle to prevent modification during transfer. cmd's
                // redirection does not share delete access, so DeleteOnClose cannot be used.
                using var guard = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                // Native copy accepts a seekable tar on stdin. Relative, traversal-free member
                // names preserve mkdir-p and basename semantics without an in-container shell.
                var result = await runWithInput(executablePath(), ["container", "cp", "--quiet", "-", $"{id}:/"], archivePath, ct)
                    .ConfigureAwait(false);
                return WithNativeDiagnostic(result);
            }
            finally
            {
                if (archiveCreated)
                {
                    await DeleteArchiveAsync(archivePath, ct).ConfigureAwait(false);
                }
            }
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

    /// <summary>Builds the archive or file stream passed to <c>wslc container cp</c> standard input.</summary>
    internal static async Task WriteSourceAsync(
        System.Formats.Tar.TarWriter writer, string source, string member, CancellationToken ct,
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
            var entry = new System.Formats.Tar.PaxTarEntry(
                isDirectory ? System.Formats.Tar.TarEntryType.Directory : System.Formats.Tar.TarEntryType.RegularFile,
                member)
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

    /// <summary>Prepares a destination path so replacing an existing file or directory is predictable.</summary>
    internal static void PrepareOverwrite(
        string path, CancellationToken ct, Func<string, FileAttributes>? readAttributes = null) =>
        ContainerDownloadPath.PrepareOverwrite(path, ct, readAttributes);

    private static CommandResult WithNativeDiagnostic(CommandResult result) => result.Success ? result : new CommandResult
    {
        ExitCode = result.ExitCode,
        StandardOutput = result.StandardOutput,
        StandardError = $"{result.ErrorText}\nNative copy failed; no retry was attempted. Check host tar.exe, staging/destination access and free space. Links and metadata follow the native archive implementation; ownership preservation is not guaranteed.",
    };
}
