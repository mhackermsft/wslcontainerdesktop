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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Verifies logout outcomes with synthetic engines, settings, and credential metadata.</summary>
public sealed class RegistryLogoutServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutUsesChosenClientAndRechecksBeforeEachAlias(bool changed)
    {
        var fixture = new Fixture();
        var calls = new List<string>();
        var checks = 0;
        var client = NetworkTestProxy.Create<IWslcService>((method, args) =>
        {
            if (method.Name != nameof(IWslcService.LogoutRegistryAsync))
                throw new InvalidOperationException(method.Name);
            calls.Add((string)args[0]!);
            return Task.FromResult(Success());
        });
        Task Verify(CancellationToken _)
        {
            checks++;
            return changed && checks == 2
                ? Task.FromException(new InvalidOperationException("Session changed"))
                : Task.CompletedTask;
        }

        if (changed)
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().LogoutAsync("docker.io",
                sessionClient: client, verifyTarget: Verify));
        else
            Assert.Equal(RegistryLogoutStatus.Completed, (await fixture.Service().LogoutAsync("docker.io",
                sessionClient: client, verifyTarget: Verify)).Status);
        Assert.Equal(changed ? ["docker.io"] : ["docker.io", "registry-1.docker.io"], calls);
        Assert.Empty(fixture.LogoutTargets);
    }

    [Fact]
    public async Task AlreadyAbsentLoginIsCompletedOnlyAfterCheckingAllAliases()
    {
        var fixture = new Fixture { Logout = _ => NotLoggedIn() };

        var result = await fixture.Service().LogoutAsync("docker.io");

        Assert.Equal(RegistryLogoutStatus.Completed, result.Status);
        Assert.Equal(["docker.io", "registry-1.docker.io"], fixture.LogoutTargets);
        Assert.Equal(4, fixture.MetadataReads.Count);
    }

    [Fact]
    public async Task RemovesBothIndependentDockerHubKeysBeforeReportingCompletion()
    {
        var fixture = new Fixture();
        fixture.Stored.Add("https://index.docker.io/v1/");
        fixture.Stored.Add("registry-1.docker.io");
        fixture.Logout = server =>
        {
            var key = server == "docker.io" ? "https://index.docker.io/v1/" : server;
            return fixture.Stored.Remove(key) ? Success() : NotLoggedIn();
        };

        var result = await fixture.Service().LogoutAsync("docker.io");

        Assert.Equal(RegistryLogoutStatus.Completed, result.Status);
        Assert.Empty(fixture.Stored);
        Assert.Equal(["docker.io", "registry-1.docker.io"], fixture.LogoutTargets);
    }

    [Fact]
    public async Task ARemainingLegacyAliasPreventsClaimingLogoutSucceeded()
    {
        var fixture = new Fixture();
        fixture.Stored.UnionWith(["index.docker.io", "https://index.docker.io/v1/", "registry-1.docker.io"]);
        fixture.Logout = server =>
        {
            fixture.Stored.Remove(server == "docker.io" ? "https://index.docker.io/v1/" : server);
            return Success();
        };

        var result = await fixture.Service().LogoutAsync("docker.io");

        Assert.Equal(RegistryLogoutStatus.CredentialsRemain, result.Status);
        Assert.Equal(["index.docker.io"], fixture.Stored);
    }

    [Fact]
    public async Task NotFoundOutputCannotHideACredentialThatStillExists()
    {
        var fixture = new Fixture { Logout = _ => NotLoggedIn() };
        fixture.Stored.Add("registry-1.docker.io");

        Assert.Equal(RegistryLogoutStatus.CredentialsRemain,
            (await fixture.Service().LogoutAsync("docker.io")).Status);
    }

    [Fact]
    public async Task AnUnreadableCredentialAliasLeavesLogoutUnconfirmed()
    {
        var fixture = new Fixture { MetadataError = 5 };

        Assert.Equal(RegistryLogoutStatus.CredentialStateUnknown,
            (await fixture.Service().LogoutAsync("docker.io")).Status);
    }

    [Theory]
    [InlineData("mydocker.io")]
    [InlineData("docker.io.example.com")]
    [InlineData("docker.io:5000")]
    public async Task OtherRegistriesNeverLogoutOrReadDockerHub(string server)
    {
        var fixture = new Fixture();
        fixture.Stored.Add("registry-1.docker.io");

        var result = await fixture.Service().LogoutAsync(server);

        Assert.Equal(RegistryLogoutStatus.Completed, result.Status);
        Assert.Equal([server], fixture.LogoutTargets);
        Assert.Equal(["wslc-credential/" + server], fixture.MetadataReads);
        Assert.Contains("registry-1.docker.io", fixture.Stored);
    }

    [Theory]
    [InlineData("https://index.docker.io/v1/")]
    [InlineData("https://docker.io:443")]
    [InlineData("docker.io:443")]
    public async Task ExplicitDockerHubEndpointsArePassedToTheEngineBeforeCanonicalKeys(string server)
    {
        var fixture = new Fixture();

        Assert.Equal(RegistryLogoutStatus.Completed,
            (await fixture.Service().LogoutAsync(server)).Status);
        Assert.Equal([server, "docker.io", "registry-1.docker.io"], fixture.LogoutTargets);
        Assert.Contains("wslc-credential/" + server, fixture.MetadataReads);
    }

    [Theory]
    [InlineData("https://docker.io:443/v2", "docker.io:443", false)]
    [InlineData("https://docker.io:443/v2", "docker.io:443", true)]
    [InlineData("https://DOCKER.IO:443/v2", "DOCKER.IO:443", false)]
    [InlineData("https://registry-1.docker.io:443/v2", "registry-1.docker.io:443", true)]
    [InlineData("https://private.example.com:5443/v2", "private.example.com:5443", false)]
    [InlineData("https://private.example.com:5443/v2", "private.example.com:5443", true)]
    public async Task ARemainingEngineHostAndPortKeyPreventsClaimingUriLogoutSucceeded(
        string server, string engineKey, bool absent)
    {
        var fixture = new Fixture { Logout = _ => absent ? NotLoggedIn() : Success() };
        fixture.Stored.Add(engineKey);

        Assert.Equal(RegistryLogoutStatus.CredentialsRemain,
            (await fixture.Service().LogoutAsync(server)).Status);
        Assert.Contains("wslc-credential/" + engineKey, fixture.MetadataReads);
        if (!RegistryServerAddress.IsDockerHub(server))
        {
            Assert.Equal([server], fixture.LogoutTargets);
        }
    }

    [Fact]
    public async Task OrdinaryEngineFailureIsRetainedAndStopsLogout()
    {
        var failure = new CommandResult { ExitCode = 1, StandardError = "Error code: E_ACCESSDENIED" };
        var fixture = new Fixture { Logout = _ => failure };

        var result = await fixture.Service().LogoutAsync("docker.io");

        Assert.Equal(RegistryLogoutStatus.Failed, result.Status);
        Assert.Same(failure, result.Error);
        Assert.Equal(["docker.io"], fixture.LogoutTargets);
        Assert.Empty(fixture.MetadataReads);
    }

    [Fact]
    public async Task AHcsNotFoundErrorIsARealFailure()
    {
        var failure = new CommandResult { ExitCode = 1, StandardError = "Error code: HCS/ERROR_NOT_FOUND" };
        var fixture = new Fixture { Logout = _ => failure };

        var result = await fixture.Service().LogoutAsync("docker.io");

        Assert.Equal(RegistryLogoutStatus.Failed, result.Status);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public async Task ASecondKeyFailureCannotBeHiddenByTheFirstKeysSuccess()
    {
        var failure = new CommandResult { ExitCode = 1, StandardError = "Error code: E_ACCESSDENIED" };
        var fixture = new Fixture { Logout = server => server == "docker.io" ? Success() : failure };

        var result = await fixture.Service().LogoutAsync("docker.io");

        Assert.Equal(RegistryLogoutStatus.Failed, result.Status);
        Assert.Same(failure, result.Error);
        Assert.Equal(["docker.io", "registry-1.docker.io"], fixture.LogoutTargets);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("wincred")]
    public async Task DefaultAndWindowsBackendsMustCheckWindowsMetadata(string? backend)
    {
        var fixture = new Fixture { Backend = backend };
        fixture.Stored.Add("registry-1.docker.io");

        Assert.Equal(RegistryLogoutStatus.CredentialsRemain,
            (await fixture.Service().LogoutAsync("docker.io")).Status);
        Assert.NotEmpty(fixture.MetadataReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileBackendUsesEngineResultsWithoutReadingUnrelatedWindowsCredentials(bool absent)
    {
        var fixture = new Fixture
        {
            Backend = "file", MetadataError = 5,
            Logout = _ => absent ? NotLoggedIn() : Success(),
        };
        fixture.Stored.Add("registry-1.docker.io");

        Assert.Equal(RegistryLogoutStatus.Completed,
            (await fixture.Service().LogoutAsync("docker.io")).Status);
        Assert.Equal(["docker.io", "registry-1.docker.io"], fixture.LogoutTargets);
        Assert.Empty(fixture.MetadataReads);
        Assert.Contains("registry-1.docker.io", fixture.Stored);
    }

    [Fact]
    public async Task AnUnreadableBackendDoesNotStopCleanupButCannotConfirmIt()
    {
        var fixture = new Fixture { SettingsError = new IOException("Synthetic unreadable settings.") };

        Assert.Equal(RegistryLogoutStatus.CredentialStateUnknown,
            (await fixture.Service().LogoutAsync("docker.io")).Status);
        Assert.Equal(["docker.io", "registry-1.docker.io"], fixture.LogoutTargets);
        Assert.Empty(fixture.MetadataReads);
    }

    [Fact]
    public async Task AnUnsupportedBackendCannotConfirmLogoutThroughWindowsMetadata()
    {
        var fixture = new Fixture { Backend = "future-backend" };

        Assert.Equal(RegistryLogoutStatus.CredentialStateUnknown,
            (await fixture.Service().LogoutAsync("docker.io")).Status);
        Assert.Empty(fixture.MetadataReads);
    }

    [Fact]
    public async Task CancellationStopsBeforeAnyLogout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new Fixture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service().LogoutAsync("docker.io", cancellation.Token));
        Assert.Empty(fixture.LogoutTargets);
    }

    private static CommandResult Success() => new() { ExitCode = 0 };

    private static CommandResult NotLoggedIn() => new()
    {
        ExitCode = 1, StandardError = "Not logged in to the registry.\nError code: ERROR_NOT_FOUND\n",
    };

    private sealed class Fixture
    {
        public string? Backend { get; init; } = "default";
        public Exception? SettingsError { get; init; }
        public int? MetadataError { get; init; }
        public Func<string, CommandResult> Logout { get; set; } = _ => Success();
        public HashSet<string> Stored { get; } = new(StringComparer.Ordinal);
        public List<string> LogoutTargets { get; } = [];
        public List<string> MetadataReads { get; } = [];

        public RegistryLogoutService Service()
        {
            var engine = NetworkTestProxy.Create<IWslcService>((method, args) =>
            {
                Assert.Equal(nameof(IWslcService.LogoutRegistryAsync), method.Name);
                var server = Assert.IsType<string>(args[0]);
                LogoutTargets.Add(server);
                return Task.FromResult(Logout(server));
            });
            var settings = NetworkTestProxy.Create<IWslcSettingsFileService>((method, _) =>
            {
                Assert.Equal(nameof(IWslcSettingsFileService.ReadAsync), method.Name);
                return SettingsError is null
                    ? Task.FromResult(new WslcSettingsFile { CredentialStore = Backend })
                    : Task.FromException<WslcSettingsFile>(SettingsError);
            });
            var credentials = new RegistryCredentialStore(NullLogger<RegistryCredentialStore>.Instance, target =>
            {
                MetadataReads.Add(target);
                if (MetadataError is { } error)
                {
                    return (error, null);
                }

                return Stored.Contains(target["wslc-credential/".Length..])
                    ? (0, "synthetic-user") : (1168, null);
            });
            return new(engine, credentials, settings);
        }
    }
}
