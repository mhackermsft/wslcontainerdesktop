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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ContainerDownloadArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wcd-archive-tests-{Guid.NewGuid():N}");

    public ContainerDownloadArchiveTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(@"source/..\sibling.txt")]
    [InlineData(@"source/sub\..\..\sibling.txt")]
    [InlineData("source/../sibling.txt")]
    [InlineData("sibling.txt")]
    [InlineData("source-other/file.bin")]
    [InlineData("/absolute/file.bin")]
    [InlineData(@"source/C:\outside.bin")]
    [InlineData("source/file.bin:stream")]
    [InlineData("source/CON.txt")]
    [InlineData("source/sub./file.bin")]
    [InlineData("source/sub /file.bin")]
    public async Task RejectsUnsafeMembersBeforeExtractingAnyEntry(string member)
    {
        var sentinel = Path.Combine(_root, "sibling.txt");
        await File.WriteAllBytesAsync(sentinel, [1, 2, 3]);
        using var archive = Archive(writer =>
        {
            WriteFile(writer, "source/safe.bin", [4, 5, 6]);
            WriteFile(writer, member, [9, 9, 9]);
        });
        using (var reader = new TarReader(archive, leaveOpen: true))
        {
            reader.GetNextEntry();
            Assert.Equal(member, reader.GetNextEntry()!.Name);
        }
        archive.Position = 0;

        var error = await Record.ExceptionAsync(() =>
            ContainerDownloadArchive.ExtractAsync(archive, ContainerDownloadPath.Create("/source", _root), CancellationToken.None));

        Assert.True(error is ArgumentException or InvalidDataException);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(sentinel));
        Assert.False(Directory.Exists(Path.Combine(_root, "source")));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink, "../../sibling.txt")]
    [InlineData(TarEntryType.SymbolicLink, @"..\..\sibling.txt")]
    [InlineData(TarEntryType.SymbolicLink, "/sibling.txt")]
    [InlineData(TarEntryType.SymbolicLink, "../NUL")]
    [InlineData(TarEntryType.HardLink, "sibling.txt")]
    [InlineData(TarEntryType.HardLink, "source/../sibling.txt")]
    [InlineData(TarEntryType.HardLink, @"source\report.bin")]
    public async Task RejectsUnsafeLinkTargetsBeforeExtractingAnyEntry(TarEntryType type, string link)
    {
        using var archive = Archive(writer =>
        {
            WriteFile(writer, "source/safe.bin", [1]);
            writer.WriteEntry(new PaxTarEntry(type, "source/sub/link.bin") { LinkName = link });
        });

        var error = await Record.ExceptionAsync(() =>
            ContainerDownloadArchive.ExtractAsync(archive, ContainerDownloadPath.Create("/source", _root), CancellationToken.None));

        Assert.True(error is ArgumentException or InvalidDataException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task ExtractsFilesEmptyDirectoriesAndPreservesContainedLinks()
    {
        using var archive = Archive(writer =>
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "source/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "source/sub/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "source/empty/"));
            WriteFile(writer, "source/report.bin", [0, 255, 13, 10]);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "source/sub/link.bin") { LinkName = "../report.bin" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "source/hard.bin") { LinkName = "source/report.bin" });
        });

        await ContainerDownloadArchive.ExtractAsync(archive, ContainerDownloadPath.Create("/source", _root), CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, "source", "empty")));
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, await File.ReadAllBytesAsync(Path.Combine(_root, "source", "report.bin")));
        Assert.Equal("../report.bin", new FileInfo(Path.Combine(_root, "source", "sub", "link.bin")).LinkTarget);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, await File.ReadAllBytesAsync(Path.Combine(_root, "source", "hard.bin")));
    }

    [Fact]
    public async Task RootDownloadAcceptsStandardDotPrefixedArchive()
    {
        using var archive = Archive(writer =>
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./alpha/"));
            WriteFile(writer, "./alpha/report.bin", [0, 255]);
        });

        await ContainerDownloadArchive.ExtractAsync(archive, ContainerDownloadPath.Create("/", _root), CancellationToken.None);

        Assert.Equal(new byte[] { 0, 255 }, await File.ReadAllBytesAsync(Path.Combine(_root, "alpha", "report.bin")));
    }

    [Fact]
    public async Task RejectsExistingDestinationLinksAndCancellation()
    {
        var target = Path.Combine(_root, "target.bin");
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_root, "source"));
        File.CreateSymbolicLink(Path.Combine(_root, "source", "report.bin"), target);
        using var archive = Archive(writer => WriteFile(writer, "source/report.bin", [9]));

        await Assert.ThrowsAsync<IOException>(() =>
            ContainerDownloadArchive.ExtractAsync(archive, ContainerDownloadPath.Create("/source", _root), CancellationToken.None));
        archive.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContainerDownloadArchive.ExtractAsync(archive, ContainerDownloadPath.Create("/source", _root), new CancellationToken(true)));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
    }

    private static MemoryStream Archive(Action<TarWriter> write)
    {
        var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true)) write(writer);
        archive.Position = 0;
        return archive;
    }

    private static void WriteFile(TarWriter writer, string name, byte[] bytes)
    {
        using var contents = new MemoryStream(bytes);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = contents });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
