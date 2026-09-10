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

public sealed class FoundryLocalLifecycleTests
{
    private static readonly AiChatConfiguration Configuration = new(AiProviderKind.FoundryLocal,
        "http://127.0.0.1:63150", FoundryLocalModelArtifacts.ModelId);

    [Fact]
    public async Task RecordedLoadAndDualMessageCompletionEstablishCanonicalVersionProof()
    {
        using var f = new Fixture();
        var before = await f.Runtime.ReadInventoryAsync(Configuration, default);
        Assert.Equal(FoundryLocalModelArtifacts.ModelId, before.Selected!.Id);
        Assert.False(before.IsLoaded);
        Assert.False(before.LoadStateKnown);
        var result = await f.Runtime.LoadRegisteredAsync(Configuration, null, default);
        Assert.True(result.IsConfirmed);
        var after = await f.Runtime.ReadInventoryAsync(Configuration, default);
        Assert.True(after.IsLoaded);
        Assert.True(after.IsCached);
        Assert.Equal("ONNX", after.Selected!.ModelType);
        Assert.Equal(1, f.Posts);
        Assert.Contains("model load " + Configuration.Model + " --output json", f.Commands);
        var unloaded = await f.Runtime.UnloadAsync(Configuration, default);
        Assert.True(unloaded.IsConfirmed);
        Assert.False((await f.Runtime.ReadInventoryAsync(Configuration, default)).IsLoaded);
        Assert.Contains("model unload " + Configuration.Model + " --output json", f.Commands);
    }

    [Fact]
    public async Task ReplacementProcessCannotBecomeAFreshLoadBaseline()
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Runtime.LoadRegisteredAsync(Configuration, null, default,
                expectedRuntimeIdentity: "different-prepared-runtime-identity"));
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("model load", StringComparison.Ordinal));
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task RestartAfterLoadAcknowledgementNeverSendsSyntheticInference()
    {
        using var f = new Fixture { RestartAfterLoad = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Runtime.LoadRegisteredAsync(Configuration, null, default));
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task WithdrawnApprovalAfterLoadPreventsSyntheticPost()
    {
        using var f = new Fixture();
        var current = true;
        var progress = new InlineProgress(text =>
        {
            if (text.StartsWith("Verifying", StringComparison.Ordinal)) current = false;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Runtime.LoadRegisteredAsync(Configuration, progress, default, () => current));
        Assert.Equal(0, f.Posts);
        Assert.False((await f.Runtime.ReadInventoryAsync(Configuration, default)).IsLoaded);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task WrongCompletionVersionCannotEstablishReadiness()
    {
        using var f = new Fixture { WrongCompletion = true };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Runtime.LoadRegisteredAsync(Configuration, null, default));
        Assert.False((await f.Runtime.ReadInventoryAsync(Configuration, default)).IsLoaded);
    }

    [Fact]
    public async Task UnloadDoesNotAdoptAnExternallyLoadedModel()
    {
        using var f = new Fixture();
        Assert.False((await f.Runtime.UnloadAsync(Configuration, default)).IsConfirmed);
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("model ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":false}")]
    [InlineData("{\"success\":true,\"success\":false}")]
    [InlineData("{\"success\":\"true\"}")]
    public void UnknownMutationSchemaRejected(string body) =>
        Assert.Throws<InvalidDataException>(() => FoundryLocalCli.RequireSuccessfulMutation(body));

    [Fact]
    public async Task RecordedStartAndStopCommandsUseExactArgumentLists()
    {
        using var f = new Fixture();
        await f.Cli.StartServerAsync(default);
        await f.Cli.StopServerAsync(default);
        Assert.Contains("server start --port 0 --idle-timeout 5 --output json", f.Commands);
        Assert.Contains("server stop --output json", f.Commands);
        Assert.DoesNotContain(f.Commands, c => c.Contains("download", StringComparison.Ordinal));
    }

    internal sealed class Fixture : HttpMessageHandler
    {
        private readonly JsonDocument _recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Foundry", "0.10.3", "networked-lifecycle.json")));
        private readonly FoundryLocalHttpClient _http;
        private bool _loaded;
        internal readonly List<string> Commands = [];
        internal bool RestartAfterLoad;
        internal bool WrongCompletion;
        internal int Posts;
        internal string? CachePath;
        internal FoundryLocalCli Cli { get; }
        internal FoundryLocalStandaloneRuntimeService Runtime { get; }

        internal Fixture()
        {
            _http = new(this);
            Cli = new(() => @"C:\Fixture\foundry.exe", (start, _) =>
            {
                var command = string.Join(" ", start.ArgumentList);
                Commands.Add(command);
                var body = command switch
                {
                    "--version" => "0.10.3",
                    "--help" => "Commands:\n  cache  Cache commands\n",
                    "cache --help" => "Commands:\n  location  Show location\n",
                    "cache location --output json" => JsonSerializer.Serialize(new { path = CachePath, userSet = false }),
                    "server status --output json" => JsonSerializer.Serialize(new
                    {
                        running = true, pid = RestartAfterLoad && _loaded ? 456 : 123,
                        startedAt = "2026-09-10T19:10:30Z", webUrls = new[] { Configuration.Endpoint },
                    }),
                    "server start --port 0 --idle-timeout 5 --output json" => Read("start"),
                    "server stop --output json" => Read("stop"),
                    _ when command == "model load " + Configuration.Model + " --output json" => Loaded(),
                    _ when command == "model unload " + Configuration.Model + " --output json" => Read("unload"),
                    _ => throw new Xunit.Sdk.XunitException("Unexpected CLI operation: " + command),
                };
                return Task.FromResult(new CommandResult { StandardOutput = body });
            });
            Runtime = new(_http, Cli);
        }

        private string Loaded() { _loaded = true; return Read("load"); }
        private string Read(string name) => _recorded.RootElement.GetProperty(name).GetString()!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains(request.RequestUri!.AbsolutePath, new[] { "/v1/models", "/v1/chat/completions" });
            if (request.Method == HttpMethod.Post) Posts++;
            var body = request.Method == HttpMethod.Get ? Read("models") : Read("completion");
            if (WrongCompletion && request.Method == HttpMethod.Post)
                body = body.Replace(Configuration.Model, FoundryLocalModelArtifacts.CatalogId, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _recorded.Dispose();
            base.Dispose(disposing);
        }
    }
}
