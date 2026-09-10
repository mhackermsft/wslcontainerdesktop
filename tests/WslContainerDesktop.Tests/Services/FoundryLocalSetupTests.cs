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
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Source-linked real adapters with captured processes, never live Foundry/winget/HTTP.
/// Synthetic help/text fixtures test fail-closed behavior, not version-locked CLI compatibility.
/// Command guidance: learn.microsoft.com/azure/foundry-local/reference/reference-cli.
/// </summary>
public sealed class FoundryLocalSetupTests
{
    [Theory]
    [InlineData("server")]
    [InlineData("service")]
    public async Task RealAdapterOnlyUsesAdvertisedReadOnlyCommandsAndArgumentList(string group)
    {
        var calls = new List<ProcessStartInfo>();
        using var cts = new CancellationTokenSource();
        var cli = new FoundryLocalCli(() => @"C:\Program Files\Foundry Local\foundry.exe", (start, token) =>
        {
            Assert.Equal(cts.Token, token);
            calls.Add(start);
            return Task.FromResult(Ok(calls.Count switch
            {
                1 => "Foundry Local synthetic-version",
                2 => $"Commands:\n  {group}  Manage local server\n  model  Models",
                3 => "Commands:\n  status  Display server status",
                _ => "Server running\nEndpoint: http://127.0.0.1:54321",
            }));
        });

        var result = await cli.DiscoverAsync(null, cts.Token);
        Assert.Equal("http://127.0.0.1:54321", result.Endpoint);
        Assert.Equal(4, calls.Count);
        Assert.Equal(["--version"], calls[0].ArgumentList);
        Assert.Equal(["--help"], calls[1].ArgumentList);
        Assert.Equal([group, "--help"], calls[2].ArgumentList);
        Assert.Equal([group, "status"], calls[3].ArgumentList);
        Assert.All(calls, start =>
        {
            Assert.Equal(@"C:\Program Files\Foundry Local\foundry.exe", start.FileName);
            Assert.Equal("", start.Arguments);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.True(start.RedirectStandardOutput);
            Assert.True(start.RedirectStandardError);
            Assert.False(start.RedirectStandardInput);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("foundry.exe")]
    [InlineData(@"C:\arbitrary.exe")]
    [InlineData(@"\\remote.invalid\share\foundry.exe")]
    public async Task MissingOrRelativeExecutableNeverRunsAnything(string path)
    {
        var cli = new FoundryLocalCli(() => path, (_, _) => throw new Xunit.Sdk.XunitException("Must not spawn"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.DiscoverAsync(null, default));
    }

    [Theory]
    [InlineData("server stopped")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://remote.invalid:54321")]
    [InlineData("http://127.0.0.1:1234 http://127.0.0.1:5678")]
    [InlineData("http://127.0.0.1:1234?token=synthetic-secret")]
    [InlineData("http://127.0.0.1:1234/unknown")]
    public void DiscoveryDoesNotInferOrFollowAmbiguousOrUnsafeEndpoint(string output) =>
        Assert.ThrowsAny<Exception>(() => FoundryLocalCli.ParseEndpoint(output));

    [Theory]
    [InlineData("http://localhost:54321")]
    [InlineData("https://[::1]:54321/v1")]
    public void DiscoveryPreservesActualExplicitEndpoint(string endpoint) =>
        Assert.Equal(endpoint, FoundryLocalCli.ParseEndpoint($"Endpoint: {endpoint}\n"));

    [Fact]
    public async Task StatusFailureNeverRetriesOrExposesProcessDiagnostics()
    {
        var calls = 0;
        var cli = new FoundryLocalCli(() => @"C:\Foundry\foundry.exe", (_, _) => Task.FromResult(++calls switch
        {
            1 => Ok("synthetic-version"),
            2 => Ok("  server Server\n  service Legacy"),
            3 => Ok("  status Display server status"),
            _ => new CommandResult { ExitCode = 1, StandardError = "synthetic-sensitive-process-output" },
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cli.DiscoverAsync(null, default));
        Assert.Equal(4, calls);
        Assert.DoesNotContain("synthetic-sensitive", error.Message);
    }

    [Fact]
    public async Task UnknownHelpDoesNotGuessFromVersion()
    {
        var calls = 0;
        var cli = new FoundryLocalCli(() => @"C:\Foundry\foundry.exe", (_, _) =>
            Task.FromResult(Ok(++calls == 1 ? "99.0.0" : "  model Model commands")));
        await Assert.ThrowsAsync<InvalidDataException>(() => cli.DiscoverAsync(null, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GroupWithoutAdvertisedStatusDoesNotInferSubcommandFromVersion()
    {
        var calls = new List<string[]>();
        var cli = new FoundryLocalCli(() => @"C:\Foundry\foundry.exe", (start, _) =>
        {
            calls.Add(start.ArgumentList.ToArray());
            return Task.FromResult(Ok(calls.Count switch
            {
                1 => "0.10.3.0",
                2 => "  server Server commands",
                _ => "  start Start server\n  stop Stop server",
            }));
        });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => cli.DiscoverAsync(null, default));
        Assert.Contains("does not advertise status", error.Message);
        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain(calls, arguments => arguments.Contains("status"));
    }

    [Fact]
    public async Task CancelledDiscoveryDoesNotStartNextCommand()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var cli = new FoundryLocalCli(() => @"C:\Foundry\foundry.exe", (_, _) =>
        {
            calls++;
            cts.Cancel();
            return Task.FromResult(Ok("synthetic-version"));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cli.DiscoverAsync(null, cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UncancelledDiscoveryDoesNotWriteSettingsOrInferModelReadiness()
    {
        var fixture = new Fixture();
        Assert.Equal(0, fixture.ProcessCalls);
        Assert.Equal(0, fixture.MetadataCalls);
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        Assert.Equal(4, fixture.ProcessCalls);
        Assert.Equal(1, fixture.MetadataCalls);
        Assert.Equal("", fixture.ViewModel.Endpoint);
        Assert.Equal(0, fixture.Saves);
        Assert.True(fixture.ViewModel.CanUseDiscoveredEndpoint);
        Assert.Contains("synthetic-exact-model", fixture.ViewModel.SetupStatus);
        Assert.Contains("http://127.0.0.1:54321", fixture.ViewModel.SetupStatus);
        Assert.Contains("not an artifact audit", fixture.ViewModel.SetupStatus);
        Assert.False(fixture.Setup.CanInstall);
        Assert.Contains("no eligible independently audited", fixture.ViewModel.InstallationGuidance);
        Assert.Contains("user-entered audit claims", fixture.ViewModel.InstallationGuidance);
    }

    [Fact]
    public async Task ExactlyOneConfirmationAppliesOnlyEndpointPreservingModelAndOtherProviders()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        var confirmations = 0;
        await fixture.ViewModel.UseDiscoveredEndpointAsync(message =>
        {
            confirmations++;
            Assert.Contains("synthetic-exact-model", message);
            Assert.Contains("synthetic-license", message);
            Assert.Contains("Advertised size MB: 100", message);
            Assert.Contains("Network:", message);
            Assert.Contains("No software, model or EP", message);
            return Task.FromResult(true);
        });
        Assert.Equal(1, confirmations);
        Assert.Equal("http://127.0.0.1:54321", fixture.ViewModel.Endpoint);
        Assert.Equal("synthetic-exact-model", fixture.ViewModel.Model);
        Assert.Equal("unchanged-ollama", fixture.Values["AiOllamaModel"]);
        Assert.Equal(1, fixture.Saves);
        await fixture.ViewModel.UseDiscoveredEndpointAsync(_ => throw new Xunit.Sdk.XunitException("No second approval"));
        Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public async Task DeclinedConfirmationDoesNotSaveOrMutateRuntime()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        await fixture.ViewModel.UseDiscoveredEndpointAsync(_ => Task.FromResult(false));
        Assert.Equal("", fixture.ViewModel.Endpoint);
        Assert.Equal(0, fixture.Saves);
        Assert.Equal(4, fixture.ProcessCalls);
        Assert.Equal(1, fixture.MetadataCalls);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("provider")]
    [InlineData("rediscover")]
    public async Task ConfirmationCannotApplyAfterOriginalConfigurationOrObservationChanges(string change)
    {
        var fixture = new Fixture();
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        await fixture.ViewModel.UseDiscoveredEndpointAsync(async _ =>
        {
            switch (change)
            {
                case "model": fixture.ViewModel.Model = "other-exact-model"; break;
                case "endpoint": fixture.ViewModel.Endpoint = "http://127.0.0.1:45678"; break;
                case "provider":
                    fixture.Values["AiProvider"] = AiProviderKind.Ollama;
                    fixture.ViewModel.OnProviderChanged();
                    fixture.Values["AiProvider"] = AiProviderKind.FoundryLocal;
                    fixture.ViewModel.OnProviderChanged();
                    break;
                case "rediscover": await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null); break;
            }
            return true;
        });
        Assert.NotEqual("http://127.0.0.1:54321", fixture.ViewModel.Endpoint);
        Assert.Equal(change is "model" or "endpoint" ? 1 : 0, fixture.Saves);
    }

    [Fact]
    public async Task ConcurrentConfirmationIsNotDuplicated()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        var answer = new TaskCompletionSource<bool>();
        var pending = fixture.ViewModel.UseDiscoveredEndpointAsync(_ => answer.Task);
        await fixture.ViewModel.UseDiscoveredEndpointAsync(_ => throw new Xunit.Sdk.XunitException("Duplicate dialog"));
        answer.SetResult(true);
        await pending;
        Assert.Equal(1, fixture.Saves);
    }

    [Fact]
    public async Task UnknownModelDoesNotGetAutoSelectedOrConfirmed()
    {
        var fixture = new Fixture();
        fixture.ViewModel.Model = "not-in-catalog";
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        Assert.False(fixture.ViewModel.CanUseDiscoveredEndpoint);
        Assert.Contains("Enter an exact model ID", fixture.ViewModel.SetupStatus);
        await fixture.ViewModel.UseDiscoveredEndpointAsync(_ => throw new Xunit.Sdk.XunitException("Cannot approve an inferred model"));
        Assert.Equal("not-in-catalog", fixture.ViewModel.Model);
        Assert.Equal("", fixture.ViewModel.Endpoint);
    }

    [Fact]
    public async Task CancelledInFlightMetadataCannotPublishOrSave()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource();
        fixture.BeforeMetadata = async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var pending = fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        await entered.Task;
        fixture.ViewModel.DiscoverCommand.Cancel();
        await pending;
        Assert.False(fixture.ViewModel.CanUseDiscoveredEndpoint);
        Assert.Equal(0, fixture.Saves);
        Assert.Contains("cancelled", fixture.ViewModel.SetupStatus);
    }

    [Fact]
    public async Task DiscoveryFailureDoesNotOfferStaleConnection()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        fixture.BeforeMetadata = _ => throw new HttpRequestException("synthetic offline");
        await fixture.ViewModel.DiscoverCommand.ExecuteAsync(null);
        Assert.False(fixture.ViewModel.CanUseDiscoveredEndpoint);
        Assert.Contains("Discovery failed", fixture.ViewModel.SetupStatus);
        Assert.Equal(0, fixture.Saves);
    }

    private static CommandResult Ok(string output) => new() { StandardOutput = output };

    private sealed class Fixture
    {
        internal readonly Dictionary<string, object?> Values = new()
        {
            ["AiProvider"] = AiProviderKind.FoundryLocal,
            ["AiFoundryLocalEndpoint"] = "",
            ["AiFoundryLocalModel"] = "synthetic-exact-model",
            ["AiOllamaModel"] = "unchanged-ollama",
        };
        internal int ProcessCalls;
        internal int MetadataCalls;
        internal int Saves;
        internal Func<CancellationToken, Task>? BeforeMetadata;
        internal FoundryLocalSetupService Setup { get; }
        internal FoundryLocalSettingsViewModel ViewModel { get; }

        internal Fixture()
        {
            var settings = NetworkTestProxy.Create<ISettingsService>((method, args) =>
            {
                if (method.Name == "Save") { Saves++; return null; }
                if (method.Name.StartsWith("get_", StringComparison.Ordinal)) return Values[method.Name[4..]];
                if (method.Name.StartsWith("set_", StringComparison.Ordinal)) { Values[method.Name[4..]] = args[0]; return null; }
                throw new Xunit.Sdk.XunitException("Unexpected settings operation " + method.Name);
            });
            var runtime = NetworkTestProxy.Create<IFoundryLocalRuntimeService>((method, args) =>
            {
                Assert.Equal("ReadInventoryAsync", method.Name);
                MetadataCalls++;
                return ReadAsync((AiChatConfiguration)args[0]!, (CancellationToken)args[1]!);
            });
            var capabilities = NetworkTestProxy.Create<IAiCapabilityService>((method, _) =>
            {
                Assert.Equal("Invalidate", method.Name);
                return null;
            });
            var cli = new FoundryLocalCli(() => @"C:\Foundry\foundry.exe", (start, _) =>
            {
                ProcessCalls++;
                return Task.FromResult(Ok(start.ArgumentList[0] switch
                {
                    "--version" => "synthetic-cli-version",
                    "--help" => "  server Server commands",
                    "server" => start.ArgumentList[1] == "--help" ? "  status Display server status"
                        : "Endpoint: http://127.0.0.1:54321",
                    _ => throw new Xunit.Sdk.XunitException("Unexpected command"),
                }));
            });
            Setup = new(cli, runtime);
            ViewModel = new(settings, runtime, capabilities, NullLogger<FoundryLocalSettingsViewModel>.Instance, Setup);
        }

        private async Task<FoundryLocalInventory> ReadAsync(AiChatConfiguration configuration, CancellationToken ct)
        {
            Assert.Equal("http://127.0.0.1:54321", configuration.Endpoint);
            if (BeforeMetadata is not null) await BeforeMetadata(ct);
            ct.ThrowIfCancellationRequested();
            return new(configuration,
                [new("synthetic-exact-model", "synthetic-version", "chat-completion", "ONNX", "CPU",
                    "synthetic-ep", 100, "synthetic-license", "synthetic-terms", null)],
                [], [], "synthetic-runtime");
        }
    }
}
