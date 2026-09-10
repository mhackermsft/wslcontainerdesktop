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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// External-runtime preparation only. Neither CLI discovery nor REST metadata is artifact
/// provenance. In particular, a user-supplied date/hash cannot authorize acquisition.
/// </summary>
public sealed class FoundryLocalSetupService(FoundryLocalCli cli, IFoundryLocalRuntimeService runtime,
    FoundryLocalArtifactCatalog? catalog = null, FoundryLocalDownloader? downloader = null,
    FoundryLocalInstaller? installer = null, FoundryLocalModelArtifacts? modelArtifacts = null)
{
    private readonly FoundryLocalArtifactCatalog _catalog = catalog ?? new();
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    public const string InstallationGuidance =
        "Runtime-only setup downloads and registers the pinned standalone Windows package for the current user, not machine-wide. " +
        "It checks for existing installations first and asks once before any download or registration. " +
        "Only the compiled publication/size/hash/license audit controls artifact selection; user-entered audit claims are not accepted. " +
        "No in-process SDK, unpinned winget operation, automatic upgrade, server start or model selection is used. " +
        "Package registration is NOT working initial-model setup or proof of initialization/inference compatibility. " +
        FoundryLocalInstaller.InitializationGuidance + " " +
        "Use initial-model setup for the separate confirmed registration/load workflow. File-only staging does not execute Foundry. " +
        "An existing externally prepared server can be discovered and connected without installing, starting, stopping or adopting it.";

    public bool CanInstall => downloader is not null && installer is not null
        && _catalog.GetStandalone()?.IsDownloadable(DateTimeOffset.UtcNow) == true;
    public string AvailabilityGuidance => (CanInstall ? "" : "Runtime-only setup unavailable: no eligible complete download manifest/adapters. ") + InstallationGuidance;
    public string CacheLocation => downloader?.CacheLocation ?? "Unavailable";
    public bool CanStageModelFiles => modelArtifacts is not null;
    public string ModelCacheLocation => modelArtifacts?.CacheLocation ?? "Unavailable";

    public async Task<FoundryLocalModelPreparationResult> StageModelFilesAsync(AiChatConfiguration original,
        Func<string, CancellationToken, Task<bool>> confirm, Func<bool> isCurrent,
        IProgress<string>? progress, CancellationToken ct)
    {
        var entered = false;
        var started = false;
        try
        {
            await _setupGate.WaitAsync(ct);
            entered = true;
            if (modelArtifacts is null)
                return new(false, "Model-file staging is unavailable. No network requested.");
            if (original.Kind != AiProviderKind.FoundryLocal || !isCurrent())
                return new(false, "Configuration changed. No model files requested.");
            if (!await confirm(FoundryLocalModelArtifacts.ConsentSummary
                + "\nYour configured endpoint/model will remain unchanged. This is not working initial-model setup.", ct))
                return new(false, "Model-file download declined. No network requested.");
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return new(false, "Approval invalidated. No model files requested.");
            started = true;
            var result = await modelArtifacts.StageAsync(progress, ct);
            ct.ThrowIfCancellationRequested();
            if (!isCurrent())
                return new(false, "Configuration changed. Staged files retained; no settings or runtime changed.");
            return new(true, "Nine pinned CPU model files are staged and reusable offline. "
                + "They are NOT registered in Foundry or loaded; endpoint/model settings remain unchanged. "
                + FoundryLocalModelArtifacts.VerificationNotice, result.DirectoryPath);
        }

        catch (OperationCanceledException)
        {
            return new(false, "Model-file preparation cancelled. No runtime or settings changed."
                + (started ? " " + FoundryLocalModelArtifacts.RetentionGuidance : ""));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException)
        {
            // The stager's messages deliberately exclude SAS credentials and private paths.
            return new(false, error.Message + " No runtime or settings changed.");
        }
        finally
        {
            if (entered) _setupGate.Release();
        }
    }

    public async Task<FoundryLocalInstallResult> InstallRuntimeAsync(AiChatConfiguration original,
        Func<string, CancellationToken, Task<bool>> confirm, Func<bool> isCurrent,
        IProgress<string>? progress, CancellationToken ct, bool initialModelSetup = false)
    {
        var entered = false;
        var stagingRequested = false;
        var registrationRequested = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var token = deadline.Token;
        try
        {
            await _setupGate.WaitAsync(token);
            entered = true;
            var package = _catalog.GetStandalone();
            if (package is null || !package.IsDownloadable(DateTimeOffset.UtcNow) || downloader is null || installer is null)
                return new(FoundryLocalInstallState.Blocked, "No eligible complete runtime download manifest/adapters. No network or installation attempted.");
            if (original.Kind != AiProviderKind.FoundryLocal || !isCurrent())
                return new(FoundryLocalInstallState.Cancelled, "Original Foundry configuration changed; nothing requested.");
            if (!FoundryLocalInstaller.CanDisplayModelSelection(original.Model))
                return new(FoundryLocalInstallState.Blocked, "Clear or correct the model selection before setup: approval requires at most 512 characters without control/format characters. No network or installation requested.");
            progress?.Report("Read-only installed-package preflight; no downloads or runtime execution…");
            var preflight = await installer.PreflightAsync(package, token);
            token.ThrowIfCancellationRequested();
            if (!preflight.CanInstall) return new(FoundryLocalInstallState.Blocked, preflight.Guidance);
            if (!isCurrent()) return new(FoundryLocalInstallState.Cancelled, "Configuration changed; no downloads or installation requested.");
            // This immutable package reference and original settings are the only consent
            // scope. The later install callback verifies it; it never opens a second dialog.
            if (!await confirm(DownloadConfirmation(package, preflight, original.Model, initialModelSetup), token))
                return new(FoundryLocalInstallState.Declined, "Runtime-only setup declined; nothing downloaded or installed.");
            token.ThrowIfCancellationRequested();
            bool ApprovalStillCurrent() => isCurrent()
                && ReferenceEquals(package, _catalog.GetStandalone()) && !token.IsCancellationRequested;
            if (!ApprovalStillCurrent())
                return new(FoundryLocalInstallState.Cancelled, "Approval invalidated; nothing downloaded or installed.");
            stagingRequested = true;
            var staged = await downloader.StageAsync(package, preflight.NeedsVcLibs, progress, token);
            token.ThrowIfCancellationRequested();
            if (!ApprovalStillCurrent())
                return new(FoundryLocalInstallState.Cancelled, "Approval invalidated; no registration requested. " + FoundryLocalDownloader.RetentionGuidance);
            registrationRequested = true;
            var result = await installer.InstallRuntimeOnlyAsync(package.Id, staged.RuntimePath, staged.PrerequisitePath,
                original, (_, _) => Task.FromResult(ApprovalStillCurrent()), ApprovalStillCurrent, progress, token);
            return result with { Guidance = result.Guidance + " " + FoundryLocalDownloader.RetentionGuidance };
        }
        catch (OperationCanceledException)
        {
            // Explicit result preserves cancellation/Windows deployment uncertainty.
            return new(FoundryLocalInstallState.Cancelled,
                (registrationRequested ? "Cancelled or timed out; Windows registration may still complete. No rollback/uninstall attempted."
                    : "Setup cancelled or timed out before registration; no package installation requested.")
                + (stagingRequested ? " " + FoundryLocalDownloader.RetentionGuidance : ""));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            // Do not expose private cache paths or signed redirect URLs from transport errors.
            return new(FoundryLocalInstallState.Failed, $"Runtime setup failed ({ex.GetType().Name}); no automatic retry or cleanup."
                + (stagingRequested ? " " + FoundryLocalDownloader.RetentionGuidance : ""));
        }
        finally
        {
            if (entered) _setupGate.Release();
        }
    }

    internal static string DownloadConfirmation(FoundryLocalAuditedPackageSet package,
        FoundryLocalInstallPreflight preflight, string model, bool initialModelSetup = false)
    {
        var archive = package.VcLibsArchive!;
        var total = package.Runtime.Bytes + (preflight.NeedsVcLibs ? archive.Bytes : 0);
        var text =
            $"Download and install Foundry Local {package.Runtime.Version} for this Windows user only?\n" +
            $"Maximum download: {total:N0} bytes (verified cache may reduce this); additional extraction: {(preflight.NeedsVcLibs ? package.VcLibs.Bytes : 0):N0} bytes. Windows registration also uses disk space.\n" +
            $"Runtime: {package.Runtime.Bytes} bytes; SHA256 {package.Runtime.Sha256}; published {package.Runtime.PublishedAt:O}.\n" +
            $"Source: {package.RuntimeDownloadUri}\nRuntime terms: {package.Runtime.License}\n{package.Runtime.LicenseEvidence}\n" +
            (preflight.NeedsVcLibs
                ? $"Prerequisite ZIP: {archive.Bytes} bytes; SHA256 {archive.Sha256}; published {archive.PublishedAt:O}.\nSource: {archive.DownloadUri}\n" +
                    $"Only entry {archive.EntryPath} is extracted: {package.VcLibs.Bytes} bytes; SHA256 {package.VcLibs.Sha256}.\n"
                : $"Existing Microsoft x64 VCLibs {preflight.ExistingVcLibsVersion} is preserved; no prerequisite download, replacement or downgrade. Existing presence is not an artifact audit or inference compatibility test.\n") +
            $"Prerequisite target: {package.VcLibs.Version}; terms: {package.VcLibs.License}\n{package.VcLibs.LicenseEvidence}\n" +
            $"Publication evidence: {package.Runtime.PublicationEvidence}\n{archive.PublicationEvidence}\n" +
            (initialModelSetup ? "Initial-model setup is described separately below. " :
                $"Selected model (unchanged): {model}. This runtime-only action does not acquire or load any model.\n") +
            (initialModelSetup ? "You approve these runtime/prerequisite terms and the initial-model preparation described below. " :
                "You approve these runtime/prerequisite terms and this runtime-only download/registration, NOT initial-model setup. ") +
            "Network: approved Microsoft GitHub releases and their HTTPS release-asset hosts; no credentials or source-agreement acceptance. Windows may use network for signature trust checks. " +
            (initialModelSetup ? "No existing runtime is upgraded, adopted or uninstalled. " :
                "No Foundry runtime is started, stopped, upgraded, adopted or uninstalled. No inference, model or EP download is requested. ") +
            "A missing/older prerequisite may be registered; a newer prerequisite is preserved. Registration is not proof of initialization/inference compatibility. " +
            (initialModelSetup ? "" : FoundryLocalInstaller.InitializationGuidance + " ") +
            "Cancellation is not rollback; Windows deployment may complete after cancellation. " +
            FoundryLocalDownloader.RetentionGuidance;
        if (text.Length > AiTextSanitizer.DiagnosticLimit)
            throw new InvalidDataException("Complete approval details exceed the display limit. No download or installation can be authorized.");
        return AiTextSanitizer.Sanitize(text, AiTextSanitizer.DiagnosticLimit);
    }

    public async Task<FoundryLocalConnectionPlan> DiscoverAsync(AiChatConfiguration original,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (original.Kind != AiProviderKind.FoundryLocal)
            throw new ArgumentException("A Foundry Local configuration is required.");
        var discovery = await cli.DiscoverAsync(progress, ct).ConfigureAwait(false);
        var target = original with { Endpoint = discovery.Endpoint };
        progress?.Report("Reading catalog/cache/memory metadata from the discovered loopback endpoint only…");
        var inventory = await runtime.ReadInventoryAsync(target, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new(original, discovery, inventory);
    }
}

/// <summary>Observation bound to the original settings, never an acquisition approval.</summary>
public sealed record FoundryLocalModelPreparationResult(bool Success, string Guidance, string? DirectoryPath = null);

public sealed record FoundryLocalConnectionPlan(AiChatConfiguration Original,
    FoundryLocalDiscovery Discovery, FoundryLocalInventory Inventory)
{
    public string Confirmation => AiTextSanitizer.Sanitize(
        $"Use existing external Foundry Local endpoint {Discovery.Endpoint}?\n" +
        $"Observed CLI version: {Discovery.CliVersion}\nExact selected model: {Original.Model}\n" +
        $"Advertised model version: {Inventory.Selected?.Version ?? "unknown"}\n" +
        $"Advertised size MB: {Inventory.Selected?.FileSizeMb?.ToString() ?? "unknown"}\n" +
        $"Advertised license: {Inventory.Selected?.License ?? "unknown"}\n" +
        $"License information: {Inventory.Selected?.LicenseDescription ?? "unknown"}" +
        $"\nCached: {(Inventory.CacheStateKnown ? Inventory.IsCached.ToString() : "unknown")}; loaded: {(Inventory.LoadStateKnown ? Inventory.IsLoaded.ToString() : "unknown")}.\n" +
        "Only this app's endpoint setting changes. Model selection is preserved. No software, model or EP " +
        "is installed/downloaded; no license is accepted. Network: later explicit metadata/inference requests " +
        "use this loopback endpoint. No runtime is started, stopped, uninstalled or adopted. " +
        "Advertised metadata is not an artifact audit or proof of hardware/inference readiness.",
        AiTextSanitizer.DiagnosticLimit);
}
