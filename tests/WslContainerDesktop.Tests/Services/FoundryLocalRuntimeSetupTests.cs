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
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class FoundryLocalRuntimeSetupTests
{
    private static AiChatConfiguration Original => new(AiProviderKind.FoundryLocal, "", "synthetic-model-selection");

    [Fact]
    public void DeploymentRechecksBothKindsOfExistingInstallAfterPrerequisiteRegistration()
    {
        var script = FoundryLocalInstaller.InstallScript;
        var prerequisite = script.IndexOf("Add-AppxPackage -Path $env:WSLCD_FOUNDRY_VCLIBS", StringComparison.Ordinal);
        var lastCliCheck = script.LastIndexOf("Get-Command foundry", StringComparison.Ordinal);
        var packageCheck = script.IndexOf("Foundry appeared during preparation", StringComparison.Ordinal);
        var registration = script.IndexOf("Add-AppxPackage -Path $env:WSLCD_FOUNDRY_MSIX", StringComparison.Ordinal);
        Assert.True(prerequisite >= 0 && lastCliCheck > prerequisite && packageCheck > prerequisite);
        Assert.True(lastCliCheck < registration && packageCheck < registration);
        Assert.DoesNotContain("-Force", script);
        Assert.DoesNotContain("-AllowUnsigned", script);
    }

    [Fact]
    public void ActualProductionConfirmationRetainsAllCriticalTermsWithinDisplayLimit()
    {
        var package = new FoundryLocalArtifactCatalog().GetStandalone()!;
        var message = FoundryLocalSetupService.DownloadConfirmation(package, new(true, true, "", ""), new string('m', 512));
        Assert.InRange(message.Length, 1, AiTextSanitizer.DiagnosticLimit - 1);
        Assert.Contains(package.Runtime.Sha256, message);
        Assert.Contains(package.VcLibsArchive!.Sha256, message);
        Assert.Contains(package.VcLibs.Sha256, message);
        Assert.Contains(package.Runtime.LicenseEvidence.ToString(), message);
        Assert.Contains(package.VcLibs.LicenseEvidence.ToString(), message);
        Assert.Contains(new string('m', 512), message);
        Assert.Contains("does not acquire or load any model", message);
        Assert.Contains("signed-package deployment", message);
        Assert.Contains("No inference, model or EP download", message);
        Assert.EndsWith(FoundryLocalDownloader.RetentionGuidance, message);
    }

    [Theory]
    [InlineData("too-long")]
    [InlineData("control")]
    [InlineData("format")]
    public async Task ModelSelectionCannotHideOrTruncateConsentTerms(string invalid)
    {
        using var fixture = new FoundryDownloadFixture();
        var model = invalid switch
        {
            "too-long" => new string('m', 100_000),
            "control" => "model\nmisleading approval",
            _ => "model\u202Emisleading approval",
        };
        var result = await fixture.Setup.InstallRuntimeAsync(Original with { Model = model },
            (_, _) => throw new Xunit.Sdk.XunitException("Do not display incomplete approval"),
            () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Blocked, result.State);
        Assert.Empty(fixture.ProcessCalls);
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public async Task ExactlyOneConsentBeforeNetworkThenRealStagingAndCapturedInstallPreservesScope()
    {
        using var fixture = new FoundryDownloadFixture();
        var confirmations = 0;
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (message, _) =>
        {
            confirmations++;
            Assert.Single(fixture.ProcessCalls); // Read-only preflight only.
            Assert.Empty(fixture.Http.Requests);
            Assert.False(Directory.Exists(fixture.CacheRoot));
            Assert.Contains("Maximum download:", message);
            Assert.Contains("synthetic-model-selection", message);
            Assert.Contains("does not acquire or load any model", message);
            Assert.Contains(fixture.Package.Runtime.Sha256, message);
            Assert.Contains(fixture.Package.VcLibsArchive!.Sha256, message);
            Assert.Contains(fixture.Package.VcLibs.Sha256, message);
            Assert.Contains("synthetic-license", message);
            Assert.Contains("Network:", message);
            Assert.Contains("not rollback", message);
            Assert.Contains("signed-package deployment", message);
            Assert.Contains("does not start Foundry or fetch missing DLLs", message);
            return Task.FromResult(true);
        }, () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Installed, result.State);
        Assert.Equal(1, confirmations);
        Assert.Equal(2, fixture.Http.Requests.Count);
        Assert.Equal(2, fixture.ProcessCalls.Count);
        Assert.Equal(2, fixture.Invalidations);
        Assert.Contains("Registration alone is not model readiness", result.Guidance);
        Assert.Contains("not proof", result.Guidance);
        Assert.Contains("CPU initialization was exercised separately", result.Guidance);
        Assert.DoesNotContain(fixture.ProcessCalls, process => process.FileName.EndsWith("foundry.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("@@WSLCD_FOUNDRY_PREFLIGHT=PRESENT")]
    [InlineData("unexpected-success-output")]
    [InlineData("@@WSLCD_FOUNDRY_PREFLIGHT=ABSENT\n@@WSLCD_VCLIBS=PRESERVE:14.0.1.0")]
    [InlineData("@@WSLCD_FOUNDRY_PREFLIGHT=ABSENT\n@@WSLCD_VCLIBS=UNKNOWN")]
    public async Task ExistingRuntimeOrUnknownPreflightStopsBeforeConsentDownloadAndMutation(string preflight)
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.PreflightOutput = preflight;
        var result = await fixture.Setup.InstallRuntimeAsync(Original,
            (_, _) => throw new Xunit.Sdk.XunitException("Do not ask to approve a blocked plan"),
            () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Blocked, result.State);
        Assert.Single(fixture.ProcessCalls);
        Assert.Empty(fixture.Http.Requests);
        Assert.Equal(0, fixture.Invalidations);
    }

    [Fact]
    public async Task FailedPreflightDoesNotRetryOrDownload()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.BeforeProcess = (_, _) => Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "private diagnostic" });
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) => Task.FromResult(true), () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Blocked, result.State);
        Assert.Single(fixture.ProcessCalls);
        Assert.Empty(fixture.Http.Requests);
        Assert.DoesNotContain("private diagnostic", result.Guidance);
    }

    [Fact]
    public async Task NewerMicrosoftPrerequisiteIsPreservedWithoutDownloadOrDowngrade()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.PreflightOutput = "@@WSLCD_FOUNDRY_PREFLIGHT=ABSENT\n@@WSLCD_VCLIBS=PRESERVE:14.0.40000.0";
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (message, _) =>
        {
            Assert.Contains("14.0.40000.0 is preserved", message);
            Assert.Contains("no prerequisite download", message);
            Assert.Contains("not an artifact audit or inference compatibility test", message);
            return Task.FromResult(true);
        }, () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Installed, result.State);
        Assert.Single(fixture.Http.Requests);
        Assert.Equal(fixture.Package.RuntimeDownloadUri, fixture.Http.Requests[0]);
        Assert.Equal("", fixture.ProcessCalls[1].Environment["WSLCD_FOUNDRY_VCLIBS"]);
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.appx"));
        Assert.Empty(Directory.GetFiles(fixture.CacheRoot, "*.zip"));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(fixture.ProcessCalls[1].ArgumentList[3]));
        Assert.DoesNotContain("$installed.Dependencies", script);
        Assert.DoesNotContain("-Force", script);
        Assert.Contains("if (-not $dependency)", script);
    }

    [Fact]
    public async Task DeclinedConsentHasNoNetworkFilesOrMutation()
    {
        using var fixture = new FoundryDownloadFixture();
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) => Task.FromResult(false), () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Declined, result.State);
        Assert.Single(fixture.ProcessCalls);
        Assert.Empty(fixture.Http.Requests);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public async Task ConfigurationChangeDuringConfirmationInvalidatesApprovalBeforeNetwork()
    {
        using var fixture = new FoundryDownloadFixture();
        var current = true;
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) =>
        {
            current = false;
            return Task.FromResult(true);
        }, () => current, null, default);
        Assert.Equal(FoundryLocalInstallState.Cancelled, result.State);
        Assert.Empty(fixture.Http.Requests);
        Assert.Single(fixture.ProcessCalls);
    }

    [Fact]
    public async Task CancellationDuringDownloadRetainsCacheAndDoesNotRegister()
    {
        using var fixture = new FoundryDownloadFixture();
        using var cts = new CancellationTokenSource();
        fixture.Http.Respond = uri =>
        {
            if (uri == fixture.Package.VcLibsArchive!.DownloadUri) cts.Cancel();
            return FoundryDownloadFixture.Bytes(uri == fixture.Package.RuntimeDownloadUri ? fixture.RuntimeBytes : fixture.ArchiveBytes);
        };
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) => Task.FromResult(true), () => true, null, cts.Token);
        Assert.Equal(FoundryLocalInstallState.Cancelled, result.State);
        Assert.Single(fixture.ProcessCalls);
        Assert.Single(Directory.GetFiles(fixture.CacheRoot, "*.msix"));
        Assert.Contains("retained", result.Guidance);
        Assert.Equal(0, fixture.Invalidations);
    }

    [Fact]
    public async Task FailedDownloadDoesNotInvokeInstallerOrRetry()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.Http.Respond = _ => new(HttpStatusCode.BadGateway);
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) => Task.FromResult(true), () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Failed, result.State);
        Assert.Single(fixture.ProcessCalls);
        Assert.Single(fixture.Http.Requests);
        Assert.Equal(0, fixture.Invalidations);
    }

    [Fact]
    public async Task PreparedOfflineCacheStillRequiresOneConsentAndPreflightBeforeInstall()
    {
        using var fixture = new FoundryDownloadFixture();
        await fixture.Downloader.StageAsync(fixture.Package, true, null, default);
        fixture.Http.Requests.Clear();
        fixture.Http.Respond = _ => throw new Xunit.Sdk.XunitException("No HTTP in offline verified-cache setup");
        var confirmations = 0;
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) =>
        {
            confirmations++;
            return Task.FromResult(true);
        }, () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Installed, result.State);
        Assert.Equal(1, confirmations);
        Assert.Empty(fixture.Http.Requests);
        Assert.Equal(2, fixture.ProcessCalls.Count);
    }

    [Fact]
    public async Task WindowsFailureIsNotRetriedOrRolledBack()
    {
        using var fixture = new FoundryDownloadFixture();
        fixture.BeforeProcess = (start, _) =>
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[3]));
            return Task.FromResult(script == FoundryLocalInstaller.PreflightScript
                ? new CommandResult { StandardOutput = fixture.PreflightOutput }
                : new CommandResult { ExitCode = 1 });
        };
        var result = await fixture.Setup.InstallRuntimeAsync(Original, (_, _) => Task.FromResult(true), () => true, null, default);
        Assert.Equal(FoundryLocalInstallState.Failed, result.State);
        Assert.Equal(2, fixture.ProcessCalls.Count);
        Assert.Contains("No retry or uninstall attempted", result.Guidance);
        Assert.Equal(2, fixture.Invalidations);
    }

    [Fact]
    public async Task SuccessfulViewModelWorkflowDoesNotChangeEndpointModelOrOtherProviderSettings()
    {
        using var fixture = new FoundryDownloadFixture();
        var vm = CreateViewModel(fixture, out var values, out var saves);
        Assert.True(vm.CanInstallRuntime);
        await vm.InstallRuntimeAsync((_, _) => Task.FromResult(true));
        Assert.False(vm.IsInstallingRuntime);
        Assert.Equal("", vm.Endpoint);
        Assert.Equal("synthetic-model-selection", vm.Model);
        Assert.Equal("unchanged-ollama", values["AiOllamaModel"]);
        Assert.Equal(0, saves());
        Assert.Contains("registered for this user", vm.SetupStatus);
        Assert.Contains("not model readiness", vm.SetupStatus);
    }

    [Fact]
    public async Task ViewModelProviderAwayAndBackDuringConsentPreventsNetworkAndDuplicateDialog()
    {
        using var fixture = new FoundryDownloadFixture();
        var vm = CreateViewModel(fixture, out var values, out var saves);
        await vm.InstallRuntimeAsync(async (_, _) =>
        {
            await vm.InstallRuntimeAsync((_, _) => throw new Xunit.Sdk.XunitException("No concurrent duplicate confirmation"));
            values["AiProvider"] = AiProviderKind.Ollama;
            vm.OnProviderChanged();
            values["AiProvider"] = AiProviderKind.FoundryLocal;
            vm.OnProviderChanged();
            return true;
        });
        Assert.Empty(fixture.Http.Requests);
        Assert.Single(fixture.ProcessCalls);
        Assert.Equal(0, saves());
        Assert.False(vm.IsInstallingRuntime);
    }

    [Fact]
    public async Task ViewModelCancellationDismissesPendingConsentBeforeNetwork()
    {
        using var fixture = new FoundryDownloadFixture();
        var vm = CreateViewModel(fixture, out _, out _);
        var entered = new TaskCompletionSource();
        var pending = vm.InstallRuntimeAsync(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        await entered.Task;
        vm.CancelRuntimeSetupCommand.Execute(null);
        await pending;
        Assert.Empty(fixture.Http.Requests);
        Assert.Single(fixture.ProcessCalls);
        Assert.Contains("cancelled", vm.SetupStatus);
    }

    private static FoundryLocalSettingsViewModel CreateViewModel(FoundryDownloadFixture fixture,
        out Dictionary<string, object?> values, out Func<int> saves)
    {
        var settingsValues = new Dictionary<string, object?>
        {
            ["AiProvider"] = AiProviderKind.FoundryLocal,
            ["AiFoundryLocalEndpoint"] = "",
            ["AiFoundryLocalModel"] = "synthetic-model-selection",
            ["AiOllamaModel"] = "unchanged-ollama",
        };
        var saved = 0;
        var settings = NetworkTestProxy.Create<ISettingsService>((method, args) =>
        {
            if (method.Name == "Save") { saved++; return null; }
            if (method.Name.StartsWith("get_", StringComparison.Ordinal)) return settingsValues[method.Name[4..]];
            if (method.Name.StartsWith("set_", StringComparison.Ordinal)) { settingsValues[method.Name[4..]] = args[0]; return null; }
            throw new Xunit.Sdk.XunitException("Unexpected settings call");
        });
        var runtime = NetworkTestProxy.Create<IFoundryLocalRuntimeService>((_, _) => throw new Xunit.Sdk.XunitException("No model/REST calls"));
        var capabilities = NetworkTestProxy.Create<IAiCapabilityService>((method, _) =>
        {
            Assert.Equal("Invalidate", method.Name);
            return null;
        });
        values = settingsValues;
        saves = () => saved;
        return new(settings, runtime, capabilities, NullLogger<FoundryLocalSettingsViewModel>.Instance, fixture.Setup);
    }
}
