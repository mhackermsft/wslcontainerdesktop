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

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class FoundryLocalInitialSetupTests
{
    private static readonly AiChatConfiguration Configuration = new(AiProviderKind.FoundryLocal,
        "http://127.0.0.1:54321", FoundryLocalModelArtifacts.ModelId);

    [Fact]
    public async Task OneApprovedFlowRegistersReceiptVerifiedFilesLoadsAndReturnsReadyConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new FoundryLocalModelRegistrationTests.Fixture();
        await files.Stage(); // Synthetic registry/bytes; subsequent use must be entirely offline.
        using var live = new FoundryLocalLifecycleTests.Fixture { CachePath = files.Cache };
        var setup = new FoundryLocalInitialSetupService(live.Cli, live.Runtime,
            new(live.Cli, live.Runtime), files.Stager, files.Service);
        var confirmations = 0;
        var result = await setup.PrepareAsync(Configuration with { Endpoint = "", Model = "" },
            (_, _) => { confirmations++; return Task.FromResult(true); }, () => true, null, default);
        Assert.True(result.Success, result.Guidance);
        Assert.Equal(1, confirmations);
        Assert.Equal(FoundryLocalModelArtifacts.ModelId, result.Configuration!.Model);
        Assert.Equal("http://127.0.0.1:63150", result.Configuration.Endpoint);
        Assert.True(File.Exists(Path.Combine(files.Payload, "inference_model.json")));
        Assert.False(File.Exists(files.Sentinel));
        Assert.Equal("untouched", File.ReadAllText(files.ForeignFile));
        Assert.Equal(1, live.Posts);
        Assert.DoesNotContain(live.Commands, c => c.StartsWith("server start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclinePreservesExistingRuntimeAndDoesNotStageOrMutate()
    {
        using var f = new Fixture();
        var confirmations = 0;
        var result = await f.Setup.PrepareAsync(Configuration, (text, _) =>
        {
            confirmations++;
            Assert.Contains("877988985", text);
            Assert.Contains("Apache-2.0", text);
            Assert.Contains("neither selects, pins nor audits", text);
            Assert.Contains("five-minute", text);
            return Task.FromResult(false);
        }, () => true, null, default);
        Assert.False(result.Success);
        Assert.Equal(1, confirmations);
        Assert.Equal(["--version", "server status --output json"], f.Commands);
        Assert.False(Directory.Exists(f.Root));
    }

    [Fact]
    public async Task InvalidatedOriginalApprovalPreventsFileAcquisition()
    {
        using var f = new Fixture();
        var current = true;
        var result = await f.Setup.PrepareAsync(Configuration, (_, _) =>
        {
            current = false;
            return Task.FromResult(true);
        }, () => current, null, default);
        Assert.False(result.Success);
        Assert.False(Directory.Exists(f.Root));
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("model ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedServerStopRequiresSpecificApprovalAndVerifiesStoppedState()
    {
        using var f = new Fixture();
        var result = await f.Setup.StopAsync(Configuration, (text, _) =>
        {
            Assert.Contains("123", text);
            Assert.Contains("all clients", text);
            Assert.Contains("No server is claimed as app-owned", text);
            return Task.FromResult(true);
        }, () => true, default);
        Assert.True(result.Success);
        Assert.Equal(1, f.Commands.Count(c => c == "server stop --output json"));
    }

    [Fact]
    public async Task SharedServerReplacementDuringDialogIsNotStopped()
    {
        using var f = new Fixture();
        var result = await f.Setup.StopAsync(Configuration, (_, _) =>
        {
            f.Pid = 456;
            return Task.FromResult(true);
        }, () => true, default);
        Assert.False(result.Success);
        Assert.DoesNotContain("server stop --output json", f.Commands);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "foundry-consent-" + Guid.NewGuid().ToString("N"));
        internal readonly List<string> Commands = [];
        internal int Pid = 123;
        private bool _running = true;
        private readonly FoundryLocalModelArtifacts _artifacts;
        internal FoundryLocalInitialSetupService Setup { get; }
        internal Fixture()
        {
            var cli = new FoundryLocalCli(() => @"C:\Fixture\foundry.exe", (start, _) =>
            {
                var command = string.Join(" ", start.ArgumentList);
                Commands.Add(command);
                var output = command switch
                {
                    "--version" => "0.10.3",
                    "server status --output json" => JsonSerializer.Serialize(new
                    {
                        running = _running, pid = Pid, startedAt = "2026-09-10T19:10:30Z",
                        webUrls = new[] { Configuration.Endpoint },
                    }),
                    "server stop --output json" => Stop(),
                    _ => throw new Xunit.Sdk.XunitException("Unexpected mutation: " + command),
                };
                return Task.FromResult(new CommandResult { StandardOutput = output });
            });
            var http = new FoundryLocalHttpClient(this);
            var runtime = new FoundryLocalStandaloneRuntimeService(http, cli);
            _artifacts = new(Root, this, FoundryLocalModelArtifacts.PinnedFiles, () => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            Setup = new(cli, runtime, new(cli, runtime), _artifacts, new FoundryLocalModelRegistration());
        }
        private string Stop() { _running = false; return """{"success":true,"message":"Server stopped."}"""; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new Xunit.Sdk.XunitException("No HTTP is authorized by this fixture");
    }
}
