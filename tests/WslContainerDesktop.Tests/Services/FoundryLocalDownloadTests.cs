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

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Synthetic archive bytes test real streaming, hashing and exact-entry extraction; no package executes.</summary>
public sealed class FoundryLocalDownloadTests
{
    [Fact]
    public void ProductionDownloadHandlerDoesNotDelegateRedirectsOrSendAmbientCredentials()
    {
        using var handler = FoundryLocalDownloader.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void ProductionManifestPinsSeparateRegistrationAssetsNotInitialModelReadiness()
    {
        var package = new FoundryLocalArtifactCatalog().GetStandalone();
        Assert.NotNull(package);
        Assert.True(package.IsDownloadable(DateTimeOffset.UtcNow));
        Assert.Equal(29982055, package.Runtime.Bytes);
        Assert.Equal(50182148, package.VcLibsArchive!.Bytes);
        Assert.Equal(6757465, package.VcLibs.Bytes);
        Assert.Equal("077A3D1A5D0622BD3004DCA85F5E192D6E98EC79B83D4AA06766759EA6C09C3D", package.VcLibs.Sha256);
        Assert.Contains("Model/EP acquisition is not approved", package.Confirmation("unchanged-model"));
    }

    [Fact]
    public async Task DownloadsOnlyApprovedAssetsAndExtractsOnlyExactVerifiedPrerequisite()
    {
        using var fixture = new FoundryDownloadFixture();
        var progress = new InlineProgress();
        var staged = await fixture.Downloader.StageAsync(fixture.Package, true, progress, default);
        Assert.Equal(fixture.RuntimeBytes, await File.ReadAllBytesAsync(staged.RuntimePath));
        Assert.Equal(fixture.PrerequisiteBytes, await File.ReadAllBytesAsync(staged.PrerequisitePath!));
        Assert.Equal([fixture.Package.RuntimeDownloadUri!, fixture.Package.VcLibsArchive!.DownloadUri], fixture.Http.Requests);
        Assert.Equal(3, Directory.GetFiles(fixture.CacheRoot).Length);
        Assert.DoesNotContain(Directory.GetFiles(fixture.CacheRoot), path => Path.GetFileName(path).Contains("unrelated", StringComparison.Ordinal));
        Assert.All(progress.Messages.Where(message => message.Contains('%')), message =>
        {
            var percent = int.Parse(message.Split(':')[1].Trim().Split('%')[0]);
            Assert.InRange(percent, 0, 99);
        });
        Assert.InRange(progress.Messages.Count, 1, 306);
        Assert.Empty(fixture.ProcessCalls);
    }

    [Fact]
    public async Task VerifiedCacheIsRehashedAndReusedOfflineWithoutAnyRequests()
    {
        using var fixture = new FoundryDownloadFixture();
        var first = await fixture.Downloader.StageAsync(fixture.Package, true, null, default);
        fixture.Http.Requests.Clear();
        fixture.Http.Respond = _ => throw new Xunit.Sdk.XunitException("Offline cache must not access HTTP");
        var second = await fixture.Downloader.StageAsync(fixture.Package, true, null, default);
        Assert.Equal(first, second);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task ChangedCachedBytesFailClosedWithoutNetworkOrInstallation()
    {
        using var fixture = new FoundryDownloadFixture();
        var first = await fixture.Downloader.StageAsync(fixture.Package, true, null, default);
        var changed = fixture.RuntimeBytes.ToArray();
        changed[0] ^= 1;
        await File.WriteAllBytesAsync(first.RuntimePath, changed);
        fixture.Http.Requests.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.StageAsync(fixture.Package, true, null, default));
        Assert.Empty(fixture.Http.Requests);
        Assert.Empty(fixture.ProcessCalls);
        Assert.Equal(changed, await File.ReadAllBytesAsync(first.RuntimePath));
    }

    [Fact]
    public async Task CorruptArchiveIsNeverOpenedOrExtractedAndPartialIsTruthfullyRetained()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = uri =>
        {
            var bytes = uri == fixture.Package.RuntimeDownloadUri ? fixture.RuntimeBytes : new byte[fixture.ArchiveBytes.Length];
            return FoundryDownloadFixture.Bytes(bytes);
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.StageAsync(fixture.Package, true, null, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.appx"));
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.partial"));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.zip"));
    }

