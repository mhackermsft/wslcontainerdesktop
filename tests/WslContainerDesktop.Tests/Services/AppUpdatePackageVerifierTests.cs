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

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class AppUpdatePackageVerifierTests : IDisposable
{
    private const string Name = "393193CD-4A5B-4502-BC94-7C6AF142CD28";
    private const string Publisher = "CN=Michael Hacker";
    private const string Thumb = "AB12";

    private static readonly MsixIdentity Installed = new(Name, Publisher, new Version(1, 8, 0, 0), "x64");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wslcd-update-tests-" + Guid.NewGuid().ToString("N"));

    public AppUpdatePackageVerifierTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the per-test temp folder.
        }
    }

    private static AppUpdateRelease Release(byte[]? payload = null, string? sha256 = null, long? size = null) => new()
    {
        Version = new Version(1, 9, 0, 0),
        Tag = "v1.9.0",
        ReleasePage = new Uri("https://github.com/mhackermsft/wslcontainerdesktop/releases/tag/v1.9.0"),
        AssetName = "WSLContainerDesktop_1.9.0_x64.msix",
        AssetDownloadUrl = new Uri("https://github.com/mhackermsft/wslcontainerdesktop/releases/download/v1.9.0/WSLContainerDesktop_1.9.0_x64.msix"),
        AssetSize = size ?? payload?.Length ?? 10,
        AssetSha256 = sha256,
    };

    private static MsixIdentity Candidate(string name = Name, string publisher = Publisher, string version = "1.9.0.0", string arch = "x64") =>
        new(name, publisher, Version.Parse(version), arch);

    [Fact]
    public void AcceptsANewerBuildFromTheSameSigner() =>
        Assert.Null(AppUpdatePackageVerifier.Verify(Release(), Installed, Candidate(), Thumb, "ab12"));

    [Theory]
    [InlineData("Other.Package", Publisher)]
    [InlineData(Name, "CN=Someone Else")]
    [InlineData(Name, "CN=michael hacker")]
    public void RejectsADifferentPackage(string name, string publisher) =>
        Assert.NotNull(AppUpdatePackageVerifier.Verify(Release(), Installed, Candidate(name, publisher), Thumb, Thumb));

    [Fact]
    public void RejectsADifferentArchitecture() =>
        Assert.Contains("arm64", AppUpdatePackageVerifier.Verify(Release(), Installed, Candidate(arch: "arm64"), Thumb, Thumb));

    [Fact]
    public void RejectsAVersionOtherThanTheReleaseAdvertises() =>
        Assert.NotNull(AppUpdatePackageVerifier.Verify(Release(), Installed, Candidate(version: "1.9.1.0"), Thumb, Thumb));

    [Fact]
    public void RejectsADowngradeOrReinstall()
    {
        var installed = Installed with { Version = new Version(1, 9, 0, 0) };
        Assert.NotNull(AppUpdatePackageVerifier.Verify(Release(), installed, Candidate(), Thumb, Thumb));
    }

    [Theory]
    [InlineData(null, Thumb)]
    [InlineData(Thumb, null)]
    [InlineData(Thumb, "CD34")]
    public void RequiresTheSameValidSigner(string? installedThumb, string? candidateThumb) =>
        Assert.NotNull(AppUpdatePackageVerifier.Verify(Release(), Installed, Candidate(), installedThumb, candidateThumb));

    [Fact]
    public void TellsTheUserHowToRecoverFromACertificateChange() =>
        Assert.Contains("GitHub release page", AppUpdatePackageVerifier.Verify(Release(), Installed, Candidate(), Thumb, "CD34"));

    [Fact]
    public async Task DownloadsAndChecksTheDigest()
    {
        var payload = RandomNumberGenerator.GetBytes(300_000);
        var release = Release(payload, Convert.ToHexStringLower(SHA256.HashData(payload)));
        var path = Path.Combine(_folder, release.AssetName);
        var reports = new List<double>();

        using var http = new HttpClient(new StubHandler(payload, sendLength: true));
        await AppUpdatePackageVerifier.DownloadAsync(http, release, path, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, reports[^1]);
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task DownloadsWithoutADigestWhenGitHubReportsNone()
    {
        var payload = RandomNumberGenerator.GetBytes(1000);
        var release = Release(payload);
        var path = Path.Combine(_folder, release.AssetName);

        using var http = new HttpClient(new StubHandler(payload, sendLength: false));
        await AppUpdatePackageVerifier.DownloadAsync(http, release, path, null, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RefusesADownloadWithTheWrongDigest()
    {
        var payload = RandomNumberGenerator.GetBytes(1000);
        var release = Release(payload, new string('0', 64));
        var path = Path.Combine(_folder, release.AssetName);

        using var http = new HttpClient(new StubHandler(payload, sendLength: true));
        var ex = await Assert.ThrowsAsync<AppUpdateException>(() =>
            AppUpdatePackageVerifier.DownloadAsync(http, release, path, null, CancellationToken.None));

        Assert.Contains("checksum", ex.Message);
        Assert.Empty(Directory.EnumerateFiles(_folder));
    }

    [Theory]
    [InlineData(true, 999)]
    [InlineData(true, 1001)]
    [InlineData(false, 999)]
    [InlineData(false, 1001)]
    public async Task RefusesADownloadOfTheWrongSize(bool sendLength, long advertisedSize)
    {
        var payload = RandomNumberGenerator.GetBytes(1000);
        var release = Release(payload, size: advertisedSize);
        var path = Path.Combine(_folder, release.AssetName);

        using var http = new HttpClient(new StubHandler(payload, sendLength));
        await Assert.ThrowsAsync<AppUpdateException>(() =>
            AppUpdatePackageVerifier.DownloadAsync(http, release, path, null, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(_folder));
    }

    [Fact]
    public async Task ReportsAnHttpFailure()
    {
        var release = Release();
        using var http = new HttpClient(new StubHandler([], sendLength: true, status: HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<AppUpdateException>(() =>
            AppUpdatePackageVerifier.DownloadAsync(http, release, Path.Combine(_folder, release.AssetName), null, CancellationToken.None));

        Assert.Contains("404", ex.Message);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class StubHandler(byte[] payload, bool sendLength, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A non-seekable stream keeps HttpClient from inferring a Content-Length.
            HttpContent content = sendLength
                ? new ByteArrayContent(payload)
                : new StreamContent(new NonSeekableStream(payload));
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
