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
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>All HTTP and model bytes are synthetic; these tests never acquire or execute a model.</summary>
public sealed class FoundryLocalModelArtifactTests
{
    private static FoundryLocalSetupService Setup(Fixture fixture)
    {
        var runtime = NetworkTestProxy.Create<IFoundryLocalRuntimeService>((_, _) =>
            throw new Xunit.Sdk.XunitException("Staging must not touch the external runtime."));
        return new(new FoundryLocalCli(() => throw new Xunit.Sdk.XunitException("No CLI execution"),
            (_, _) => throw new Xunit.Sdk.XunitException("No CLI execution")), runtime, modelArtifacts: fixture.Stager);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupDeclineOrInvalidatedApprovalNeverStartsNetwork(bool invalidate)
    {
        using var fixture = new Fixture();
        var current = true;
        var confirmations = 0;
        var result = await Setup(fixture).StageModelFilesAsync(new(AiProviderKind.FoundryLocal, "", "unchanged"),
            (message, _) =>
            {
                confirmations++;
                Assert.Contains("877988985", message);
                Assert.Contains("not working initial-model setup", message);
                if (invalidate) current = false;
                return Task.FromResult(invalidate);
            }, () => current, null, default);
        Assert.False(result.Success);
        Assert.Equal(1, confirmations);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task SetupStagesRealAdapterButNeverRegistersLoadsOrChangesSelection()
    {
        using var fixture = new Fixture();
        var result = await Setup(fixture).StageModelFilesAsync(new(AiProviderKind.FoundryLocal, "", "unchanged"),
            (_, _) => Task.FromResult(true), () => true, null, default);
        Assert.True(result.Success);
        Assert.Equal(fixture.Payload, result.DirectoryPath);
        Assert.Contains("NOT registered", result.Guidance);
        Assert.Equal(10, fixture.Http.Requests.Count);
    }

    [Fact]
    public async Task SetupSurfacesPinnedOriginFailureAsPreparationFailure()
    {
        using var fixture = new Fixture();
        fixture.RegistryResponse = () => Fixture.Json(new { blobSasUri = "https://unapproved.invalid/?sig=secret" });
        var result = await Setup(fixture).StageModelFilesAsync(new(AiProviderKind.FoundryLocal, "", "unchanged"),
            (_, _) => Task.FromResult(true), () => true, null, default);
        Assert.False(result.Success);
        Assert.Null(result.DirectoryPath);
        Assert.Contains("outside the exact approved HTTPS origin/container", result.Guidance);
        Assert.DoesNotContain("secret", result.Guidance);
        Assert.Single(fixture.Http.Requests);
    }

    [Fact]
    public async Task ViewModelCancelsPendingModelConsentOnProviderChange()
    {
        using var fixture = new Fixture();
        var provider = AiProviderKind.FoundryLocal;
        var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
        {
            "get_AiProvider" => provider,
            "get_AiFoundryLocalEndpoint" => "",
            "get_AiFoundryLocalModel" => "unchanged",
            _ => throw new Xunit.Sdk.XunitException("No other settings access"),
        });
        var runtime = NetworkTestProxy.Create<IFoundryLocalRuntimeService>((_, _) =>
            throw new Xunit.Sdk.XunitException("No runtime mutation"));
        var capabilities = NetworkTestProxy.Create<IAiCapabilityService>((method, _) =>
        {
            Assert.Equal("Invalidate", method.Name);
            return null;
        });
        var vm = new FoundryLocalSettingsViewModel(settings, runtime, capabilities,
            NullLogger<FoundryLocalSettingsViewModel>.Instance, Setup(fixture));
        await vm.StageModelFilesAsync(async (_, token) =>
        {
            Assert.True(vm.IsPreparingAnything);
            Assert.False(vm.CanStageModelFiles);
            await vm.StageModelFilesAsync((_, _) => throw new Xunit.Sdk.XunitException("No duplicate confirmation"));
            provider = AiProviderKind.Ollama;
            vm.OnProviderChanged();
            Assert.True(token.IsCancellationRequested);
            return true;
        });
        Assert.False(vm.IsPreparingAnything);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public void ProductionPinsAreExactCompleteAndNotPublisherDigests()
    {
        var files = FoundryLocalModelArtifacts.PinnedFiles;
        Assert.Equal(9, files.Count);
        Assert.Equal(877988985, files.Sum(f => f.Bytes));
        Assert.Equal(["added_tokens.json", "genai_config.json", "merges.txt", "model.onnx", "model.onnx.data",
            "special_tokens_map.json", "tokenizer.json", "tokenizer_config.json", "vocab.json"], files.Select(f => f.Name));
        Assert.Equal([605L, 1517, 1671853, 169136, 861939200, 613, 11421894, 7334, 2776833], files.Select(f => f.Bytes));
        Assert.Equal(["0x8DE2350A9009EF0", "0x8DE2350A90F3495", "0x8DE2350A94CE2CF", "0x8DE2350A91B3519",
            "0x8DE2350AA9E9C1E", "0x8DE2350A91327D1", "0x8DE2350A96AF775", "0x8DE2350A9121792", "0x8DE2350A9346D32"], files.Select(f => f.ETag));
        Assert.All(files, f => Assert.Equal(new DateTimeOffset(2025, 11, 14, 7, 37, f.Name == "model.onnx.data" ? 30 : 27, TimeSpan.Zero), f.Modified));
        Assert.Contains("/versions/4", FoundryLocalModelArtifacts.AssetId);
        Assert.Contains("not independent publisher verification", FoundryLocalModelArtifacts.VerificationNotice);
        Assert.Contains("Download model files only", FoundryLocalModelArtifacts.ConsentSummary);
        Assert.Contains(FoundryLocalModelArtifacts.DisplayName, FoundryLocalModelArtifacts.ConsentSummary);
        Assert.Contains(FoundryLocalModelArtifacts.AssetId, FoundryLocalModelArtifacts.ConsentSummary);
        Assert.Contains("877988985 bytes", FoundryLocalModelArtifacts.ConsentSummary);
        Assert.Contains(FoundryLocalModelArtifacts.LicenseUrl, FoundryLocalModelArtifacts.ConsentSummary);
        Assert.Contains("does not import into the Foundry cache", FoundryLocalModelArtifacts.ConsentSummary);
        using var handler = FoundryLocalDownloader.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task Conditional200StagesExactlyNineFilesAndLocalReceiptsWithoutPersistingSas()
    {
        using var fixture = new Fixture();
        Assert.Empty(fixture.Http.Requests); // Construction is not consent.
        var progress = new InlineProgress();
        var result = await fixture.Stager.StageAsync(progress, default);
        Assert.Equal(fixture.Payload, result.DirectoryPath);
        Assert.Equal(FoundryLocalModelArtifacts.AssetId, result.AssetId);
        Assert.Equal(fixture.Files.Sum(f => f.Bytes), result.Bytes);
        Assert.Equal(9, Directory.GetFiles(result.DirectoryPath).Length);
        Assert.Equal(10, fixture.Http.Requests.Count);
        Assert.Empty(Directory.GetFiles(fixture.Partials));
        foreach (var file in fixture.Files)
        {
            Assert.Equal(fixture.Bytes[file.Name], await File.ReadAllBytesAsync(Path.Combine(result.DirectoryPath, file.Name)));
            var receipt = await File.ReadAllTextAsync(fixture.Receipt(file));
            Assert.DoesNotContain(Fixture.Secret, receipt);
            Assert.DoesNotContain("blobSasUri", receipt);
            Assert.Contains("\"Sha256\":", receipt);
        }
        Assert.InRange(progress.Messages.Count, 9, 920);
        Assert.DoesNotContain(progress.Messages, m => m.Contains(Fixture.Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Messages, m => m.Contains("100%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifiedRepeatRehashesOfflineWithoutResolvingRegistry()
    {
        using var fixture = new Fixture();
        var first = await fixture.Stage();
        fixture.Http.Requests.Clear();
        fixture.Http.Respond = _ => throw new InvalidOperationException("Offline");
        Assert.Equal(first, await fixture.Stage());
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("missing-etag")]
    [InlineData("weak-etag")]
    [InlineData("date")]
    [InlineData("missing-date")]
    [InlineData("length")]
    [InlineData("missing-length")]
    [InlineData("encoding")]
    [InlineData("range")]
    public async Task EveryResponseMustMatchPinnedStrongEtagDateAndLength(string problem)
    {
        using var fixture = new Fixture();
        fixture.BlobResponse = file =>
        {
            var response = fixture.Blob(file);
            switch (problem)
            {
                case "etag": response.Headers.ETag = new("\"different\""); break;
                case "missing-etag": response.Headers.ETag = null; break;
                case "weak-etag": response.Headers.ETag = new('"' + file.ETag + '"', isWeak: true); break;
                case "date": response.Content.Headers.LastModified = file.Modified.AddSeconds(1); break;
                case "missing-date": response.Content.Headers.LastModified = null; break;
                case "length": response.Content.Headers.ContentLength = file.Bytes + 1; break;
                case "missing-length": response.Content.Headers.ContentLength = null; break;
                case "encoding": response.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "range": response.Content.Headers.ContentRange = new(0, file.Bytes - 1, file.Bytes); break;
            }
            return response;
        };
        await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.Empty(Directory.GetFiles(fixture.Payload));
        Assert.Empty(Directory.GetFiles(fixture.Partials));
    }

    [Theory]
    [InlineData(412, false)]
    [InlineData(302, false)]
    [InlineData(307, false)]
    [InlineData(206, false)]
    [InlineData(500, false)]
    [InlineData(302, true)]
    [InlineData(308, true)]
    [InlineData(401, true)]
    public async Task Non200AndRedirectsNeverRetryOrRefreshCredentials(int status, bool registry)
    {
        using var fixture = new Fixture();
        var fail = new HttpResponseMessage((HttpStatusCode)status);
        fail.Headers.Location = new("https://evil.invalid/?secret=" + Fixture.Secret);
        if (registry) fixture.RegistryResponse = () => fail;
        else fixture.BlobResponse = _ => fail;
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain(Fixture.Secret, error.ToString());
        Assert.Equal(registry ? 1 : 2, fixture.Http.Requests.Count);
        Assert.Empty(Directory.GetFiles(fixture.Payload));
    }

    [Theory]
    [InlineData("http://amlwlrt4usc01.blob.core.windows.net/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3")]
    [InlineData("https://evil.invalid/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3")]
    [InlineData("https://amlwlrt4usc01.blob.core.windows.net/another-container")]
    [InlineData("https://amlwlrt4usc01.blob.core.windows.net:444/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3")]
    [InlineData("https://user@amlwlrt4usc01.blob.core.windows.net/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3")]
    [InlineData("https://amlwlrt4usc01.blob.core.windows.net/azureml-ab8fb672-187c-5c05-b028-d5004b5d5ae3/v4")]
    public async Task RegistryCannotChangeOriginContainerOrCredentials(string container)
    {
        using var fixture = new Fixture();
        fixture.RegistryResponse = () => Fixture.Json(new { blobSasUri = container + "?sig=" + Fixture.Secret });
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.DoesNotContain(Fixture.Secret, error.ToString());
        Assert.Single(fixture.Http.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("nested")]
    [InlineData("fragment")]
    [InlineData("no-query")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("oversized-stream")]
    public async Task RegistryJsonIsBoundedAndRequiresUnambiguousKnownSasShape(string problem)
    {
        using var fixture = new Fixture();
        fixture.RegistryResponse = () =>
        {
            var sas = FoundryLocalModelArtifacts.BlobContainer + "?sig=" + Fixture.Secret;
            return problem switch
            {
                "missing" => Fixture.Json(new { unknown = sas }),
                "null" => Fixture.Json(new { blobSasUri = (string?)null }),
                "duplicate" => Fixture.RawJson($"{{\"blobSasUri\":\"{sas}\",\"blobSasUri\":\"{sas}\"}}"),
                "nested" => Fixture.Json(new { unknown = new { blobSasUri = sas } }),
                "fragment" => Fixture.Json(new { blobSasUri = sas + "#fragment" }),
                "no-query" => Fixture.Json(new { blobSasUri = FoundryLocalModelArtifacts.BlobContainer }),
                "malformed" => Fixture.RawJson(Fixture.Secret),
                "oversized" => Fixture.RawJson(new string('x', 65537)),
                _ => new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekStream(new byte[65537])) },
            };
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.DoesNotContain(Fixture.Secret, error.ToString());
        Assert.Single(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task LyingContentLengthDoesNotPermitShortOrOversizedStream(int difference)
    {
        using var fixture = new Fixture();
        fixture.BlobResponse = f => fixture.Blob(f, new NonSeekStream(new byte[f.Bytes + difference]));
        await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.Single(Directory.GetFiles(fixture.Partials, "*.partial"));
        Assert.Empty(Directory.GetFiles(fixture.Payload));
        Assert.Empty(Directory.GetFiles(fixture.Receipts));
        Assert.Equal(2, fixture.Http.Requests.Count);
    }

    [Fact]
    public async Task CancelledStreamRetainsPartialAndExplicitRetryStartsFreshWithoutRange()
    {
        using var fixture = new Fixture();
        using var cts = new CancellationTokenSource();
        fixture.BlobResponse = f => fixture.Blob(f, new CancelStream(fixture.Bytes[f.Name], cts));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Stager.StageAsync(null, cts.Token));
        var partial = Assert.Single(Directory.GetFiles(fixture.Partials, "*.partial"));
        Assert.Empty(Directory.GetFiles(fixture.Payload));
        Assert.Empty(Directory.GetFiles(fixture.Receipts));
        fixture.BlobResponse = f => fixture.Blob(f);
        await fixture.Stage();
        Assert.True(File.Exists(partial));
        Assert.Single(Directory.GetFiles(fixture.Partials));
        Assert.Equal(12, fixture.Http.Requests.Count);
    }

    [Fact]
    public async Task ExplicitRecoveryReusesOnlyCompletedReceiptedFiles()
    {
        using var fixture = new Fixture();
        fixture.BlobResponse = f => f.Name == fixture.Files[1].Name ? new(HttpStatusCode.PreconditionFailed) : fixture.Blob(f);
        await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.Single(Directory.GetFiles(fixture.Payload));
        fixture.Http.Requests.Clear();
        fixture.BlobResponse = f => fixture.Blob(f);
        await fixture.Stage();
        Assert.Equal(9, fixture.Http.Requests.Count);
        Assert.DoesNotContain(fixture.Http.Requests.Skip(1), uri => uri.AbsolutePath.EndsWith(fixture.Files[0].Name, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("missing-receipt")]
    [InlineData("missing-file")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("ManifestId")]
    [InlineData("Asset")]
    [InlineData("Name")]
    [InlineData("Bytes")]
    [InlineData("ETag")]
    [InlineData("Modified")]
    [InlineData("Sha256")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("extra-file")]
    public async Task CorruptCacheAndReceiptFailActionablyBeforeAnyNetwork(string problem)
    {
        using var fixture = new Fixture();
        await fixture.Stage();
        fixture.Http.Requests.Clear();
        var file = fixture.Files[^1]; // Even later corruption is detected before any missing-file acquisition.
        var path = Path.Combine(fixture.Payload, file.Name);
        var receiptPath = fixture.Receipt(file);
        switch (problem)
        {
            case "bytes": await File.WriteAllBytesAsync(path, new byte[file.Bytes]); break;
            case "missing-receipt": File.Delete(receiptPath); break;
            case "missing-file": File.Delete(path); break;
            case "malformed": await File.WriteAllTextAsync(receiptPath, Fixture.Secret); break;
            case "oversized": await File.WriteAllTextAsync(receiptPath, new string('x', 4097)); break;
            case "extra-file": await File.WriteAllTextAsync(Path.Combine(fixture.Payload, "extra"), "unexpected"); break;
            case "duplicate":
                var text = await File.ReadAllTextAsync(receiptPath);
                await File.WriteAllTextAsync(receiptPath, text.Insert(1, "\"Name\":\"other\","));
                break;
            default:
                var json = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
                if (problem == "Bytes") json[problem] = 1;
                else json[problem] = Fixture.Secret;
                await File.WriteAllTextAsync(receiptPath, json.ToJsonString());
                break;
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.Contains("cache", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Fixture.Secret, error.ToString());
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportAndStreamingExceptionsDoNotExposeSasOrInnerExceptions(bool streaming)
    {
        using var fixture = new Fixture();
        if (streaming) fixture.BlobResponse = f => fixture.Blob(f, new ThrowStream(fixture.Bytes[f.Name]));
        else fixture.Http.Respond = request => throw new HttpRequestException("Failed " + request.RequestUri + Fixture.Secret);
        var error = await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Fixture.Secret, error.ToString());
    }

    [Fact]
    public async Task AgePolicyAndAlreadyCancelledOperationDoNotAccessNetwork()
    {
        using var fixture = new Fixture(now: new(2025, 11, 20, 0, 0, 0, TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidDataException>(fixture.Stage);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Stager.StageAsync(null, cts.Token));
        Assert.Empty(fixture.Http.Requests);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task DeadlineStopsCooperativeTransportWithoutRetry()
    {
        var clock = new DeadlineClock();
        using var fixture = new Fixture(timeProvider: clock);
        fixture.Http.WaitForCancellation = true;
        var staging = fixture.Stage();
        Assert.False(staging.IsCompleted);
        clock.Expire();
        await Assert.ThrowsAsync<TimeoutException>(() => staging);
        Assert.Single(fixture.Http.Requests);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _expire;
        internal void Expire() => _expire!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMinutes(1), dueTime);
            _expire = () => callback(state);
            return new DeadlineTimer();
        }
        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class InlineProgress : IProgress<string>
    {
        internal List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    private class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class CancelStream(byte[] bytes, CancellationTokenSource cts) : NonSeekStream(bytes)
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads == 2) cts.Cancel();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 128)], cancellationToken);
        }
    }

    private sealed class ThrowStream(byte[] bytes) : NonSeekStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("Transport failed at ?sig=" + Fixture.Secret);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Secret = "synthetic-SAS-do-not-leak";
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "FoundryModelFixtures-" + Guid.NewGuid().ToString("N"));
        internal string Payload => Path.Combine(Root, FoundryLocalModelArtifacts.VersionId, "v4");
        internal string Receipts => Path.Combine(Root, FoundryLocalModelArtifacts.VersionId, "receipts");
        internal string Partials => Path.Combine(Root, FoundryLocalModelArtifacts.VersionId, "partials");
        internal FoundryLocalModelFile[] Files { get; }
        internal Dictionary<string, byte[]> Bytes { get; }
        internal HttpStub Http { get; } = new();
        internal FoundryLocalModelArtifacts Stager { get; }
        internal Func<HttpResponseMessage> RegistryResponse;
        internal Func<FoundryLocalModelFile, HttpResponseMessage> BlobResponse;
        internal Fixture(DateTimeOffset? now = null, TimeProvider? timeProvider = null)
        {
            Files = FoundryLocalModelArtifacts.PinnedFiles.Select((f, i) => f with { Bytes = i == 0 ? 190000 : 16 + i }).ToArray();
            Bytes = Files.ToDictionary(f => f.Name, f => Enumerable.Repeat((byte)42, (int)f.Bytes).ToArray());
            RegistryResponse = () => Json(new { blobSasUri = FoundryLocalModelArtifacts.BlobContainer + "?sv=synthetic&sig=" + Secret });
            BlobResponse = f => Blob(f);
            Http.Respond = request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                Assert.False(request.Headers.Contains("Proxy-Authorization"));
                Assert.Null(request.Headers.Range);
                if (request.RequestUri == FoundryLocalModelArtifacts.RegistryUri)
                {
                    Assert.Empty(request.Headers.IfMatch);
                    return RegistryResponse();
                }
                var file = Assert.Single(Files, f => request.RequestUri!.AbsolutePath.EndsWith("/v4/" + f.Name, StringComparison.Ordinal));
                Assert.Equal(FoundryLocalModelArtifacts.BlobContainer + "/v4/" + file.Name + "?sv=synthetic&sig=" + Secret, request.RequestUri!.AbsoluteUri);
                var condition = Assert.Single(request.Headers.IfMatch);
                Assert.False(condition.IsWeak);
                Assert.Equal('"' + file.ETag + '"', condition.Tag);
                return BlobResponse(file);
            };
            Stager = new(Root, Http, Files, () => now ?? new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(1), timeProvider);
        }
        internal Task<FoundryLocalStagedModel> Stage() => Stager.StageAsync(null, default);
        internal string Receipt(FoundryLocalModelFile file) => Path.Combine(Receipts, file.Name + ".json");
        internal HttpResponseMessage Blob(FoundryLocalModelFile file, Stream? stream = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream ?? new NonSeekStream(Bytes[file.Name])),
            };
            response.Headers.ETag = new EntityTagHeaderValue('"' + file.ETag + '"');
            response.Content.Headers.ContentLength = file.Bytes;
            response.Content.Headers.LastModified = file.Modified;
            return response;
        }
        internal static HttpResponseMessage Json(object value) => RawJson(JsonSerializer.Serialize(value));
        internal static HttpResponseMessage RawJson(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        public void Dispose()
        {
            Stager.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class HttpStub : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => throw new InvalidOperationException("No handler");
        internal bool WaitForCancellation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Respond(request);
        }
    }
}