    [Theory]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("missing-entry")]
    [InlineData("duplicate-entry")]
    public async Task NestedEntryMustMatchExactPathSizeAndHash(string invalid)
    {
        using var fixture = new FoundryDownloadFixture();
        var bytes = fixture.BuildArchive(invalid);
        var archive = fixture.Package.VcLibsArchive! with
        {
            Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        };
        var package = fixture.Package with { VcLibsArchive = archive };
        fixture.Http.Respond = uri => FoundryDownloadFixture.Bytes(uri == package.RuntimeDownloadUri ? fixture.RuntimeBytes : bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.StageAsync(package, true, null, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.appx"));
        Assert.Empty(fixture.ProcessCalls);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://untrusted.invalid/file")]
    [InlineData("https://github.com.evil.invalid/file")]
    [InlineData("https://user:password@github.com/file")]
    [InlineData("https://github.com:444/file")]
    public async Task RedirectEscapeIsRejectedBeforeSecondRequest(string redirect)
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new(redirect) } };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.StageAsync(fixture.Package, false, null, default));
        Assert.Single(fixture.Http.Requests);
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot));
    }

    [Fact]
    public async Task MicrosoftGithubAssetRedirectWorksWithoutForwardingCredentialsOrCookies()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = uri => uri.Host == "github.com"
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new("https://release-assets.githubusercontent.com/synthetic?signature=opaque") } }
            : FoundryDownloadFixture.Bytes(fixture.RuntimeBytes);
        var staged = await fixture.Downloader.StageAsync(fixture.Package, false, null, default);
        Assert.Equal(fixture.RuntimeBytes, await File.ReadAllBytesAsync(staged.RuntimePath));
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.Null(staged.PrerequisitePath);
    }

    [Fact]
    public async Task RedirectLoopIsBoundedAndNotRetried()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = _ => new(HttpStatusCode.Redirect) { Headers = { Location = fixture.Package.RuntimeDownloadUri } };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.StageAsync(fixture.Package, false, null, default));
        Assert.Equal(4, fixture.Http.Requests.Count);
    }

    [Fact]
    public async Task HttpFailureIsNotRetriedOrPromotedToVerifiedCache()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = _ => new(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Downloader.StageAsync(fixture.Package, false, null, default));
        Assert.Single(fixture.Http.Requests);
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot));
    }

    [Fact]
    public async Task OversizedStreamingBodyIsBoundedWithoutTrustingContentLength()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedStream(new byte[fixture.RuntimeBytes.Length + 1])),
            };
            Assert.Null(response.Content.Headers.ContentLength);
            return response;
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Downloader.StageAsync(fixture.Package, false, null, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.msix"));
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.partial"));
    }

    [Fact]
    public async Task CancellationRetainsUnverifiedPartialButNeverPublishesOrInstallsIt()
    {
        using var fixture = new FoundryDownloadFixture();
        using var cts = new CancellationTokenSource();
        fixture.Http.Respond = _ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelStream(fixture.RuntimeBytes, cts)),
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Downloader.StageAsync(fixture.Package, false, null, cts.Token));
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.partial"));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.msix"));
        Assert.Empty(fixture.ProcessCalls);
    }

    private sealed class CancelStream(byte[] bytes, CancellationTokenSource cts) : MemoryStream(bytes)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (++_reads == 2) cts.Cancel();
            return base.ReadAsync(buffer[..Math.Min(128, buffer.Length)], ct);
        }

    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class InlineProgress : IProgress<string>
    {
        internal readonly List<string> Messages = [];
        public void Report(string value)
        {
            // Final verification announcements have no unverified-progress percentage.
            if (!value.Contains("verified (100%)", StringComparison.Ordinal)) Messages.Add(value);
        }
    }
}

