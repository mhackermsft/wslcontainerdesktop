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
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Synthetic standard-v1 edge tests, not live load/cache compatibility proof.</summary>
public sealed class FoundryLocalStandaloneTests
{
    private static readonly AiChatConfiguration Configuration = new(AiProviderKind.FoundryLocal, "http://127.0.0.1:54321", "synthetic");
    private const string Running = """{"running":true,"pid":123,"startedAt":"2026-09-10T12:00:00Z","webUrls":["http://127.0.0.1:54321"]}""";

    private static FoundryLocalCli Cli(Func<string> status) => new(() => @"C:\Fixture\foundry.exe", (start, _) =>
    {
        if (start.ArgumentList.SequenceEqual(["--version"])) return Task.FromResult(new CommandResult { StandardOutput = "0.10.3" });
        Assert.Equal(["server", "status", "--output", "json"], start.ArgumentList);
        return Task.FromResult(new CommandResult { StandardOutput = status() });
    });

    [Fact]
    public async Task StandardModelListingDoesNotInventCachedLoadedOrToolProof()
    {
        using var handler = new ModelsHandler();
        using var http = new FoundryLocalHttpClient(handler);
        var runtime = new FoundryLocalStandaloneRuntimeService(http, Cli(() => Running));
        var inventory = await runtime.ReadInventoryAsync(Configuration, default);
        Assert.Single(inventory.Catalog);
        Assert.False(inventory.CacheStateKnown);
        Assert.False(inventory.LoadStateKnown);
        var observer = new FoundryLocalCapabilityObserver(runtime, http);
        var metadata = await observer.ReadMetadataAsync(Configuration, default);
        Assert.Equal(AiLoadState.Unknown, metadata.Load);
        Assert.Equal(AiDownloadState.Unknown, metadata.Download);
        Assert.Equal(AiModelState.Unknown, metadata.Model);
        Assert.False(metadata.CanChat);
        Assert.Equal(metadata, await observer.ProbeAsync(metadata, default));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task StoppedRuntimeNeverContactsStaleEndpoint()
    {
        using var handler = new ModelsHandler();
        using var http = new FoundryLocalHttpClient(handler);
        var runtime = new FoundryLocalStandaloneRuntimeService(http, Cli(() => Running.Replace("true", "false", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ReadInventoryAsync(Configuration, default));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RestartDuringModelReadInvalidatesObservation()
    {
        using var handler = new ModelsHandler();
        using var http = new FoundryLocalHttpClient(handler);
        var count = 0;
        var runtime = new FoundryLocalStandaloneRuntimeService(http, Cli(() => ++count == 1 ? Running : Running.Replace("123", "456", StringComparison.Ordinal)));
        var invalidated = false;
        runtime.StateChanged += () => invalidated = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ReadInventoryAsync(Configuration, default));
        Assert.True(invalidated);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void RuntimeIdentityIgnoresUptimeButChangesWithProcessStart()
    {
        var first = FoundryLocalCli.ParseServerStatus(Running);
        var later = FoundryLocalCli.ParseServerStatus(Running.Replace("}", ",\"uptime\":\"9h\"}", StringComparison.Ordinal));
        Assert.Equal(FoundryLocalStandaloneRuntimeService.RuntimeIdentity(first, Configuration.Endpoint),
            FoundryLocalStandaloneRuntimeService.RuntimeIdentity(later, Configuration.Endpoint));
        Assert.NotEqual(FoundryLocalStandaloneRuntimeService.RuntimeIdentity(first, Configuration.Endpoint),
            FoundryLocalStandaloneRuntimeService.RuntimeIdentity(first with { StartedAt = first.StartedAt!.Value.AddSeconds(1) }, Configuration.Endpoint));
    }

    [Theory]
    [InlineData("""{"models":[]}""")]
    [InlineData("""{"data":[{"id":"x"},{"id":"x"}]}""")]
    [InlineData("""{"data":[{"id":""}]}""")]
    [InlineData("""{"data":[{"id":"x","id":"y"}]}""")]
    public void UnknownModelSchemasAreRejected(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Throws<InvalidDataException>(() => FoundryLocalStandaloneRuntimeService.ParseModelIds(json.RootElement));
    }

    private sealed class ModelsHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"object":"list","data":[{"id":"synthetic","object":"model"}]}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
