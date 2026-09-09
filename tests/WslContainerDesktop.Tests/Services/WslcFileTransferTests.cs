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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcFileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcd-copy-tests-{Guid.NewGuid():N}");
    private string Staging => Path.Combine(_root, "staging");
    private int _legacyCalls;
    private int _nativeCalls;

    public WslcFileTransferTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedSelectsOnlyLegacy(bool upload)
    {
        var transfer = Create(WslcCapabilitySupport.Unsupported);
        var result = upload
            ? await transfer.CopyToAsync("container-id", "source", "/dest", Legacy)
            : await transfer.CopyFromAsync("container-id", "/source", "dest", Legacy);
        Assert.True(result.Success);
        Assert.Equal(1, _legacyCalls);
        Assert.Equal(0, _nativeCalls);
        Assert.False(Directory.Exists(Staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSurfacesDiagnosticWithoutAnyTransfer(bool upload)
    {
        var transfer = Create(WslcCapabilitySupport.Unknown);
        var result = upload
            ? await transfer.CopyToAsync("container-id", "source", "/dest", Legacy)
            : await transfer.CopyFromAsync("container-id", "/source", "dest", Legacy);
        Assert.False(result.Success);
        Assert.Contains("probe timed out", result.ErrorText);
        Assert.Equal(0, _legacyCalls);
        Assert.Equal(0, _nativeCalls);
    }

    [Fact]
    public async Task DownloadCreatesDirectoryKeepsBasenameAndClearsReadonly()
    {
        var destination = Path.Combine(_root, "new destination");
        Directory.CreateDirectory(destination);
        var existing = Path.Combine(destination, "report.bin");
        await File.WriteAllBytesAsync(existing, [1]);
        File.SetAttributes(existing, FileAttributes.ReadOnly);
        var transfer = Create(run: (_, arguments, _) =>
        {
            Assert.Equal(["container", "cp", "container-id:/space dir/report.bin", destination], arguments);
            Assert.True(Directory.Exists(destination));
            Assert.False(File.GetAttributes(existing).HasFlag(FileAttributes.ReadOnly));
            return Task.FromResult(new CommandResult());
        });
        Assert.True((await transfer.CopyFromAsync("container-id", "/space dir/report.bin/", destination, Legacy)).Success);
        Assert.Equal(0, _legacyCalls);
    }

    [Fact]
    public async Task DownloadFailureNeverFallsBackOrRenamesMissingDestination()
    {
        var destination = Path.Combine(_root, "missing", "destination");
        var transfer = Create(run: (_, arguments, _) =>
        {
            Assert.Equal(destination, arguments.Last());
            Assert.True(Directory.Exists(destination));
            return Task.FromResult(new CommandResult { ExitCode = 9, StandardError = "tar.exe missing" });
        });
        var result = await transfer.CopyFromAsync("container-id", "/source", destination, Legacy);
        Assert.Equal(9, result.ExitCode);
        Assert.Contains("tar.exe missing", result.ErrorText);
        Assert.Equal(0, _legacyCalls);
    }

    [Fact]
    public async Task UploadArchivePreservesNestedDirectoriesBinaryAndEmptyDirectories()
    {
        var source = Path.Combine(_root, "source space \u00e9");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        var bytes = new byte[4 * 1024 * 1024 + 17];
        new Random(68).NextBytes(bytes);
        await File.WriteAllBytesAsync(Path.Combine(source, "nested", "data.bin"), bytes);
        var transfer = Create(input: async (executable, arguments, archivePath, ct) =>
        {
            Assert.Equal("configured-wslc.exe", executable);
            Assert.Equal(["container", "cp", "-", "container-id:/"], arguments);
            await using var stream = File.OpenRead(archivePath);
            var destination = Path.Combine(_root, "simulated-container");
            Directory.CreateDirectory(destination);
            await TarFile.ExtractToDirectoryAsync(stream, destination, overwriteFiles: true, ct);
            var copied = Path.Combine(destination, "missing", "deep", Path.GetFileName(source), "nested");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(copied, "data.bin"), ct));
            Assert.True(Directory.Exists(Path.Combine(copied, "empty")));
            return new CommandResult();
        });
        Assert.True((await transfer.CopyToAsync("container-id", source + Path.DirectorySeparatorChar, "/missing/deep/", Legacy)).Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
        Assert.Equal(0, _legacyCalls);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"Z:\")]
    [InlineData(@"C:\unused\..")]
    [InlineData(@"\\server\share\")]
    public async Task NativeDownloadRejectsRootBeforeMutation(string destination)
    {
        var result = await Create().CopyFromAsync("container-id", "/report.bin", destination, Legacy);
        Assert.False(result.Success);
        Assert.Contains("below the drive or share root", result.ErrorText);
        Assert.Equal(0, _nativeCalls);
        Assert.Equal(0, _legacyCalls);
        Assert.False(Directory.Exists(Staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonLinkReparsePointsArchiveContentsWithoutFollowingRealLinks(bool directory)
    {
        var source = Path.Combine(_root, "cloud");
        var file = directory ? Path.Combine(source, "data.bin") : source;
        var bytes = new byte[] { 0, 255, 13, 10 };
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(source, "empty"));
        }
        await File.WriteAllBytesAsync(file, bytes);
        File.SetLastWriteTimeUtc(file, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        if (directory)
        {
            File.CreateSymbolicLink(Path.Combine(source, "file-link"), "data.bin");
            Directory.CreateSymbolicLink(Path.Combine(source, "directory-link"), _root);
        }

        await using var archive = new MemoryStream();
        await using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            // Model the attributes of hydrated cloud entries without requiring a sync provider.
            await WslcFileTransfer.WriteSourceAsync(writer, source, "cloud", CancellationToken.None,
                path => File.GetAttributes(path) | FileAttributes.ReparsePoint);
        }
        archive.Position = 0;
        using var reader = new TarReader(archive);
        var entries = new Dictionary<string, TarEntryType>();
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            entries.Add(entry.Name.TrimEnd('/'), entry.EntryType);
            if (entry.EntryType == TarEntryType.RegularFile)
            {
                using var data = new MemoryStream();
                await entry.DataStream!.CopyToAsync(data);
                Assert.Equal(bytes, data.ToArray());
                Assert.Equal(File.GetLastWriteTimeUtc(file), entry.ModificationTime.UtcDateTime);
            }
        }
        if (directory)
        {
            Assert.Equal(5, entries.Count);
            Assert.Equal(TarEntryType.Directory, entries["cloud"]);
            Assert.Equal(TarEntryType.Directory, entries["cloud/empty"]);
            Assert.Equal(TarEntryType.RegularFile, entries["cloud/data.bin"]);
            Assert.Equal(TarEntryType.SymbolicLink, entries["cloud/file-link"]);
            Assert.Equal(TarEntryType.SymbolicLink, entries["cloud/directory-link"]);
        }
        else
        {
            Assert.Single(entries);
            Assert.Equal(TarEntryType.RegularFile, entries["cloud"]);
        }
    }

    [Theory]
    [InlineData("/../outside")]
    [InlineData("/safe/../../outside")]
    [InlineData("/safe/./child")]
    [InlineData("relative")]
    public async Task UnsafeArchiveDestinationIsRejectedBeforeMutation(string destination)
    {
        var transfer = Create();
        var result = await transfer.CopyToAsync("container-id", _root, destination, Legacy);
        Assert.False(result.Success);
        Assert.Equal(0, _nativeCalls);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public async Task UploadNativeFailureCleansArchiveWithoutLegacyRetry()
    {
        var source = Path.Combine(_root, "file.bin");
        await File.WriteAllBytesAsync(source, [0, 255, 13, 10]);
        var transfer = Create(input: (_, _, _, _) =>
            Task.FromResult(new CommandResult { ExitCode = 2, StandardError = "partial native failure" }));
        var result = await transfer.CopyToAsync("container-id", source, "/target", Legacy);
        Assert.False(result.Success);
        Assert.Contains("partial native failure", result.ErrorText);
        Assert.Equal(0, _legacyCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
    }

    [Fact]
    public async Task CancellationDuringUploadPropagatesAndCleansArchive()
    {
        var source = Path.Combine(_root, "file.bin");
        await File.WriteAllBytesAsync(source, [0, 255]);
        using var cancellation = new CancellationTokenSource();
        var transfer = Create(input: (_, _, _, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new CommandResult());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transfer.CopyToAsync("container-id", source, "/target", Legacy, cancellation.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
        Assert.Equal(0, _legacyCalls);
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotProbeOrStage()
    {
        var transfer = Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transfer.CopyToAsync("container-id", _root, "/target", Legacy, new CancellationToken(true)));
        Assert.Equal(0, _nativeCalls);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public async Task MissingHostSourceReportsFailureBeforeNativeMutation()
    {
        var result = await Create().CopyToAsync("container-id", Path.Combine(_root, "missing"), "/target", Legacy);
        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorText);
        Assert.Equal(0, _nativeCalls);
    }

    [Fact]
    public async Task UploadPreservesLinksWithoutEnumeratingDirectoryTargets()
    {
        var source = Path.Combine(_root, "links");
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(external);
        await File.WriteAllBytesAsync(Path.Combine(source, "target.bin"), [255, 0]);
        await File.WriteAllBytesAsync(Path.Combine(external, "not-followed.bin"), [1]);
        File.CreateSymbolicLink(Path.Combine(source, "file-link"), "target.bin");
        Directory.CreateSymbolicLink(Path.Combine(source, "directory-link"), external);
        var entries = new Dictionary<string, TarEntryType>();
        var transfer = Create(input: async (_, _, archivePath, ct) =>
        {
            await using var archive = File.OpenRead(archivePath);
            using var reader = new TarReader(archive);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                entries.Add(entry.Name.TrimEnd('/'), entry.EntryType);
            }
            return new CommandResult();
        });
        Assert.True((await transfer.CopyToAsync("container-id", source, "/target", Legacy)).Success);
        Assert.Equal(TarEntryType.SymbolicLink, entries["target/links/file-link"]);
        Assert.Equal(TarEntryType.SymbolicLink, entries["target/links/directory-link"]);
        Assert.DoesNotContain(entries.Keys, key => key.Contains("not-followed"));
    }

    [Fact]
    public async Task CleanupWaitsForDuplicatedArchiveHandleWithoutHidingCancellation()
    {
        var source = Path.Combine(_root, "file.bin");
        await File.WriteAllBytesAsync(source, [0, 255]);
        using var cancellation = new CancellationTokenSource();
        Task release = Task.CompletedTask;
        var transfer = Create(input: (_, _, archivePath, ct) =>
        {
            var retained = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            release = Task.Run(async () =>
            {
                await Task.Delay(150);
                retained.Dispose();
            });
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new CommandResult());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transfer.CopyToAsync("container-id", source, "/target", Legacy, cancellation.Token));
        await release;
        Assert.Empty(Directory.EnumerateFileSystemEntries(Staging));
        Assert.Equal(1, _nativeCalls);
        Assert.Equal(0, _legacyCalls);
    }

    [Fact]
    public async Task DownloadDoesNotOverwriteExistingHostLink()
    {
        var target = Path.Combine(_root, "target.bin");
        await File.WriteAllBytesAsync(target, [0, 255]);
        File.CreateSymbolicLink(Path.Combine(_root, "source"), target);
        var result = await Create().CopyFromAsync("container-id", "/source", _root, Legacy);
        Assert.False(result.Success);
        Assert.Contains("symbolic link", result.ErrorText);
        Assert.Equal(0, _nativeCalls);
        Assert.Equal(new byte[] { 0, 255 }, await File.ReadAllBytesAsync(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadPreparesNonLinkCloudEntriesForOverwrite(bool directory)
    {
        var destination = Path.Combine(_root, "cloud");
        var file = directory ? Path.Combine(destination, "report.bin") : destination;
        if (directory)
        {
            Directory.CreateDirectory(destination);
        }
        await File.WriteAllBytesAsync(file, [0, 255]);
        File.SetAttributes(file, FileAttributes.ReadOnly);

        WslcFileTransfer.PrepareOverwrite(destination, CancellationToken.None,
            path => File.GetAttributes(path) | FileAttributes.ReparsePoint);

        Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(new byte[] { 0, 255 }, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public void DownloadStillRejectsRealDirectoryLinksWithinCloudDirectories()
    {
        var destination = Path.Combine(_root, "cloud");
        Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(Path.Combine(destination, "directory-link"), _root);

        var error = Assert.Throws<IOException>(() => WslcFileTransfer.PrepareOverwrite(
            destination, CancellationToken.None, path => File.GetAttributes(path) | FileAttributes.ReparsePoint));

        Assert.Contains("symbolic link", error.Message);
    }

    private Task<CommandResult> Legacy(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _legacyCalls++;
        return Task.FromResult(new CommandResult());
    }

    private WslcFileTransfer Create(
        WslcCapabilitySupport support = WslcCapabilitySupport.Supported,
        Func<string, IEnumerable<string>, CancellationToken, Task<CommandResult>>? run = null,
        Func<string, IEnumerable<string>, string, CancellationToken, Task<CommandResult>>? input = null) =>
        new(new Capabilities(support),
            (executable, arguments, ct) =>
            {
                _nativeCalls++;
                return run?.Invoke(executable, arguments, ct) ?? Task.FromResult(new CommandResult());
            },
            (executable, arguments, stream, ct) =>
            {
                _nativeCalls++;
                return input?.Invoke(executable, arguments, stream, ct) ?? Task.FromResult(new CommandResult());
            },
            () => Staging);

    private sealed class Capabilities(WslcCapabilitySupport support) : IWslcCapabilitiesService
    {
        public Task<WslcCapabilities> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WslcCapabilities("configured-wslc.exe", "test",
                new Dictionary<WslcFeature, WslcCapability>
                {
                    [WslcFeature.ContainerCp] = new(support, "probe timed out"),
                }));
        public void Invalidate() { }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }
}