internal sealed class FoundryDownloadFixture : IDisposable
{
    internal string CacheRoot { get; } = Path.Combine(AppContext.BaseDirectory, "FoundryDownloadFixtures-" + Guid.NewGuid().ToString("N"));
    internal byte[] RuntimeBytes { get; } = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("synthetic-runtime-not-executable", 6000)));
    internal byte[] PrerequisiteBytes { get; } = Encoding.UTF8.GetBytes("synthetic prerequisite, not a real APPX");
    internal byte[] ArchiveBytes { get; }
    internal FoundryLocalAuditedPackageSet Package { get; }
    internal FoundryLocalArtifactCatalog Catalog { get; }
    internal FoundryLocalDownloader Downloader { get; }
    internal FoundryLocalInstaller Installer { get; }
    internal FoundryLocalSetupService Setup { get; }
    internal FakeHttp Http { get; } = new();
    internal List<ProcessStartInfo> ProcessCalls { get; } = [];
    internal string PreflightOutput = "@@WSLCD_FOUNDRY_PREFLIGHT=ABSENT\n@@WSLCD_VCLIBS=REQUIRED";
    internal Func<ProcessStartInfo, CancellationToken, Task<CommandResult>>? BeforeProcess;
    internal int Invalidations;
    internal const string Entry = "x64/Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx";

    internal FoundryDownloadFixture()
    {
        ArchiveBytes = BuildArchive("");
        Package = new(FoundryLocalArtifactCatalog.StandalonePackageSetId,
            Artifact("0.10.3.0", RuntimeBytes), Artifact("14.0.33728.0", PrerequisiteBytes),
            new("https://synthetic.invalid/test-audit-not-production-evidence"),
            new("https://github.com/microsoft/synthetic/releases/download/test/runtime.msix"),
            new(new("https://github.com/microsoft/synthetic/releases/download/test/dependencies.zip"),
                ArchiveBytes.Length, Convert.ToHexString(SHA256.HashData(ArchiveBytes)),
                new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new("https://synthetic.invalid/test-publication"), Entry));
        Catalog = new(Package);
        Http.Respond = uri => Bytes(uri == Package.RuntimeDownloadUri ? RuntimeBytes : ArchiveBytes);
        Downloader = new(CacheRoot, Http);
        Installer = new(Catalog, async (start, ct) =>
        {
            ProcessCalls.Add(start);
            if (BeforeProcess is not null) return await BeforeProcess(start, ct);
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[3]));
            Assert.True(script is FoundryLocalInstaller.PreflightScript or FoundryLocalInstaller.InstallScript);
            return new()
            {
                StandardOutput = script == FoundryLocalInstaller.PreflightScript ? PreflightOutput : "@@WSLCD_FOUNDRY_INSTALLED",
            };
        }, () => Invalidations++);
        var runtime = NetworkTestProxy.Create<IFoundryLocalRuntimeService>((_, _) => throw new Xunit.Sdk.XunitException("No REST or model operations in runtime setup"));
        var cli = new FoundryLocalCli(() => throw new Xunit.Sdk.XunitException("No CLI execution in runtime setup"),
            (_, _) => throw new Xunit.Sdk.XunitException("No CLI execution"));
        Setup = new(cli, runtime, Catalog, Downloader, Installer);
    }

    internal byte[] BuildArchive(string invalid)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var path = invalid == "missing-entry" ? "arm64/other.appx" : Entry;
            var bytes = invalid == "size" ? new byte[1] : PrerequisiteBytes.ToArray();
            if (invalid == "hash") bytes[0] ^= 1;
            using (var target = zip.CreateEntry(path).Open()) target.Write(bytes);
            if (invalid == "duplicate-entry")
                using (var target = zip.CreateEntry(path).Open()) target.Write(bytes);
            using var unrelated = zip.CreateEntry("../../unrelated-should-never-extract.exe").Open();
            unrelated.Write(Encoding.UTF8.GetBytes("unrelated archive entry, not executable"));
        }
        return memory.ToArray();
    }

    private static FoundryLocalAuditedArtifact Artifact(string version, byte[] bytes) =>
        new(version, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new("https://synthetic.invalid/test-publication"), "synthetic-license",
            new("https://synthetic.invalid/test-license"));
    internal static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    public void Dispose()
    {
        Downloader.Dispose();
        if (Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, recursive: true);
    }

    internal sealed class FakeHttp : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal Func<Uri, HttpResponseMessage> Respond = _ => throw new Xunit.Sdk.XunitException("No handler configured");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("Proxy-Authorization"));
            return Task.FromResult(Respond(request.RequestUri!));
        }
    }
}
