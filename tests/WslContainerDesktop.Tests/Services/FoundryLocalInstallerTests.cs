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
using System.Security.Cryptography;
using System.Text;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Real file/hash verification and process adapter, with synthetic bytes and captured execution only.</summary>
public sealed class FoundryLocalInstallerTests
{
    [Fact]
    public async Task ProductionCatalogHasNoApprovalAndNeverReadsPathsOrAsksForConsent()
    {
        var catalog = new FoundryLocalArtifactCatalog();
        Assert.False(catalog.HasEligibleRuntime);
        var installer = new FoundryLocalInstaller(catalog, (_, _) => throw new Xunit.Sdk.XunitException("No execution"),
            () => throw new Xunit.Sdk.XunitException("No mutation invalidation"));
        var result = await installer.InstallRuntimeOnlyAsync("0.10.3.0", "not-even-a-path", "not-a-path",
            Configuration, (_, _) => throw new Xunit.Sdk.XunitException("No consent for ineligible plan"),
            () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Blocked, result.State);
        Assert.Contains("No eligible independently audited", result.Guidance);
    }

    [Fact]
    public void ApprovalCannotBeSuppliedThroughPublicConstructorsOrMutableCollections()
    {
        Assert.Single(typeof(FoundryLocalArtifactCatalog).GetConstructors());
        Assert.Empty(typeof(FoundryLocalArtifactCatalog).GetConstructors()[0].GetParameters());
        Assert.False(typeof(FoundryLocalAuditedArtifact).IsPublic);
        Assert.False(typeof(FoundryLocalAuditedPackageSet).IsPublic);
    }

    [Theory]
    [InlineData("runtime-age")]
    [InlineData("dependency-age")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("license")]
    [InlineData("publication")]
    [InlineData("closure")]
    public void IncompleteOrTooYoungCompiledAuditIsIneligible(string invalid)
    {
        using var fixture = new Fixture();
        var package = fixture.Package;
        package = invalid switch
        {
            "runtime-age" => package with { Runtime = package.Runtime with { PublishedAt = DateTimeOffset.UtcNow.AddDays(-6) } },
            "dependency-age" => package with { VcLibs = package.VcLibs with { PublishedAt = DateTimeOffset.UtcNow.AddDays(-6) } },
            "hash" => package with { Runtime = package.Runtime with { Sha256 = "claimed" } },
            "size" => package with { Runtime = package.Runtime with { Bytes = 0 } },
            "license" => package with { Runtime = package.Runtime with { License = "" } },
            "publication" => package with { Runtime = package.Runtime with { PublicationEvidence = new("http://synthetic.invalid") } },
            _ => package with { CompleteBundledDependencyAudit = new("file:///claimed-audit") },
        };
        Assert.False(new FoundryLocalArtifactCatalog(package).HasEligibleRuntime);
    }

