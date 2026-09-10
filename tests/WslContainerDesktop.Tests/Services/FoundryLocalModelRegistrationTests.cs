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

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Synthetic bytes and in-memory HTTP only: no runtime, download, CLI, or model execution.</summary>
public sealed class FoundryLocalModelRegistrationTests
{
    [Fact]
    public async Task RegistersExactScannerLayoutAndTemplatesThenReusesOffline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var registered = await fixture.Register(staged);
        Assert.Equal(fixture.Payload, registered);
        Assert.False(File.Exists(fixture.Sentinel));
        Assert.Equal(10, Directory.GetFiles(registered).Length);
        Assert.Empty(Directory.GetDirectories(registered));
        foreach (var file in fixture.Files)
            Assert.Equal(fixture.Bytes[file.Name], File.ReadAllBytes(Path.Combine(registered, file.Name)));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(registered, "inference_model.json")));
        Assert.Equal(2, metadata.RootElement.EnumerateObject().Count());
        Assert.Equal("qwen2.5-0.5b-instruct-generic-cpu:4", metadata.RootElement.GetProperty("Name").GetString());
        var templates = metadata.RootElement.GetProperty("PromptTemplate");
        Assert.Equal(4, templates.EnumerateObject().Count());
        Assert.Equal("<|im_start|>system\n{Content}<|im_end|>", templates.GetProperty("system").GetString());
        Assert.Equal("<|im_start|>user\n{Content}<|im_end|>", templates.GetProperty("user").GetString());
        Assert.Equal("<|im_start|>assistant\n{Content}<|im_end|>", templates.GetProperty("assistant").GetString());
        Assert.Equal("<|im_start|>user\n{Content}<|im_end|>\n<|im_start|>assistant", templates.GetProperty("prompt").GetString());
        var marker = File.ReadAllBytes(fixture.Marker);
        var timestamps = Directory.GetFiles(registered).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        Assert.Equal(registered, await fixture.Register(staged));
        Assert.Equal(marker, File.ReadAllBytes(fixture.Marker));
        Assert.All(timestamps, pair => Assert.Equal(pair.Value, File.GetLastWriteTimeUtc(pair.Key)));
        Assert.Equal("untouched", File.ReadAllText(fixture.ForeignFile));
        Assert.False(File.Exists(fixture.Sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRetainsSentinelAndCompatibleRetryCompletes(bool afterFirstCopy)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(message =>
        {
            Assert.True(File.Exists(fixture.Sentinel));
            if (!afterFirstCopy || message.StartsWith("Verified registered", StringComparison.Ordinal))
                cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Register(staged, progress, cancellation.Token));
        Assert.True(File.Exists(fixture.Sentinel));
        Assert.True(File.Exists(fixture.Marker));
        Assert.False(File.Exists(Path.Combine(fixture.Payload, "inference_model.json")));
        Assert.Equal(fixture.Payload, await fixture.Register(staged));
        Assert.False(File.Exists(fixture.Sentinel));
    }

    [Fact]
    public async Task PreCancelledOperationDoesNotReserveAnything()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Register(staged, null, new(true)));
        Assert.False(Directory.Exists(fixture.Owned));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("malformed-marker")]
    public async Task ForeignSameNamedCollisionIsNeverAdoptedOrChanged(string collision)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        if (collision == "file") File.WriteAllText(fixture.Owned, "foreign");
        else
        {
            Directory.CreateDirectory(fixture.Owned);
            if (collision == "malformed-marker") File.WriteAllText(fixture.Marker, "{}");
        }
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Register(staged));
        if (collision == "file") Assert.Equal("foreign", File.ReadAllText(fixture.Owned));
        else
        {
            Assert.False(Directory.Exists(fixture.Payload));
            if (collision == "malformed-marker") Assert.Equal("{}", File.ReadAllText(fixture.Marker));
            else Assert.Empty(Directory.GetFileSystemEntries(fixture.Owned));
        }
        Assert.Equal("untouched", File.ReadAllText(fixture.ForeignFile));
    }

    [Theory]
    [InlineData("added_tokens.json")]
    [InlineData("inference_model.json")]
    public async Task TamperedOwnedFilesAreRejectedWithoutOverwriteAndRemainHidden(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        await fixture.Register(staged);
        var target = Path.Combine(fixture.Payload, name);
        var altered = File.ReadAllBytes(target);
        altered[0] ^= 1;
        File.WriteAllBytes(target, altered);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Register(staged));
        Assert.Equal(altered, File.ReadAllBytes(target));
        Assert.True(File.Exists(fixture.Sentinel));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("length")]
    [InlineData("receipt")]
    [InlineData("missing-receipt")]
    [InlineData("extra-file")]
    public async Task SourceMustStillMatchPinnedManifestAndStagerReceipts(string mutation)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var sourceFile = Path.Combine(staged.DirectoryPath, fixture.Files[0].Name);
        var receipt = Path.Combine(Path.GetDirectoryName(staged.DirectoryPath)!, "receipts", fixture.Files[0].Name + ".json");
        switch (mutation)
        {
            case "bytes":
                var bytes = File.ReadAllBytes(sourceFile);
                bytes[0] ^= 1;
                File.WriteAllBytes(sourceFile, bytes);
                break;
            case "length": File.AppendAllText(sourceFile, "x"); break;
            case "receipt": File.WriteAllText(receipt, "{}"); break;
            case "missing-receipt": File.Delete(receipt); break;
            case "extra-file": File.WriteAllText(Path.Combine(staged.DirectoryPath, "unexpected"), "x"); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Register(staged));
        Assert.False(Directory.Exists(fixture.Owned));
    }

    [Fact]
    public async Task PublicConstructorCannotAcceptSyntheticSizesOrUnpinnedIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        await Assert.ThrowsAsync<InvalidDataException>(() => new FoundryLocalModelRegistration()
            .RegisterAsync(staged, fixture.Cache, null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Register(staged with { AssetId = "another-model" }));
        Assert.False(Directory.Exists(fixture.Owned));
    }

    [Fact]
    public async Task MissingExternalRootIsNotCreated()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var missing = Path.Combine(fixture.Root, "missing");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RegisterAsync(staged, missing, null, default));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task RegistrationCannotWriteIntoTheStagedModel()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.RegisterAsync(staged, staged.DirectoryPath, null, default));
        Assert.Equal(9, Directory.GetFileSystemEntries(staged.DirectoryPath).Length);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("cache")]
    public async Task TraversalSegmentsAreRejectedBeforeAnyReservation(string location)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var cache = fixture.Cache;
        if (location == "cache") cache = Path.Combine(cache, "..", Path.GetFileName(cache));
        else staged = staged with { DirectoryPath = Path.Combine(staged.DirectoryPath, "..", "v4") };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RegisterAsync(staged, cache, null, default));
        Assert.False(Directory.Exists(fixture.Owned));
    }

    [Theory]
    [InlineData("source-file")]
    [InlineData("source-directory")]
    [InlineData("receipt")]
    [InlineData("cache")]
    [InlineData("owned")]
    [InlineData("registered-file")]
    public async Task ReparsePointsAreRejectedWithoutFollowingOrChangingTheirTargets(string location)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var target = Path.Combine(fixture.Root, "link-target");
        Directory.CreateDirectory(target);
        var canary = Path.Combine(target, "canary");
        File.WriteAllText(canary, "untouched");
        var link = "";
        var directory = false;
        try
        {
            switch (location)
            {
                case "source-file": link = Path.Combine(staged.DirectoryPath, fixture.Files[0].Name); File.Delete(link); break;
                case "receipt":
                    link = Path.Combine(Path.GetDirectoryName(staged.DirectoryPath)!, "receipts", fixture.Files[0].Name + ".json");
                    File.Delete(link); break;
                case "source-directory":
                    link = staged.DirectoryPath; Directory.Delete(link, true); directory = true; break;
                case "cache":
                    link = Path.Combine(fixture.Root, "cache-link"); directory = true; break;
                case "owned": link = fixture.Owned; directory = true; break;
                case "registered-file":
                    await fixture.Register(staged);
                    link = Path.Combine(fixture.Payload, fixture.Files[0].Name); File.Delete(link); break;
            }
            try
            {
                if (directory) Directory.CreateSymbolicLink(link, target);
                else File.CreateSymbolicLink(link, canary);
            }
            catch (UnauthorizedAccessException)
            {
                // Developer Mode or SeCreateSymbolicLinkPrivilege is needed on Windows.
                return;
            }
            await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Service.RegisterAsync(staged, location == "cache" ? link : fixture.Cache, null, default));
            Assert.Equal("untouched", File.ReadAllText(canary));
            Assert.Single(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            if (link.Length > 0 && Path.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
            {
                if (directory) Directory.Delete(link);
                else File.Delete(link);
            }
        }
    }

    [Fact]
    public async Task HeldSourceAndAncestorHandlesPreventMutationAndRenameWhileCopying()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var invoked = false;
        await fixture.Register(staged, new InlineProgress(_ =>
        {
            if (invoked) return;
            invoked = true;
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(staged.DirectoryPath, fixture.Files[0].Name), "changed"));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Cache, fixture.Cache + "-renamed"));
            Assert.Throws<IOException>(() => Directory.Move(fixture.Owned, fixture.Owned + "-renamed"));
            Assert.Throws<IOException>(() => Directory.Move(staged.DirectoryPath, staged.DirectoryPath + "-renamed"));
        }));
        Assert.True(invoked);
    }

    [Fact]
    public async Task ConcurrentRegistrationCannotAdoptAnInFlightReservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var attempted = false;
        await fixture.Register(staged, new InlineProgress(_ =>
        {
            if (attempted) return;
            attempted = true;
            Assert.Throws<IOException>(() => fixture.Register(staged).GetAwaiter().GetResult());
        }));
        Assert.True(attempted);
        Assert.False(File.Exists(fixture.Sentinel));
    }

    [Fact]
    public async Task RetainedPartialIsNeverResumedOrDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Register(staged,
            new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        var retained = Path.Combine(fixture.Owned, "partials", Guid.NewGuid().ToString("N") + ".partial");
        File.WriteAllText(retained, "interrupted bytes");
        await fixture.Register(staged);
        Assert.Equal("interrupted bytes", File.ReadAllText(retained));
        Assert.False(File.Exists(fixture.Sentinel));
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("sentinel")]
    [InlineData("extra-payload")]
    [InlineData("extra-owned")]
    public async Task IncompatibleOwnershipOrUnexpectedContentIsNeverCleared(string mutation)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Register(staged,
            new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        var changed = mutation switch
        {
            "marker" => fixture.Marker,
            "sentinel" => fixture.Sentinel,
            "extra-payload" => Path.Combine(fixture.Payload, "foreign.txt"),
            _ => Path.Combine(fixture.Owned, "foreign.txt"),
        };
        File.WriteAllText(changed, "foreign");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Register(staged));
        Assert.Equal("foreign", File.ReadAllText(changed));
        Assert.True(File.Exists(fixture.Sentinel));
        Assert.Equal("untouched", File.ReadAllText(fixture.ForeignFile));
    }

    [Fact]
    public async Task CopiedReservationCannotClaimAnotherCacheRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        await fixture.Register(staged);
        var otherRoot = Path.Combine(fixture.Root, "other-cache");
        var otherOwned = Path.Combine(otherRoot, FoundryLocalModelRegistration.OwnedDirectoryName);
        Directory.CreateDirectory(otherOwned);
        var otherMarker = Path.Combine(otherOwned, FoundryLocalModelRegistration.MarkerName);
        File.Copy(fixture.Marker, otherMarker);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RegisterAsync(staged, otherRoot, null, default));
        Assert.Single(Directory.GetFileSystemEntries(otherOwned));
        Assert.Equal(File.ReadAllBytes(fixture.Marker), File.ReadAllBytes(otherMarker));
    }

    [Fact]
    public async Task ExplicitPreparationCanCreateAFreshCliCacheWithoutFollowingReparsePoints()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var staged = await fixture.Stage();
        var cache = Path.Combine(fixture.Root, "fresh", "models");
        var registered = await fixture.Service.RegisterAsync(staged, cache, null, default, createCacheRoot: true);
        Assert.True(File.Exists(Path.Combine(registered, "inference_model.json")));
        Assert.False(File.Exists(Path.Combine(registered, "download.tmp")));
        Assert.Equal("untouched", File.ReadAllText(fixture.ForeignFile));
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "FoundryRegistrationFixtures-" + Guid.NewGuid().ToString("N"));
        internal string Cache => Path.Combine(Root, "external");
        internal string Owned => Path.Combine(Cache, FoundryLocalModelRegistration.OwnedDirectoryName);
        internal string Payload => Path.Combine(Owned, "v4");
        internal string Sentinel => Path.Combine(Payload, "download.tmp");
        internal string Marker => Path.Combine(Owned, FoundryLocalModelRegistration.MarkerName);
        internal string ForeignFile => Path.Combine(Cache, "user-model.txt");
        internal FoundryLocalModelFile[] Files { get; }
        internal Dictionary<string, byte[]> Bytes { get; }
        internal FoundryLocalModelRegistration Service { get; }
        private readonly FoundryLocalModelArtifacts _stager;
        internal FoundryLocalModelArtifacts Stager => _stager;
        private readonly HttpStub _http;
        internal Fixture()
        {
            Files = FoundryLocalModelArtifacts.PinnedFiles.Select((file, i) => file with { Bytes = 256 + i }).ToArray();
            Bytes = Files.ToDictionary(file => file.Name, file => Enumerable.Repeat((byte)42, (int)file.Bytes).ToArray());
            Directory.CreateDirectory(Cache);
            File.WriteAllText(ForeignFile, "untouched");
            _http = new HttpStub(Files, Bytes);
            _stager = new(Path.Combine(Root, "staged"), _http, Files,
                () => new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(1));
            Service = new(Files);
        }
        internal async Task<FoundryLocalStagedModel> Stage()
        {
            var result = await _stager.StageAsync(null, default);
            _http.Offline = true;
            return result;
        }
        internal Task<string> Register(FoundryLocalStagedModel staged, IProgress<string>? progress = null, CancellationToken ct = default)
            => Service.RegisterAsync(staged, Cache, progress, ct);
        public void Dispose()
        {
            _stager.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class HttpStub(FoundryLocalModelFile[] files, Dictionary<string, byte[]> bytes) : HttpMessageHandler
    {
        internal bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.False(Offline, "Registration must remain entirely offline.");
            if (request.RequestUri == FoundryLocalModelArtifacts.RegistryUri)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        blobSasUri = FoundryLocalModelArtifacts.BlobContainer + "?sv=synthetic&sig=synthetic",
                    }), Encoding.UTF8, "application/json"),
                });
            var file = Assert.Single(files, f => request.RequestUri!.AbsolutePath.EndsWith("/v4/" + f.Name, StringComparison.Ordinal));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[file.Name]) };
            response.Headers.ETag = new EntityTagHeaderValue('"' + file.ETag + '"');
            response.Content.Headers.LastModified = file.Modified;
            return Task.FromResult(response);
        }
    }
}
