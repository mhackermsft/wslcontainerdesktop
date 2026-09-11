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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class FoundryLocalTests
{
    [Theory]
    [InlineData("http://localhost:43210", "localhost")]
    [InlineData("http://127.0.0.1:43210/v1", "127.0.0.1")]
    [InlineData("http://127.12.34.56:43210/", "127.12.34.56")]
    [InlineData("https://[::1]:43210/v1/", "[::1]")]
    [InlineData("http://127.0.0.1:80", "127.0.0.1")]
    public void OnlyExplicitLoopbackEndpointIsAccepted(string input, string host)
    {
        var uri = FoundryLocalEndpoint.BuildUri(input, "v1/chat/completions");
        Assert.Equal(host, uri.Host);
        Assert.Equal("/v1/chat/completions", uri.AbsolutePath);
        Assert.Equal("", uri.UserInfo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://api.openai.com:443/v1")]
    [InlineData("http://192.168.1.1:1234")]
    [InlineData("http://0.0.0.0:1234")]
    [InlineData("http://[::]:1234")]
    [InlineData("http://localhost.example:1234")]
    [InlineData("http://user:password@localhost:1234")]
    [InlineData("http://localhost:1234?key=secret")]
    [InlineData("http://localhost:1234?")]
    [InlineData("http://localhost:1234#")]
    [InlineData("http://localhost:1234#fragment")]
    [InlineData("file://localhost:1234")]
    [InlineData("http://localhost:0")]
    [InlineData("http://localhost:65536")]
    [InlineData("http://localhost:1234/other")]
    [InlineData("http://localhost:1234/v1/../")]
    [InlineData("http://localhost:1234/v1/chat/completions")]
    public void InvalidDestinationsHaveNoFallback(string input) =>
        Assert.Throws<ArgumentException>(() => FoundryLocalEndpoint.Validate(input));

    [Fact]
    public void ProductionClientDisablesRedirectsProxiesCookiesAndIntegratedCredentials()
    {
        using var handler = FoundryLocalHttpClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void HttpsLocalhostRetainsCertificateAuthorityWhileSocketIsPinnedWithoutDns()
    {
        var uri = FoundryLocalEndpoint.BuildUri("https://localhost:43210/v1", "v1/chat/completions");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(certificate.MatchesHostname(uri.IdnHost, allowCommonName: false));
        Assert.False(certificate.MatchesHostname("127.0.0.1", allowCommonName: false));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 43210),
            FoundryLocalHttpClient.SocketEndpoint(new(uri.IdnHost, uri.Port)));
    }

    [Theory]
    [InlineData("localhost.example")]
    [InlineData("192.168.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void SocketPinningRejectsNonloopbackEvenOutsideUriBuilder(string host) =>
        Assert.Throws<ArgumentException>(() => FoundryLocalHttpClient.SocketEndpoint(new(host, 43210)));

    [Theory]
    [InlineData("127.12.34.56")]
    [InlineData("::1")]
    public void SocketPinningPreservesExplicitLoopbackLiteral(string host)
    {
        Assert.Equal(new IPEndPoint(IPAddress.Parse(host), 43210),
            FoundryLocalHttpClient.SocketEndpoint(new(host, 43210)));
    }

    [Fact]
    public void LoadGuidanceDoesNotConfuseCachedDataWithAuditedEpPreparation()
    {
        Assert.Contains("Model loading and runtime-driven model/EP acquisition are blocked", FoundryLocalRuntimeService.AcquisitionGuidance);
        Assert.Contains("Runtime-only package registration also does not establish model readiness", FoundryLocalRuntimeService.AcquisitionGuidance);
        Assert.Contains("does not register or load the model", FoundryLocalRuntimeService.AcquisitionGuidance);
        Assert.Contains("user attestation are not substitutes", FoundryLocalRuntimeService.AcquisitionGuidance);
        Assert.Contains("not hardware compatibility measurements", FoundryLocalRuntimeService.AcquisitionGuidance);
        Assert.Contains("preview Foundry Local CLI REST API", FoundryLocalRuntimeService.MemoryPolicy);
        Assert.Contains("parity with an SDK's optional REST server is not guaranteed", FoundryLocalRuntimeService.MemoryPolicy);
        Assert.Contains("without choosing an EP", FoundryLocalRuntimeService.MemoryPolicy);
        Assert.Contains("externally prepared, already-loaded host", FoundryLocalRuntimeService.AcquisitionGuidance);
    }

    [Fact]
    public async Task MetadataReadsOnlyFourDocumentedInventoryRoutesAndDoesNotTrustAdvertisedEndpoint()
    {
        using var f = new Fixture();
        var inventory = await f.Runtime.ReadInventoryAsync(f.Configuration, default);
        Assert.True(inventory.IsCached);
        Assert.True(inventory.IsLoaded);
        Assert.Equal("synthetic-license", inventory.Selected!.License);
        Assert.Equal(123, inventory.Selected.FileSizeMb);
        Assert.Equal("CPU", inventory.Selected.DeviceType);
        Assert.Equal(new[] { "/openai/status", "/foundry/list", "/openai/models", "/openai/loadedmodels" },
            f.Handler.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(f.Handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("127.0.0.1", r.Uri.Host);
            Assert.Empty(r.Body);
            Assert.DoesNotContain("Authorization", r.Headers.Keys);
        });
    }

    [Theory]
    [InlineData(false, false, AiModelState.Missing, AiDownloadState.NotDownloaded, AiLoadState.Unloaded)]
    [InlineData(true, false, AiModelState.Available, AiDownloadState.Downloaded, AiLoadState.Unloaded)]
    [InlineData(true, true, AiModelState.Available, AiDownloadState.Downloaded, AiLoadState.Loaded)]
    public async Task ReadinessAndFeaturesAreIndependent(bool cached, bool loaded, AiModelState model, AiDownloadState download, AiLoadState load)
    {
        using var f = new Fixture();
        f.Handler.IsCached = cached;
        f.Handler.IsLoaded = loaded;
        var state = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        Assert.Equal(AiRuntimeState.Ready, state.Runtime);
        Assert.Equal(model, state.Model);
        Assert.Equal(download, state.Download);
        Assert.Equal(load, state.Load);
        Assert.Equal(AiSupport.Unknown, state.Chat.Support);
        Assert.Equal(AiSupport.Unknown, state.Tools.Support); // advertised true isn't protocol proof
        Assert.Equal(AiSupport.Unknown, state.Streaming.Support);
        Assert.False(state.CanUseTools);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task FailedRuntimeStatusCannotProveReadinessOrFollowRedirects(int status)
    {
        using var f = new Fixture();
        f.Handler.StatusCode = (HttpStatusCode)status;
        var state = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        Assert.Equal(AiRuntimeState.Unavailable, state.Runtime);
        Assert.False(state.CanChat);
        Assert.Single(f.Handler.Requests);
        Assert.Equal(status is 401 or 403 ? AiAuthenticationState.RequiredOrRejected : AiAuthenticationState.Unknown,
            state.Authentication);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"models":null}""")]
    [InlineData("""{"models":[{}]}""")]
    [InlineData("""{"models":[{"name":"duplicate"},{"name":"duplicate"}]}""")]
    public async Task MalformedInventoryDoesNotPublishFeatureProof(string catalog)
    {
        using var f = new Fixture();
        f.Handler.CatalogOverride = catalog;
        var state = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        Assert.False(state.CanChat);
        Assert.Equal(AiModelState.Unknown, state.Model);
    }

    [Fact]
    public async Task ExplicitProbeRequiresAlreadyLoadedCachedCatalogModel()
    {
        using var f = new Fixture();
        f.Handler.IsLoaded = false;
        var metadata = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        var result = await f.Observer.ProbeAsync(metadata, default);
        Assert.False(result.CanChat);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("/load/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitSyntheticProbesVerifyChatToolsJsonAndStreamingWithoutCallbacks()
    {
        using var f = new Fixture();
        var metadata = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        f.Handler.Completions.Enqueue(JsonReply("OK"));
        f.Handler.Completions.Enqueue(JsonReply(null, new() { Id = "probe", Name = "capability_ack", ArgumentsJson = """{"ok":true}""" }));
        f.Handler.Completions.Enqueue(JsonReply("""{"ok":true}"""));
        f.Handler.Completions.Enqueue(JsonReply("OK")); // Synthetic tool-result roundtrip.
        f.Handler.Completions.Enqueue(SseReply("OK"));
        var result = await f.Observer.ProbeAsync(metadata, default);
        Assert.True(result.CanUseTools);
        Assert.Equal(AiSupport.Supported, result.StructuredJson.Support);
        Assert.Equal(AiSupport.Supported, result.Streaming.Support);
        var requests = f.Handler.Requests.Where(r => r.Method == HttpMethod.Post).ToArray();
        Assert.Equal(5, requests.Length);
        Assert.All(requests, r =>
        {
            using var body = JsonDocument.Parse(r.Body);
            Assert.Equal(ModelId, body.RootElement.GetProperty("model").GetString());
            Assert.Equal(64, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.DoesNotContain("stop_container", r.Body);
            Assert.Empty(r.Headers);
        });
    }

    [Fact]
    public async Task WrongResponseModelDoesNotGrantCapabilities()
    {
        using var f = new Fixture();
        var metadata = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        f.Handler.Completions.Enqueue(JsonReply("OK", model: "wrong-model"));
        var result = await f.Observer.ProbeAsync(metadata, default);
        Assert.False(result.CanChat);
        Assert.False(result.CanUseTools);
        Assert.Single(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CatalogToolRejectionIsNotOverriddenByProbe()
    {
        using var f = new Fixture();
        f.Handler.ToolsAdvertised = false;
        var metadata = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        f.Handler.Completions.Enqueue(JsonReply("OK"));
        f.Handler.Completions.Enqueue(JsonReply("""{"ok":true}"""));
        f.Handler.Completions.Enqueue(SseReply("OK"));
        var result = await f.Observer.ProbeAsync(metadata, default);
        Assert.True(result.CanChat);
        Assert.Equal(AiSupport.Unsupported, result.Tools.Support);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Body.Contains("capability_ack", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AiSupport.Unknown)]
    [InlineData(AiSupport.Unsupported)]
    public async Task UnknownOrUnsupportedToolsBlockBeforeInference(AiSupport tools)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Capabilities.Snapshot = f.Capabilities.Snapshot! with { Tools = new(tools) };
        f.Handler.Requests.Clear();
        await Assert.ThrowsAsync<AiProviderException>(() => f.Provider.RunTurnAsync(f.Request, Tools, NoTool, default));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData(AiSupport.Unknown, false)]
    [InlineData(AiSupport.Unsupported, false)]
    [InlineData(AiSupport.Supported, true)]
    public async Task StreamingNeedsPositiveProofAndUsesSharedParser(AiSupport support, bool streaming)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Capabilities.Snapshot = f.Capabilities.Snapshot! with { Streaming = new(support) };
        f.Handler.Completions.Enqueue(streaming ? SseReply("Reply") : JsonReply("Reply"));
        var events = new List<AiChatProgress>();
        var result = await f.Provider.RunTurnAsync(f.Request with { Progress = events.Add }, [], NoTool, default);
        Assert.Equal("Reply", result.FinalText);
        using var payload = JsonDocument.Parse(f.Handler.Requests.Last().Body);
        Assert.Equal(streaming, payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain(events, e => e.Kind is AiChatProgressKind.ExecutingTool or AiChatProgressKind.AwaitingApproval);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosisUsesIsolatedConfigurationAndOnlyVerifiedJsonMode(bool json)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Capabilities.Snapshot = f.Capabilities.Snapshot! with { StructuredJson = new(json ? AiSupport.Supported : AiSupport.Unknown) };
        f.Handler.Completions.Enqueue(JsonReply("""{"summary":"ok","likelyCause":"configured","confidence":1}"""));
        var diagnosis = await f.Provider.CompleteAsync(new("Return JSON", "password: synthetic-diagnostic-secret"), default);
        Assert.Equal("ok", diagnosis.Summary);
        var request = f.Handler.Requests.Last();
        Assert.Equal("127.0.0.1", request.Uri.Host);
        Assert.Empty(request.Headers);
        Assert.DoesNotContain("synthetic-diagnostic-secret", request.Body);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(json, body.RootElement.TryGetProperty("response_format", out _));
        Assert.Equal(ModelId, body.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("kind")]
    public async Task ConfigurationMismatchSendsNothing(string changed)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Handler.Requests.Clear();
        var configuration = changed switch
        {
            "endpoint" => f.Configuration with { Endpoint = "http://127.0.0.1:43211" },
            "model" => f.Configuration with { Model = "different-model" },
            _ => f.Configuration with { Kind = AiProviderKind.OpenAi },
        };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            f.Provider.RunTurnAsync(f.Request with { Configuration = configuration }, [], NoTool, default));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongOrMissingResponseIdentityCannotExecuteTools(bool missing)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        var response = JsonReply(null, new() { Id = "call1", Name = "stop_container" }, model: "different-model");
        if (missing) response = response.Replace("\"model\":\"different-model\",", "", StringComparison.Ordinal);
        f.Handler.Completions.Enqueue(response);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Provider.RunTurnAsync(f.Request, Tools, NoTool, default));
    }

    [Fact]
    public async Task RuntimeReplacementWithdrawsProofWithoutInference()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Handler.RuntimeMarker = "replacement";
        await Assert.ThrowsAsync<AiProviderException>(() => f.Provider.RunTurnAsync(f.Request, [], NoTool, default));
        Assert.Null(f.Capabilities.Snapshot);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(false, "invalidated")]
    [InlineData(false, "replacement")]
    [InlineData(true, "disabled")]
    [InlineData(true, "invalidated")]
    [InlineData(true, "replacement")]
    public async Task InFlightInventoryCannotResurrectWithdrawnCapabilityProof(bool chat, string change)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Handler.Requests.Clear();
        f.Handler.BeforeSend = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/openai/loadedmodels") return;
            await Task.Yield();
            if (change == "disabled") f.Values[nameof(ISettingsService.AiFeaturesEnabled)] = false;
            else if (change == "replacement") f.Capabilities.Snapshot = f.Capabilities.Snapshot! with { };
            else f.Capabilities.Invalidate();
        };
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            if (chat) await f.Provider.RunTurnAsync(f.Request, [], NoTool, default);
            else await f.Provider.CompleteAsync(new("Return JSON", "Synthetic question"), default);
        });
        Assert.DoesNotContain(f.Handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratingCallbackCannotSendAfterAiDisableOrProofInvalidation(bool disable)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Handler.Requests.Clear();
        var request = f.Request with
        {
            Progress = progress =>
            {
                if (progress.Kind != AiChatProgressKind.Generating) return;
                if (disable) f.Values[nameof(ISettingsService.AiFeaturesEnabled)] = false;
                else f.Capabilities.Invalidate();
            },
        };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Provider.RunTurnAsync(request, [], NoTool, default));
        Assert.DoesNotContain(f.Handler.Requests, sent => sent.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UnloadedModelBlocksInsteadOfImplicitWarmup()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Handler.IsLoaded = false;
        await Assert.ThrowsAsync<AiProviderException>(() => f.Provider.RunTurnAsync(f.Request, [], NoTool, default));
        Assert.DoesNotContain(f.Handler.Requests, r => r.Method == HttpMethod.Post || r.Uri.AbsolutePath.Contains("/load/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OriginalToolArgumentsReachCallbackOnlyRedactedCopiesEnterHistory()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        const string arguments = """{"id":"approved-id","password":"synthetic-tool-secret"}""";
        f.Handler.Completions.Enqueue(JsonReply(null, new() { Id = "call1", Name = "stop_container", ArgumentsJson = arguments }));
        f.Handler.Completions.Enqueue(JsonReply("Done"));
        var invoked = 0;
        var result = await f.Provider.RunTurnAsync(f.Request, Tools, (call, _) =>
        {
            invoked++;
            Assert.Equal(arguments, call.ArgumentsJson);
            return Task.FromResult("password: synthetic-result-secret");
        }, default);
        Assert.Equal(1, invoked);
        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("call1", result.Messages[1].ToolCallId);
        Assert.DoesNotContain("synthetic-tool-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("synthetic-result-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("synthetic-tool-secret", f.Handler.Requests.Last().Body);
        Assert.DoesNotContain("synthetic-result-secret", f.Handler.Requests.Last().Body);
    }

    [Fact]
    public async Task RealAssistantKeepsOriginalApprovalClosureAndStructuredJournal()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        var h = new AiContractHarness(providerFactory: _ => [f.Provider]);
        h.SettingsValues[nameof(ISettingsService.AiProvider)] = AiProviderKind.FoundryLocal;
        h.SettingsValues[nameof(ISettingsService.AiFoundryLocalEndpoint)] = Endpoint;
        h.SettingsValues[nameof(ISettingsService.AiFoundryLocalModel)] = ModelId;
        f.Handler.Completions.Enqueue(JsonReply(null, new() { Id = "approve1", Name = "stop_container",
            ArgumentsJson = """{"id":"approved-id","password":"original-secret"}""" }));
        f.Handler.Completions.Enqueue(JsonReply("Complete"));
        var requested = AiContractHarness.Signal<AssistantApprovalRequest>();
        h.Assistant.ApprovalChanged += (_, approval) => { if (approval is not null) requested.TrySetResult(approval); };
        var turn = h.Assistant.SendAsync("Stop the container");
        var pending = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Tools.Executed);
        await h.Assistant.ApproveAsync(pending);
        var completed = await turn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(h.Tools.Executed);
        Assert.Contains("original-secret", h.Tools.Executed[0].ArgumentsJson);
        Assert.DoesNotContain("original-secret", JsonSerializer.Serialize(completed));
        Assert.DoesNotContain("original-secret", string.Join("\n", h.PersistedActivity));
    }

    [Fact]
    public async Task IncompleteStreamingToolCallDoesNotExecuteOrRetry()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Capabilities.Snapshot = f.Capabilities.Snapshot! with { Streaming = new(AiSupport.Supported) };
        f.Handler.Completions.Enqueue("""data: {"model":"synthetic-local-model:7","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"pending","type":"function","function":{"name":"stop_container","arguments":"{}"}}]},"finish_reason":null}]}""" + "\n\n");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            f.Provider.RunTurnAsync(f.Request with { Progress = _ => { } }, Tools, NoTool, default));
        Assert.Single(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CallerCancellationStopsMetadataWithoutInference()
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        f.Handler.BeforeSend = (_, _) => { cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Runtime.ReadInventoryAsync(f.Configuration, cancel.Token));
        Assert.DoesNotContain(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ExplicitUnloadUsesOnlyDocumentedSelectedModelRouteWithoutForce()
    {
        using var f = new Fixture();
        var invalidations = 0;
        f.Runtime.StateChanged += () => invalidations++;
        var unloaded = await f.Runtime.UnloadAsync(f.Configuration, default);
        Assert.True(unloaded.IsConfirmed);
        Assert.Equal(LocalRuntimeResourceState.Absent, unloaded.Memory);
        Assert.Equal(LocalRuntimeResourceState.Retained, unloaded.ModelData);
        Assert.Equal(2, invalidations);
        var mutation = Assert.Single(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("/unload/", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Get, mutation.Method);
        Assert.EndsWith(Uri.EscapeDataString(ModelId), mutation.Uri.AbsolutePath);
        Assert.Empty(mutation.Uri.Query);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("/load/", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("download", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "ONNX")]
    [InlineData(true, "external")]
    [InlineData(true, "ONNX")]
    public async Task AllAssetsBlockLoadWithoutAnyHttpRequest(bool cached, string type)
    {
        using var f = new Fixture();
        f.Handler.IsCached = cached;
        f.Handler.ModelType = type;
        var result = await f.Runtime.LoadAsync(f.Configuration, default);
        Assert.False(result.IsConfirmed);
        Assert.Contains("at least seven days", result.Guidance);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.Memory);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.ModelData);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task CancellationAfterMutationDoesNotAssertRollbackAndInvalidatesProof()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Runtime.StateChanged += f.Capabilities.Invalidate;
        using var cancel = new CancellationTokenSource();
        f.Handler.AfterMutation = () => cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Runtime.UnloadAsync(f.Configuration, cancel.Token));
        Assert.False(f.Handler.IsLoaded); // The completed server change was NOT rolled back.
        Assert.Null(f.Capabilities.Snapshot);
    }

    [Fact]
    public async Task UnloadResponseWithoutObservedStateIsNotSuccess()
    {
        using var f = new Fixture();
        f.Handler.ApplyMutations = false;
        var result = await f.Runtime.UnloadAsync(f.Configuration, default);
        Assert.False(result.IsConfirmed);
        Assert.Equal(LocalRuntimeResourceState.Retained, result.Memory);
    }

    [Fact]
    public async Task EndpointChangeDuringRefreshCannotSendMutationToNewDestination()
    {
        using var f = new Fixture();
        f.Handler.BeforeSend = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/openai/loadedmodels")
                f.Values[nameof(ISettingsService.AiFoundryLocalEndpoint)] = "http://127.0.0.1:43211";
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Runtime.UnloadAsync(f.Configuration, default));
        Assert.DoesNotContain(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("/unload/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsAreSeparatePersistentAndConstructingUiDoesNotStartRuntime()
    {
        using var f = new Fixture();
        var vm = f.CreateViewModel();
        Assert.Empty(f.Handler.Requests);
        vm.Endpoint = "http://127.0.0.1:43211";
        vm.Model = "another-actual-id";
        var reopened = f.CreateViewModel();
        Assert.Equal(vm.Endpoint, reopened.Endpoint);
        Assert.Equal(vm.Model, reopened.Model);
        Assert.Equal("https://cloud.invalid/v1", f.Settings.AiOpenAiEndpoint);
        Assert.Equal("cloud-model", f.Settings.AiOpenAiModel);
        Assert.Equal(2, f.Saves);
        await reopened.RunOperationCommand.ExecuteAsync("refresh");
        Assert.Contains("No load, download or inference", reopened.Status);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Method != HttpMethod.Get);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("nested/model")]
    [InlineData("nested\\model")]
    [InlineData("bad\nmodel")]
    public async Task MissingOrRouteUnsafeModelNeverSendsRequest(string id)
    {
        using var f = new Fixture();
        f.Values[nameof(ISettingsService.AiFoundryLocalModel)] = id;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Runtime.LoadAsync(f.Configuration, default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Provider.RunTurnAsync(f.Request, [], NoTool, default));
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task MissingEndpointDoesNotBorrowOpenAiConfiguration()
    {
        using var f = new Fixture();
        f.Values[nameof(ISettingsService.AiFoundryLocalEndpoint)] = "";
        var state = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        Assert.Equal(AiEndpointState.InvalidConfiguration, state.Endpoint);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Provider.CompleteAsync(new("test", "test"), default));
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task CancelledGenerationDoesNotExecuteOrRetry()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = AiContractHarness.Signal<bool>();
        f.Handler.BeforeSend = async (request, token) =>
        {
            if (request.Method != HttpMethod.Post) return;
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var turn = f.Provider.RunTurnAsync(f.Request, Tools, NoTool, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ConfigurationChangeAfterGenerationPreventsToolCallback()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Handler.Completions.Enqueue(JsonReply(null, new() { Id = "pending", Name = "stop_container" }));
        f.Handler.BeforeSend = (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                f.Values[nameof(ISettingsService.AiFoundryLocalModel)] = "changed-model";
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Provider.RunTurnAsync(f.Request, Tools, NoTool, default));
        Assert.Single(f.Handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task IterationLimitDoesNotRepeatCompletedActions()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        for (var i = 0; i < 8; i++)
            f.Handler.Completions.Enqueue(JsonReply(null, new() { Id = "call" + i, Name = "stop_container" }));
        var calls = new HashSet<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Provider.RunTurnAsync(f.Request, Tools, (call, _) =>
            {
                Assert.True(calls.Add(call.Id));
                return Task.FromResult("completed");
            }, default));
        Assert.Contains("iteration", error.Message);
        Assert.Equal(8, calls.Count);
        Assert.Equal(8, f.Handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task WrongStreamingResponseModelDoesNotPublishText()
    {
        using var f = new Fixture();
        await f.ProveAsync();
        f.Capabilities.Snapshot = f.Capabilities.Snapshot! with { Streaming = new(AiSupport.Supported) };
        f.Handler.Completions.Enqueue(SseReply("Untrusted").Replace(ModelId, "wrong-model", StringComparison.Ordinal));
        var events = new List<AiChatProgress>();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            f.Provider.RunTurnAsync(f.Request with { Progress = events.Add }, [], NoTool, default));
        Assert.DoesNotContain(events, e => e.Kind == AiChatProgressKind.TextDelta);
    }

    [Fact]
    public void PersistedFoundryConfigurationRoundTripsWithoutChangingOtherProviders()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foundry-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsService>.Instance;
            var original = new SettingsService(logger, directory)
            {
                AiProvider = AiProviderKind.FoundryLocal,
                AiFoundryLocalEndpoint = Endpoint,
                AiFoundryLocalModel = ModelId,
                AiOpenAiEndpoint = "https://cloud.invalid/v1",
                AiOpenAiModel = "cloud-model",
                AiOllamaEndpoint = "http://ollama.invalid:12345",
                AiOllamaModel = "ollama-model",
            };
            original.Save();
            var restored = new SettingsService(logger, directory);
            restored.Load();
            Assert.Equal(AiProviderKind.FoundryLocal, restored.AiProvider);
            Assert.Equal(Endpoint, restored.AiFoundryLocalEndpoint);
            Assert.Equal(ModelId, restored.AiFoundryLocalModel);
            Assert.Equal(original.AiOpenAiEndpoint, restored.AiOpenAiEndpoint);
            Assert.Equal(original.AiOpenAiModel, restored.AiOpenAiModel);
            Assert.Equal(original.AiOllamaEndpoint, restored.AiOllamaEndpoint);
            Assert.Equal(original.AiOllamaModel, restored.AiOllamaModel);
            Assert.DoesNotContain("ApiKey", File.ReadAllText(Path.Combine(directory, "settings.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void OlderSettingsKeepExistingEnumValuesAndNoFoundryDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foundry-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "settings.json"), """{"AiProvider":4,"AiOpenAiEndpoint":"https://cloud.invalid/v1","AiOpenAiModel":"existing-model"}""");
            var settings = new SettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsService>.Instance, directory);
            settings.Load();
            Assert.Equal(AiProviderKind.OpenAi, settings.AiProvider);
            Assert.Equal("existing-model", settings.AiOpenAiModel);
            Assert.Empty(settings.AiFoundryLocalEndpoint);
            Assert.Empty(settings.AiFoundryLocalModel);
            Assert.Equal(5, (int)AiProviderKind.FoundryLocal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AlreadyLoadedModelStillReturnsBlockedLoadNotAssumedAudit()
    {
        using var f = new Fixture();
        var result = await f.Runtime.LoadAsync(f.Configuration, default);
        Assert.False(result.IsConfirmed);
        Assert.Equal(LocalRuntimeResourceState.Unknown, result.Memory);
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task FailedToolResultRoundtripCannotEnableTools()
    {
        using var f = new Fixture();
        var metadata = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        f.Handler.Completions.Enqueue(JsonReply("OK"));
        f.Handler.Completions.Enqueue(JsonReply(null, new() { Id = "probe", Name = "capability_ack", ArgumentsJson = """{"ok":true}""" }));
        f.Handler.Completions.Enqueue(JsonReply("""{"ok":true}"""));
        f.Handler.Completions.Enqueue(JsonReply("I cannot process tool results."));
        f.Handler.Completions.Enqueue(SseReply("OK"));
        var result = await f.Observer.ProbeAsync(metadata, default);
        Assert.True(result.CanChat);
        Assert.False(result.CanUseTools);
        Assert.Equal(AiSupport.Unknown, result.Tools.Support);
    }

    [Fact]
    public async Task GenericOpenAiProviderCannotBypassFoundryClientPolicy()
    {
        using var f = new Fixture();
        using var ordinaryClient = new AiHttpClient(f.Handler);
        var provider = new OpenAiProvider(ordinaryClient, f.Settings, new AiContractHarness.Credentials("must-not-send"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RunTurnAsync(f.Request, [], NoTool, default));
        Assert.Empty(f.Handler.Requests);
    }

    [Theory]
    [InlineData(AiLoadState.Unknown)]
    [InlineData(AiLoadState.Unloaded)]
    [InlineData(AiLoadState.Loading)]
    public async Task FeatureProofAloneDoesNotEnableUnreadyFoundryRuntime(AiLoadState load)
    {
        using var f = new Fixture();
        await f.ProveAsync();
        var snapshot = f.Capabilities.Snapshot! with { Load = load };
        Assert.False(snapshot.CanChat);
        Assert.False(snapshot.CanUseTools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderSwitchBeforeLifecycleRequestBlocksAllIo(bool load)
    {
        using var f = new Fixture();
        f.Values[nameof(ISettingsService.AiProvider)] = AiProviderKind.OpenAi;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load
            ? f.Runtime.LoadAsync(f.Configuration, default) : f.Runtime.UnloadAsync(f.Configuration, default));
        Assert.Empty(f.Handler.Requests);
    }

    [Fact]
    public async Task ProviderSwitchCancelsInFlightUnloadMetadataAndUnsubscribes()
    {
        using var f = new Fixture();
        var entered = AiContractHarness.Signal<bool>();
        f.Handler.BeforeSend = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var operation = f.Runtime.UnloadAsync(f.Configuration, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Settings.AiProvider = AiProviderKind.OpenAi;
        f.Settings.Save();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(f.Handler.IsLoaded);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("/unload/", StringComparison.Ordinal));
        Assert.False(f.HasSettingsSubscriptions);
    }

    [Fact]
    public async Task ProviderSwitchAfterUnloadCannotClaimRollbackOrPublishSuccess()
    {
        using var f = new Fixture();
        f.Handler.AfterMutation = () =>
        {
            f.Settings.AiProvider = AiProviderKind.OpenAi;
            f.Settings.Save();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Runtime.UnloadAsync(f.Configuration, default));
        Assert.False(f.Handler.IsLoaded); // Server action already completed; no rollback claim.
        Assert.False(f.HasSettingsSubscriptions);
    }

    [Fact]
    public async Task ViewModelProviderChangeCancelsAndDoesNotPublishStaleFeedback()
    {
        using var f = new Fixture();
        var vm = f.CreateViewModel();
        var entered = AiContractHarness.Signal<bool>();
        f.Handler.BeforeSend = async (_, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var operation = vm.RunOperationCommand.ExecuteAsync("unload");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Settings.AiProvider = AiProviderKind.OpenAi;
        vm.OnProviderChanged();
        var status = vm.Status;
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status, vm.Status);
        Assert.Contains("Provider changed", vm.InventoryText);
        Assert.Contains(f.Logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.DoesNotContain(f.Handler.Requests, r => r.Uri.AbsolutePath.Contains("/unload/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ViewModelLoadCommandCannotBypassDisabledUiOrClaimReadiness()
    {
        using var f = new Fixture();
        var vm = f.CreateViewModel();
        await vm.RunOperationCommand.ExecuteAsync("load");
        Assert.Contains("Load blocked", vm.Status);
        Assert.Empty(f.Handler.Requests);
        Assert.Contains("Not refreshed", vm.InventoryText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewModelFailuresAreSanitizedLoggedAndSurfaced(bool unexpected)
    {
        using var f = new Fixture();
        f.Handler.BeforeSend = (_, _) => throw (unexpected
            ? new InvalidOperationException("password: synthetic-vm-secret")
            : new HttpRequestException("password: synthetic-vm-secret"));
        var vm = f.CreateViewModel();
        await vm.RunOperationCommand.ExecuteAsync("refresh");
        Assert.Contains("No fallback or acquisition", vm.Status);
        if (unexpected) Assert.Contains("Unexpected Foundry Local error", vm.Status);
        var entry = Assert.Single(f.Logger.Entries);
        Assert.Equal(unexpected ? LogLevel.Error : LogLevel.Debug, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("synthetic-vm-secret", entry.Text);
        Assert.DoesNotContain("synthetic-vm-secret", vm.Status);
    }

    [Fact]
    public async Task UnloadedCapabilityGuidanceRequiresExplicitPreparation()
    {
        using var f = new Fixture();
        f.Handler.IsLoaded = false;
        var snapshot = await f.Observer.ReadMetadataAsync(f.Configuration, default);
        Assert.Contains("current load evidence", snapshot.NextStep);
        Assert.Contains("initial-model setup", snapshot.NextStep);
        Assert.DoesNotContain("load the selected cached model in Foundry Local Settings", snapshot.NextStep);
    }

    [Fact]
    public void SourceContractsKeepExactGplHeadersConsentedLoadAndCredentialClearingOrder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WslContainerDesktop.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = Path.Combine(root.FullName, "src", "WslContainerDesktop");
        var header = File.ReadAllLines(Path.Combine(source, "Services", "OpenAiProvider.cs")).Take(15).ToArray();
        string[] newFiles =
        [
            "src/WslContainerDesktop/Models/FoundryLocalModels.cs",
            "src/WslContainerDesktop/Services/FoundryLocalEndpoint.cs",
            "src/WslContainerDesktop/Services/FoundryLocalRuntimeService.cs",
            "src/WslContainerDesktop/Services/IFoundryLocalRuntimeService.cs",
            "src/WslContainerDesktop/Services/FoundryLocalCapabilityObserver.cs",
            "src/WslContainerDesktop/Services/FoundryLocalProvider.cs",
            "src/WslContainerDesktop/ViewModels/FoundryLocalSettingsViewModel.cs",
            "tests/WslContainerDesktop.Tests/Services/FoundryLocalTests.cs",
            "tests/WslContainerDesktop.Tests/Services/FoundryLocalRuntimeOptInTests.cs",
        ];
        foreach (var file in newFiles)
            Assert.Equal(header, File.ReadAllLines(Path.Combine(root.FullName, file)).Take(15).ToArray());

        var viewModel = File.ReadAllText(Path.Combine(source, "ViewModels", "SettingsViewModel.cs"));
        var method = viewModel[viewModel.IndexOf("private void LoadStoredAiSecretIndicator()", StringComparison.Ordinal)..];
        var firstStatement = method[(method.IndexOf('{') + 1)..].Split('\n')
            .Select(line => line.Trim()).First(line => line.Length > 0);
        Assert.Equal("AiApiKey = string.Empty;", firstStatement);
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(source, "Views", "SettingsPage.xaml"));
        // Foundry Local is implemented but intentionally not exposed in Settings yet. If it is
        // re-exposed, restore the consent wiring this asserted: a Click handler that routes through
        // the preparation dialog, never a bare Command binding that could load without approval.
        Assert.DoesNotContain(page.Descendants(), e =>
            e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.AutomationId"
                && a.Value.StartsWith("FoundryLocal", StringComparison.Ordinal)));
    }

    private const string Endpoint = "http://127.0.0.1:43210";
    private const string ModelId = "synthetic-local-model:7";
    private static readonly AiToolDefinition[] Tools =
        [new() { Name = "stop_container", Description = "Stop synthetic container", JsonSchemaParameters = """{"type":"object"}""" }];
    private static Task<string> NoTool(AiToolCall _, CancellationToken __) => throw new InvalidOperationException("No tool execution expected.");

    private static string JsonReply(string? text, AiToolCall? call = null, string model = ModelId) =>
        JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new
            {
                index = 0, finish_reason = call is null ? "stop" : "tool_calls",
                message = new
                {
                    role = "assistant", content = text,
                    tool_calls = call is null ? Array.Empty<object>() :
                        new object[] { new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } } },
                },
            } },
        });
    private static string SseReply(string text) => "data: " + JsonSerializer.Serialize(new
    {
        model = ModelId, choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = "stop" } },
    }) + "\n\ndata: [DONE]\n\n";

    private sealed class Fixture : IDisposable
    {
        internal Dictionary<string, object?> Values { get; } = new()
        {
            [nameof(ISettingsService.AiFeaturesEnabled)] = true,
            [nameof(ISettingsService.AiProvider)] = AiProviderKind.FoundryLocal,
            [nameof(ISettingsService.AiFoundryLocalEndpoint)] = Endpoint,
            [nameof(ISettingsService.AiFoundryLocalModel)] = ModelId,
            [nameof(ISettingsService.AiOpenAiEndpoint)] = "https://cloud.invalid/v1",
            [nameof(ISettingsService.AiOpenAiModel)] = "cloud-model",
        };
        internal int Saves;
        private EventHandler? _settingsChanged;
        internal bool HasSettingsSubscriptions => _settingsChanged is not null;
        internal TestLogger Logger { get; } = new();
        internal ISettingsService Settings { get; }
        internal Handler Handler { get; } = new();
        internal FoundryLocalHttpClient Http { get; }
        internal FoundryLocalRuntimeService Runtime { get; }
        internal FoundryLocalCapabilityObserver Observer { get; }
        internal Cache Capabilities { get; } = new();
        internal FoundryLocalProvider Provider { get; }
        internal AiChatConfiguration Configuration => AiConversationContext.Capture(Settings, AiProviderKind.FoundryLocal);
        internal AiChatRequest Request => new(Configuration, [new() { Role = "user", Content = "Synthetic question" }]);
        internal Fixture()
        {
            Settings = NetworkTestProxy.Create<ISettingsService>((method, args) =>
            {
                if (method.Name == "Save") { Saves++; _settingsChanged?.Invoke(Settings, EventArgs.Empty); return null; }
                if (method.Name == "add_Changed") { _settingsChanged += (EventHandler)args[0]!; return null; }
                if (method.Name == "remove_Changed") { _settingsChanged -= (EventHandler)args[0]!; return null; }
                if (method.Name.StartsWith("get_", StringComparison.Ordinal)) return Values[method.Name[4..]];
                if (method.Name.StartsWith("set_", StringComparison.Ordinal)) { Values[method.Name[4..]] = args[0]; return null; }
                throw new InvalidOperationException("Unexpected settings dependency: " + method.Name);
            });
            Http = new(Handler);
            Runtime = new(Http, Settings);
            Observer = new(Runtime, Http);
            Provider = new(Http, Settings, Runtime, Capabilities);
        }
        internal FoundryLocalSettingsViewModel CreateViewModel() => new(Settings, Runtime, Capabilities, Logger);
        internal async Task ProveAsync()
        {
            Capabilities.Snapshot = await Observer.ReadMetadataAsync(Configuration, default);
            Capabilities.Snapshot = Capabilities.Snapshot with
            {
                Chat = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
                Tools = new(AiSupport.Supported, AiObservationSource.HarmlessProbe),
            };
        }
        public void Dispose() => Http.Dispose();
    }

    private sealed class Cache : IAiCapabilityService
    {
        internal AiCapabilitySnapshot? Snapshot;
        public AiCapabilitySnapshot GetCached(AiChatConfiguration configuration) => Snapshot ?? new(configuration);
        public Task<AiCapabilitySnapshot> GetAsync(AiChatConfiguration configuration, bool probe = false, CancellationToken ct = default) =>
            Task.FromResult(GetCached(configuration));
        public void Invalidate() => Snapshot = null;
    }

    private sealed class TestLogger : ILogger<FoundryLocalSettingsViewModel>
    {
        internal List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal bool IsCached = true;
        internal bool IsLoaded = true;
        internal bool ApplyMutations = true;
        internal bool ToolsAdvertised = true;
        internal string ModelType = "ONNX";
        internal string RuntimeMarker = "synthetic-runtime";
        internal string? CatalogOverride;
        internal HttpStatusCode StatusCode = HttpStatusCode.OK;
        internal List<AiContractHarness.CapturedRequest> Requests { get; } = [];
        internal Queue<string> Completions { get; } = new();
        internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeSend;
        internal Action? AfterMutation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!, request.Method, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (BeforeSend is not null) await BeforeSend(request, ct);
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var body = "";
            if (path == "/openai/status")
                body = JsonSerializer.Serialize(new { Endpoints = new[] { "https://never-follow.invalid:443" }, PipeName = RuntimeMarker });
            else if (path == "/foundry/list")
                body = CatalogOverride ?? JsonSerializer.Serialize(new { models = new[] { new
                {
                    name = ModelId, version = "7", task = "chat-completion", modelType = ModelType,
                    runtime = new { deviceType = "CPU", executionProvider = "synthetic-ep" },
                    fileSizeMb = 123, license = "synthetic-license", licenseDescription = "synthetic terms", supportsToolCalling = ToolsAdvertised,
                } } });
            else if (path == "/openai/models") body = JsonSerializer.Serialize(IsCached ? new[] { ModelId } : []);
            else if (path == "/openai/loadedmodels") body = JsonSerializer.Serialize(IsLoaded ? new[] { ModelId } : []);
            else if (path.StartsWith("/openai/unload/", StringComparison.Ordinal))
            {
                if (ApplyMutations) IsLoaded = false;
                AfterMutation?.Invoke();
            }
            else if (path == "/v1/chat/completions" && request.Method == HttpMethod.Post)
                body = Completions.Dequeue();
            else throw new InvalidOperationException("Unexpected route; no real HTTP fallback: " + path);
            return new(path == "/openai/status" ? StatusCode : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, body.StartsWith("data:", StringComparison.Ordinal) ? "text/event-stream" : "application/json"),
            };
        }
    }
}