    [Fact]
    public async Task RealAdapterVerifiesLocalBytesAndSingleConsentThenUsesFixedScriptAndEnvironmentData()
    {
        using var fixture = new Fixture();
        var confirmations = 0;
        var result = await fixture.Install(async (message, ct) =>
        {
            confirmations++;
            Assert.Contains("runtime-only installation", message);
            Assert.Contains("Model/EP version, size and license: unaudited", message);
            Assert.Contains("synthetic-exact-model", message);
            Assert.Contains("synthetic-license", message);
            Assert.Contains(fixture.Package.Runtime.Sha256, message);
            Assert.Contains(fixture.Package.VcLibs.Sha256, message);
            await Task.Yield();
            return true;
        });
        Assert.Equal(FoundryLocalInstallState.Installed, result.State);
        Assert.Equal(2, fixture.Invalidations);
        Assert.Equal(1, confirmations);
        var start = Assert.Single(fixture.Calls);
        Assert.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe", start.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", start.Arguments);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], start.ArgumentList.Take(3));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[3]));
        Assert.Equal(FoundryLocalInstaller.InstallScript, script);
        Assert.DoesNotContain(fixture.RuntimePath, script);
        Assert.Equal(fixture.RuntimePath, start.Environment["WSLCD_FOUNDRY_MSIX"]);
        Assert.Equal(fixture.DependencyPath, start.Environment["WSLCD_FOUNDRY_VCLIBS"]);
        Assert.Equal(fixture.Package.Runtime.Version, start.Environment["WSLCD_FOUNDRY_VERSION"]);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.DoesNotContain("-AllowUnsigned", script);
        Assert.DoesNotContain("-Force", script);
        Assert.DoesNotContain("Remove-AppxPackage", script);
        Assert.DoesNotContain("Invoke-Expression", script);
        Assert.DoesNotContain("winget", script);
        Assert.Contains("Get-Command foundry", script);
        Assert.Contains("Exact dependency not confirmed", script);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("dependency")]
    public async Task ChangedPreparedBytesFailBeforeConsentOrExecution(string changed)
    {
        using var fixture = new Fixture();
        var path = changed == "runtime" ? fixture.RuntimePath : fixture.DependencyPath;
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        var result = await fixture.Install((_, _) => throw new Xunit.Sdk.XunitException("No confirmation for bad bytes"));
        Assert.Equal(FoundryLocalInstallState.Failed, result.State);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task DifferentPreparedSizeFailsBeforeConsent()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.RuntimePath, "short");
        var result = await fixture.Install((_, _) => throw new Xunit.Sdk.XunitException("No consent for wrong size"));
        Assert.Equal(FoundryLocalInstallState.Failed, result.State);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task ApprovedFilesCannotBeReplacedWhileConfirmationIsOpen()
    {
        using var fixture = new Fixture();
        var result = await fixture.Install((_, _) =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.RuntimePath, "changed"));
            Assert.Throws<IOException>(() => File.Delete(fixture.DependencyPath));
            return Task.FromResult(false);
        });
        Assert.Equal(FoundryLocalInstallState.Declined, result.State);
        Assert.Equal(0, fixture.Invalidations);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task ApprovalInvalidatedBySettingsChangeNeverInstalls()
    {
        using var fixture = new Fixture();
        var result = await fixture.Install((_, _) =>
        {
            fixture.Current = false;
            return Task.FromResult(true);
        });
        Assert.Equal(FoundryLocalInstallState.Cancelled, result.State);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task CancellationWhileAwaitingConsentNeverInstalls()
    {
        using var fixture = new Fixture();
        using var cts = new CancellationTokenSource();
        var result = await fixture.Install((_, token) =>
        {
            Assert.Equal(cts.Token, token);
            cts.Cancel();
            return Task.FromResult(true);
        }, cts.Token);
        Assert.Equal(FoundryLocalInstallState.Cancelled, result.State);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task CancelledDeploymentDoesNotUninstallOrPromiseRollback()
    {
        using var fixture = new Fixture();
        fixture.Execute = (_, _) => throw new OperationCanceledException();
        var result = await fixture.Install();
        Assert.Equal(FoundryLocalInstallState.Cancelled, result.State);
        Assert.Contains("may still complete", result.Guidance);
        Assert.Equal(2, fixture.Invalidations);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task ConfigurationChangedDuringDeploymentDoesNotReportOriginalPlanReady()
    {
        using var fixture = new Fixture();
        fixture.Execute = (_, _) =>
        {
            fixture.Current = false;
            return Task.FromResult(new CommandResult { StandardOutput = "@@WSLCD_FOUNDRY_INSTALLED" });
        };
        var result = await fixture.Install();
        Assert.Equal(FoundryLocalInstallState.Cancelled, result.State);
        Assert.Contains("may be installed", result.Guidance);
        Assert.Single(fixture.Calls);
    }

    [Theory]
    [InlineData(1, "@@WSLCD_FOUNDRY_INSTALLED")]
    [InlineData(0, "")]
    [InlineData(0, "unverified @@WSLCD_FOUNDRY_INSTALLED suffix")]
    public async Task NonzeroExitOrMissingExactObservationFailsWithoutRetry(int exitCode, string output)
    {
        using var fixture = new Fixture();
        fixture.Execute = (_, _) => Task.FromResult(new CommandResult
        {
            ExitCode = exitCode, StandardOutput = output, StandardError = "synthetic-private-diagnostic",
        });
        var result = await fixture.Install();
        Assert.Equal(FoundryLocalInstallState.Failed, result.State);
        Assert.DoesNotContain("synthetic-private", result.Guidance);
        Assert.Equal(2, fixture.Invalidations);
        Assert.Single(fixture.Calls);
    }

    private static AiChatConfiguration Configuration => new(AiProviderKind.FoundryLocal, "", "synthetic-exact-model");

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "FoundryInstallerFixtures-" + Guid.NewGuid().ToString("N"));
        internal string RuntimePath { get; }
        internal string DependencyPath { get; }
        internal FoundryLocalAuditedPackageSet Package { get; }
        internal List<ProcessStartInfo> Calls { get; } = [];
        internal int Invalidations { get; private set; }
        internal bool Current = true;
        internal Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> Execute =
            (_, _) => Task.FromResult(new CommandResult { StandardOutput = "@@WSLCD_FOUNDRY_INSTALLED\n" });
        private readonly FoundryLocalInstaller _installer;

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            RuntimePath = Path.Combine(_directory, "synthetic runtime ' $ ; &.msix");
            DependencyPath = Path.Combine(_directory, "synthetic dependency.appx");
            var runtimeBytes = Encoding.UTF8.GetBytes("synthetic runtime bytes, not an installer");
            var dependencyBytes = Encoding.UTF8.GetBytes("synthetic dependency bytes, not an installer");
            File.WriteAllBytes(RuntimePath, runtimeBytes);
            File.WriteAllBytes(DependencyPath, dependencyBytes);
            Package = new("synthetic-reviewed-test-set", Artifact("1.0.0.0", runtimeBytes),
                Artifact("14.0.1.0", dependencyBytes), new("https://synthetic.invalid/test-fixture-not-an-audit"));
            _installer = new(new FoundryLocalArtifactCatalog(Package), (start, ct) =>
            {
                Assert.Equal(1, Invalidations);
                Calls.Add(start);
                return Execute(start, ct);
            }, () => Invalidations++);
        }

        internal Task<FoundryLocalInstallResult> Install(Func<string, CancellationToken, Task<bool>>? confirm = null,
            CancellationToken ct = default) =>
            _installer.InstallRuntimeOnlyAsync(Package.Id, RuntimePath, DependencyPath, Configuration,
                confirm ?? ((_, _) => Task.FromResult(true)), () => Current, null, ct);

        private static FoundryLocalAuditedArtifact Artifact(string version, byte[] bytes) =>
            new(version, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
                new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new("https://synthetic.invalid/test-fixture-publication"), "synthetic-license",
                new("https://synthetic.invalid/test-fixture-license"));

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
