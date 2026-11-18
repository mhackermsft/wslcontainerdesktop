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
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Covers Copilot CLI discovery, explicit selection, version parsing and the verified runtime install.</summary>
public sealed class CopilotCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wcd-copilot-cli-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static CopilotCliProbeEnvironment Env(
        IEnumerable<string> files,
        Dictionary<string, string?>? variables = null,
        Dictionary<string, string[]>? directories = null,
        Dictionary<string, string>? managedVersions = null)
    {
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        variables ??= new();
        directories ??= new(StringComparer.OrdinalIgnoreCase);
        return new CopilotCliProbeEnvironment(
            name => variables.TryGetValue(name, out var value) ? value : null,
            @"C:\Users\u\AppData\Local",
            @"C:\Users\u\AppData\Roaming",
            @"C:\Cache\copilot-cli",
            existing.Contains,
            dir => directories.TryGetValue(dir, out var children) ? children : [],
            dir => managedVersions is not null && managedVersions.TryGetValue(dir, out var version) ? version : null);
    }

    [Fact]
    public void CandidatesFollowAutomaticOrderAndSkipMissingAndDuplicates()
    {
        var platform = CopilotCliInstaller.PlatformSuffix!;
        var managedOld = @"C:\Cache\copilot-cli\1.0.100";
        var managedNew = @"C:\Cache\copilot-cli\1.0.120";
        var env = Env(
            files:
            [
                @"D:\Pinned\copilot.exe",
                CopilotCliInstaller.RuntimeExecutablePath(managedOld, platform),
                CopilotCliInstaller.RuntimeExecutablePath(managedNew, platform),
                @"C:\Users\u\AppData\Local\GitHubCopilotCLI\copilot.exe",
                @"C:\Users\u\AppData\Roaming\npm\copilot.cmd",
                @"C:\VSCode\bin\copilot-shim\copilot.exe",
            ],
            variables: new()
            {
                [CopilotCliLocator.EnvironmentVariable] = "\"D:\\Pinned\\copilot.exe\"",
                ["PATH"] = @"C:\VSCode\bin\copilot-shim;""C:\Users\u\AppData\Local\GitHubCopilotCLI"";C:\Missing",
            },
            directories: new(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Cache\copilot-cli"] = [managedOld, managedNew, @"C:\Cache\copilot-cli\1.0.130", @"C:\Cache\copilot-cli\.install-1.0.140-x"],
            },
            // 1.0.130 is unverified (damaged), so it is not offered.
            managedVersions: new(StringComparer.OrdinalIgnoreCase) { [managedOld] = "1.0.100", [managedNew] = "1.0.120" });

        var candidates = CopilotCliLocator.FindCandidates(env);

        Assert.Equal(
        [
            (@"D:\Pinned\copilot.exe", CopilotCliSource.Environment),
            (CopilotCliInstaller.RuntimeExecutablePath(managedNew, platform), CopilotCliSource.AppManaged),
            (CopilotCliInstaller.RuntimeExecutablePath(managedOld, platform), CopilotCliSource.AppManaged),
            (@"C:\Users\u\AppData\Local\GitHubCopilotCLI\copilot.exe", CopilotCliSource.WinGetInstall),
            (@"C:\Users\u\AppData\Roaming\npm\copilot.cmd", CopilotCliSource.Npm),
            (@"C:\VSCode\bin\copilot-shim\copilot.exe", CopilotCliSource.Path),
        ], candidates.Select(c => (c.Path, c.Source)));
    }

    [Fact]
    public void ExplicitSelectionWinsAndNeverFallsBackWhenMissing()
    {
        var env = Env([@"C:\Users\u\AppData\Local\GitHubCopilotCLI\copilot.exe", @"E:\Tools\copilot.exe"]);

        Assert.Equal(@"C:\Users\u\AppData\Local\GitHubCopilotCLI\copilot.exe", CopilotCliLocator.Resolve("", env));
        Assert.Equal(@"C:\Users\u\AppData\Local\GitHubCopilotCLI\copilot.exe", CopilotCliLocator.Resolve("  ", env));
        Assert.Equal(@"E:\Tools\copilot.exe", CopilotCliLocator.Resolve(" \"E:\\Tools\\copilot.exe\" ", env));
        Assert.Null(CopilotCliLocator.Resolve(@"E:\Gone\copilot.exe", env));
        Assert.Null(CopilotCliLocator.Resolve(null, Env([])));
    }

    [Theory]
    [InlineData("GitHub Copilot CLI 1.0.114.\nRun 'copilot update' to check for updates.", "1.0.114")]
    [InlineData("copilot 0.0.423", "0.0.423")]
    [InlineData("v1.2.3-preview.1 (build 99)", "1.2.3-preview.1")]
    [InlineData("no version here", null)]
    public void VersionOutputIsParsed(string output, string? expected) =>
        Assert.Equal(expected, CopilotCliLocator.ParseVersionOutput(output));

    [Fact]
    public void ChecksumMustBeListedExactlyOnce()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        Assert.Equal(hash.ToUpperInvariant(), CopilotCliInstaller.FindChecksum($"{other}  other.tgz\r\n{hash} *asset.tgz\r\n", "asset.tgz"));
        Assert.Null(CopilotCliInstaller.FindChecksum($"{hash}  asset.tgz\n{other}  asset.tgz\n", "asset.tgz"));
        Assert.Null(CopilotCliInstaller.FindChecksum($"{hash}  asset.tgz.sig\n", "asset.tgz"));
    }

    [Fact]
    public async Task ExtractionStripsTheTopFolderAndRejectsEscapesAndLinks()
    {
        Directory.CreateDirectory(_root);
        var good = Path.Combine(_root, "good.tgz");
        WriteArchive(good, ("package/", null), ("package/prebuilds/win32-x64/copilot-runtime.exe", "exe"), ("package/README.md", "readme"));
        var target = Path.Combine(_root, "out");
        await CopilotCliInstaller.ExtractAsync(good, target, default);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(target, "prebuilds", "win32-x64", "copilot-runtime.exe")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(target, "README.md")));
        Assert.False(Directory.Exists(Path.Combine(target, "package")));

        var escape = Path.Combine(_root, "escape.tgz");
        WriteArchive(escape, ("package/../../evil.txt", "x"));
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotCliInstaller.ExtractAsync(escape, Path.Combine(_root, "out2"), default));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));

        var link = Path.Combine(_root, "link.tgz");
        using (var file = File.Create(link))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "package/runtime.node") { LinkName = "C:/Windows/System32/kernel32.dll" });
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotCliInstaller.ExtractAsync(link, Path.Combine(_root, "out3"), default));
    }

    [Fact]
    public async Task InstallVerifiesChecksumThenReusesTheCompletedInstallOffline()
    {
        const string version = "9.8.7";
        const string platform = "win32-x64";
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "bundle.tgz");
        WriteArchive(archive,
            ("package/package.json", "{\"name\":\"@github/copilot\",\"version\":\"9.8.7\"}"),
            ("package/prebuilds/win32-x64/copilot-runtime.exe", "runtime"),
            ("package/prebuilds/win32-x64/runtime.node", "node"));
        var bytes = File.ReadAllBytes(archive);
        var asset = CopilotCliInstaller.AssetName(version, platform);
        var handler = new FakeReleaseHandler(new()
        {
            [$"/v{version}/SHA256SUMS.txt"] = System.Text.Encoding.ASCII.GetBytes($"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}  {asset}\n"),
            [$"/v{version}/{asset}"] = bytes,
        });
        using var http = new HttpClient(handler);
        var installer = new CopilotCliInstaller(NullLogger<CopilotCliInstaller>.Instance);
        var root = Path.Combine(_root, "managed");
        var stages = new List<CopilotCliInstallStage>();

        var path = await installer.InstallAsync(version, platform, root, "https://releases.invalid/", http,
            new SynchronousProgress(p => stages.Add(p.Stage)), default);

        Assert.Equal(CopilotCliInstaller.RuntimeExecutablePath(Path.Combine(root, version), platform), path);
        Assert.Equal("runtime", File.ReadAllText(path));
        Assert.Equal(version, File.ReadAllText(Path.Combine(root, version, CopilotCliLocator.CompleteMarker)));
        Assert.Equal([Path.Combine(root, version)], Directory.GetDirectories(root));
        Assert.Contains(CopilotCliInstallStage.Verifying, stages);
        Assert.Equal(CopilotCliInstallStage.Completed, stages[^1]);
        Assert.Equal(version, CopilotCliInstaller.TryReadInstalledVersion(path));
        Assert.Equal([Path.Combine(root, version)], CopilotCliInstaller.ListInstalled(root));

        var requests = handler.Requests;
        Assert.Equal(path, await installer.InstallAsync(version, platform, root, "https://releases.invalid/", http, null, default));
        Assert.Equal(requests, handler.Requests);
    }

    [Fact]
    public async Task ChecksumMismatchInstallsNothing()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "bundle.tgz");
        WriteArchive(archive, ("package/prebuilds/win32-x64/copilot-runtime.exe", "runtime"), ("package/prebuilds/win32-x64/runtime.node", "node"));
        var asset = CopilotCliInstaller.AssetName("1.0.0", "win32-x64");
        using var http = new HttpClient(new FakeReleaseHandler(new()
        {
            ["/v1.0.0/SHA256SUMS.txt"] = System.Text.Encoding.ASCII.GetBytes($"{new string('0', 64)}  {asset}\n"),
            [$"/v1.0.0/{asset}"] = File.ReadAllBytes(archive),
        }));
        var root = Path.Combine(_root, "managed");

        await Assert.ThrowsAsync<InvalidDataException>(() => new CopilotCliInstaller(NullLogger<CopilotCliInstaller>.Instance)
            .InstallAsync("1.0.0", "win32-x64", root, "https://releases.invalid", http, null, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task ArchiveReportingAnotherVersionIsNotInstalled()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "bundle.tgz");
        WriteArchive(archive,
            ("package/package.json", "{\"version\":\"1.0.99\"}"),
            ("package/prebuilds/win32-x64/copilot-runtime.exe", "runtime"),
            ("package/prebuilds/win32-x64/runtime.node", "node"));
        var bytes = File.ReadAllBytes(archive);
        var asset = CopilotCliInstaller.AssetName("1.0.0", "win32-x64");
        using var http = new HttpClient(new FakeReleaseHandler(new()
        {
            ["/v1.0.0/SHA256SUMS.txt"] = System.Text.Encoding.ASCII.GetBytes($"{Convert.ToHexString(SHA256.HashData(bytes))}  {asset}\n"),
            [$"/v1.0.0/{asset}"] = bytes,
        }));
        var root = Path.Combine(_root, "managed");

        await Assert.ThrowsAsync<InvalidDataException>(() => new CopilotCliInstaller(NullLogger<CopilotCliInstaller>.Instance)
            .InstallAsync("1.0.0", "win32-x64", root, "https://releases.invalid", http, null, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void VerifiedVersionRequiresMarkerManifestAndFolderToAgree()
    {
        var platform = "win32-x64";
        string Make(string folder, string? marker, string? manifest)
        {
            var dir = Path.Combine(_root, "v", folder);
            Directory.CreateDirectory(Path.Combine(dir, "prebuilds", platform));
            File.WriteAllText(CopilotCliInstaller.RuntimeExecutablePath(dir, platform), "x");
            if (marker is not null) File.WriteAllText(Path.Combine(dir, CopilotCliLocator.CompleteMarker), marker);
            if (manifest is not null) File.WriteAllText(Path.Combine(dir, "package.json"), manifest);
            return dir;
        }

        var good = Make("1.0.114", "1.0.114", "{\"version\":\"1.0.114\"}");
        Assert.Equal("1.0.114", CopilotCliInstaller.ReadVerifiedVersion(good));
        Assert.Equal("1.0.114", CopilotCliInstaller.TryReadInstalledVersion(CopilotCliInstaller.RuntimeExecutablePath(good, platform)));
        Assert.Null(CopilotCliInstaller.ReadVerifiedVersion(Make("1.0.1", null, "{\"version\":\"1.0.1\"}")));
        Assert.Null(CopilotCliInstaller.ReadVerifiedVersion(Make("1.0.2", "1.0.2", null)));
        Assert.Null(CopilotCliInstaller.ReadVerifiedVersion(Make("1.0.3", "1.0.3", "{\"version\":\"1.0.4\"}")));
        Assert.Null(CopilotCliInstaller.ReadVerifiedVersion(Make("1.0.5", "1.0.6", "{\"version\":\"1.0.6\"}")));
        Assert.Null(CopilotCliInstaller.ReadVerifiedVersion(Make("1.0.7", "1.0.7", "not json")));
        Assert.Null(CopilotCliInstaller.TryReadInstalledVersion(Path.Combine(good, "copilot-runtime.exe")));
        Assert.Equal([good], CopilotCliInstaller.ListInstalled(Path.Combine(_root, "v")));

        new CopilotCliInstaller(NullLogger<CopilotCliInstaller>.Instance).UninstallAll(Path.Combine(_root, "v"));
        Assert.False(Directory.Exists(Path.Combine(_root, "v")));
        Assert.Empty(CopilotCliInstaller.ListInstalled(Path.Combine(_root, "v")));
    }

    [Fact]
    public void ProtocolMismatchIsRecognisedThroughWrappers()
    {
        var mismatch = new InvalidOperationException("SDK protocol version mismatch: SDK supports versions 4-4, but server reports version 3.");
        Assert.True(GitHubCopilotProvider.IsProtocolMismatch(mismatch));
        Assert.True(GitHubCopilotProvider.IsProtocolMismatch(new IOException("start failed", mismatch)));
        Assert.False(GitHubCopilotProvider.IsProtocolMismatch(new InvalidOperationException("not signed in")));
        Assert.False(GitHubCopilotProvider.IsProtocolMismatch(null));
    }

    [Theory]
    [InlineData(new string[0], "auto", WslContainerDesktop.Models.AiModelState.Available)]
    [InlineData(new string[0], "", WslContainerDesktop.Models.AiModelState.Available)]
    [InlineData(new[] { "gpt-5" }, "AUTO", WslContainerDesktop.Models.AiModelState.Available)]
    [InlineData(new string[0], "gpt-5", WslContainerDesktop.Models.AiModelState.Unknown)]
    [InlineData(new[] { "gpt-5", "claude-sonnet" }, "GPT-5", WslContainerDesktop.Models.AiModelState.Available)]
    [InlineData(new[] { "gpt-5" }, "retired-model", WslContainerDesktop.Models.AiModelState.Missing)]
    public void AutoIsAlwaysAcceptedAndAnEmptyModelListCannotRejectAModel(string[] listed, string configured, WslContainerDesktop.Models.AiModelState expected) =>
        Assert.Equal(expected, GitHubCopilotProvider.ClassifyModel(listed, configured));

    private static void WriteArchive(string path, params (string Name, string? Content)[] entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        foreach (var (name, content) in entries)
        {
            if (content is null)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
                continue;
            }
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            });
        }
    }

    private sealed class SynchronousProgress(Action<CopilotCliInstallProgress> report) : IProgress<CopilotCliInstallProgress>
    {
        public void Report(CopilotCliInstallProgress value) => report(value);
    }

    private sealed class FakeReleaseHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(responses.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
