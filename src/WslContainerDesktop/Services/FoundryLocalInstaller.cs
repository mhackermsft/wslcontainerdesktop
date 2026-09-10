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

namespace WslContainerDesktop.Services;

/// <summary>
/// Prepared, signed user-scope MSIX installation, not an SDK or an unpinned winget operation.
/// https://learn.microsoft.com/powershell/module/appx/add-appxpackage
/// This fixed script passes paths as environment data, never interpolated PowerShell code.
/// </summary>
public sealed class FoundryLocalInstaller
{
    public const string InitializationGuidance =
        "Standalone 0.10.3 CPU initialization was exercised separately; that is not proof of this app's signed-package deployment or every hardware configuration. " +
        "Setup registers packages only; it does not start Foundry or fetch missing DLLs, execution providers or models.";

    internal const string PreflightScript = """
        $ErrorActionPreference = 'Stop'
        if ((Get-AppxPackage -Name 'Microsoft.FoundryLocal') -or (Get-Command foundry -CommandType Application -ErrorAction SilentlyContinue)) {
            Write-Output '@@WSLCD_FOUNDRY_PREFLIGHT=PRESENT'
            exit 0
        }
        Write-Output '@@WSLCD_FOUNDRY_PREFLIGHT=ABSENT'
        $dependency = Get-AppxPackage -Name 'Microsoft.VCLibs.140.00.UWPDesktop' | Where-Object { $_.Architecture -eq 'X64' -and $_.PackageFamilyName -eq 'Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe' -and $_.Version -ge [version]$env:WSLCD_FOUNDRY_VCLIBS_VERSION } | Sort-Object Version -Descending | Select-Object -First 1
        if ($dependency) {
            Write-Output ('@@WSLCD_VCLIBS=PRESERVE:' + $dependency.Version.ToString())
        } else {
            Write-Output '@@WSLCD_VCLIBS=REQUIRED'
        }
        """;

    internal const string InstallScript = """
        $ErrorActionPreference = 'Stop'
        if (Get-AppxPackage -Name 'Microsoft.FoundryLocal') { throw 'Existing Foundry package; installation blocked.' }
        if (Get-Command foundry -CommandType Application -ErrorAction SilentlyContinue) { throw 'Existing standalone CLI; installation blocked.' }
        $dependency = Get-AppxPackage -Name 'Microsoft.VCLibs.140.00.UWPDesktop' | Where-Object { $_.Architecture -eq 'X64' -and $_.PackageFamilyName -eq 'Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe' -and $_.Version -ge [version]$env:WSLCD_FOUNDRY_VCLIBS_VERSION }
        if (-not $dependency) {
            if (-not $env:WSLCD_FOUNDRY_VCLIBS) { throw 'Prerequisite state changed; inspect before retrying.' }
            Add-AppxPackage -Path $env:WSLCD_FOUNDRY_VCLIBS -ErrorAction Stop
        }
        $registered = Get-AppxPackage -Name 'Microsoft.VCLibs.140.00.UWPDesktop' | Where-Object { $_.Architecture -eq 'X64' -and $_.PackageFamilyName -eq 'Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe' -and $_.Version -ge [version]$env:WSLCD_FOUNDRY_VCLIBS_VERSION }
        if (-not $registered) { throw 'Prerequisite registration not confirmed.' }
        if (Get-AppxPackage -Name 'Microsoft.FoundryLocal') { throw 'Foundry appeared during preparation; installation blocked.' }
        if (Get-Command foundry -CommandType Application -ErrorAction SilentlyContinue) { throw 'Standalone CLI appeared during preparation; installation blocked.' }
        Add-AppxPackage -Path $env:WSLCD_FOUNDRY_MSIX -ErrorAction Stop
        $installed = Get-AppxPackage -Name 'Microsoft.FoundryLocal' | Where-Object { $_.Architecture -eq 'X64' -and $_.PackageFamilyName -eq 'Microsoft.FoundryLocal_8wekyb3d8bbwe' -and $_.Version.ToString() -eq $env:WSLCD_FOUNDRY_VERSION }
        if (-not $installed) { throw 'Installed version not confirmed.' }
        $registered = Get-AppxPackage -Name 'Microsoft.VCLibs.140.00.UWPDesktop' | Where-Object { $_.Architecture -eq 'X64' -and $_.PackageFamilyName -eq 'Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe' -and $_.Version -ge [version]$env:WSLCD_FOUNDRY_VCLIBS_VERSION }
        if (-not $registered) { throw 'Prerequisite registration changed; inspect packages.' }
        Write-Output '@@WSLCD_FOUNDRY_INSTALLED'
        """;

    private readonly FoundryLocalArtifactCatalog _catalog;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> _run;
    private readonly Action _invalidateCapabilities;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FoundryLocalInstaller(FoundryLocalArtifactCatalog catalog, IAiCapabilityService capabilities) : this(catalog,
        (start, ct) => ProcessExecutor.RunAsync(start, timeout: TimeSpan.FromMinutes(10),
            launchErrorContext: "Could not launch Windows package deployment.", ct: ct),
        capabilities.Invalidate) { }

    internal FoundryLocalInstaller(FoundryLocalArtifactCatalog catalog,
        Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> run,
        Action invalidateCapabilities)
    {
        _catalog = catalog;
        _run = run;
        _invalidateCapabilities = invalidateCapabilities;
    }

    internal async Task<FoundryLocalInstallPreflight> PreflightAsync(FoundryLocalAuditedPackageSet package, CancellationToken ct)
    {
        if (!ReferenceEquals(package, _catalog.Find(package.Id, DateTimeOffset.UtcNow)))
            return new(false, false, "", "Package approval changed; no preparation attempted.");
        var start = BuildProcess(PreflightScript);
        start.Environment["WSLCD_FOUNDRY_VCLIBS_VERSION"] = package.VcLibs.Version;
        var result = await _run(start, ct);
        ct.ThrowIfCancellationRequested();
        if (!result.Success)
            return new(false, false, "", "Windows package preflight failed; no downloads or installation requested.");
        var lines = result.StandardOutput.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.SequenceEqual(["@@WSLCD_FOUNDRY_PREFLIGHT=PRESENT"]))
            return new(false, false, "", "An existing Foundry package or standalone CLI was found. No upgrade, adoption or download attempted; use discovery/manual connection.");
        if (lines.Length != 2 || lines[0] != "@@WSLCD_FOUNDRY_PREFLIGHT=ABSENT")
            return new(false, false, "", "Unrecognized package preflight output. Inspect installation manually; nothing downloaded.");
        if (lines[1] == "@@WSLCD_VCLIBS=REQUIRED")
            return new(true, true, "", "The pinned prerequisite needs registration.");
        const string preserve = "@@WSLCD_VCLIBS=PRESERVE:";
        if (lines[1].StartsWith(preserve, StringComparison.Ordinal)
            && Version.TryParse(lines[1][preserve.Length..], out var version)
            && version >= Version.Parse(package.VcLibs.Version))
            return new(true, false, version.ToString(), "Existing Microsoft x64 VCLibs will be preserved; its presence is not proof of Foundry inference compatibility.");
        return new(false, false, "", "Unrecognized prerequisite state; nothing downloaded.");
    }

    public async Task<FoundryLocalInstallResult> InstallRuntimeOnlyAsync(string approvedPackageSet,
        string runtimePath, string? dependencyPath, AiChatConfiguration original,
        Func<string, CancellationToken, Task<bool>> confirm, Func<bool> isCurrent, IProgress<string>? progress, CancellationToken ct)
    {
        var entered = false;
        var mutationAttempted = false;
        try
        {
            await _gate.WaitAsync(ct);
            entered = true;
            var package = _catalog.Find(approvedPackageSet, DateTimeOffset.UtcNow);
            if (package is null)
                return new(FoundryLocalInstallState.Blocked, "No eligible independently audited package set. No files, confirmation or installation were requested.");
            if (original.Kind != AiProviderKind.FoundryLocal || !isCurrent())
                return new(FoundryLocalInstallState.Cancelled, "Original Foundry Local configuration changed.");
            if (!CanDisplayModelSelection(original.Model))
                return new(FoundryLocalInstallState.Blocked, "Clear or correct the model selection before setup: approval requires at most 512 characters without control/format characters. No installation requested.");
            progress?.Report("Verifying prepared runtime and dependency bytes against the approved audit…");
            // Hold read-only, non-delete-sharing handles until deployment exits. Neither the
            // approved bytes nor their paths may be replaced while confirmation is open.
            await using var runtime = await OpenVerifiedAsync(runtimePath, package.Runtime, ".msix", ct);
            await using var dependency = dependencyPath is null ? null
                : await OpenVerifiedAsync(dependencyPath, package.VcLibs, ".appx", ct);
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return new(FoundryLocalInstallState.Cancelled, "Original configuration changed; nothing installed.");
            progress?.Report("Checking explicit runtime-only installation consent...");
            if (!await confirm(package.Confirmation(original.Model) + "\n" + InitializationGuidance, ct))
                return new(FoundryLocalInstallState.Declined, "Runtime-only installation declined; nothing installed.");
            ct.ThrowIfCancellationRequested();
            if (!isCurrent()) return new(FoundryLocalInstallState.Cancelled, "Approval invalidated by configuration change; nothing installed.");
            progress?.Report("Installing the exact prepared user-scope MSIX and dependency; no server start or model setup…");
            var start = BuildStartInfo(runtime.Name, dependency?.Name, package);
            ct.ThrowIfCancellationRequested();
            _invalidateCapabilities();
            mutationAttempted = true;
            var result = await _run(start, ct);
            ct.ThrowIfCancellationRequested();
            if (!isCurrent())
                return new(FoundryLocalInstallState.Cancelled, "Configuration changed during Windows deployment; the runtime may be installed. Settings were not changed. Inspect packages before retrying.");
            if (!result.Success || !result.StandardOutput.Split('\n').Any(line => line.Trim() == "@@WSLCD_FOUNDRY_INSTALLED"))
                return new(FoundryLocalInstallState.Failed, "Windows deployment did not confirm the exact runtime version. No retry or uninstall attempted; inspect packages.");
            return new(FoundryLocalInstallState.Installed, "Exact runtime package registered for this user; Microsoft x64 VCLibs prerequisite observed (existing newer versions preserved). No server started or settings changed by this runtime-only step. Registration alone is not model readiness. " + InitializationGuidance);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is represented in the public result, including the deployment race.
            return new(FoundryLocalInstallState.Cancelled, mutationAttempted
                ? "Installation cancelled; Windows deployment may still complete. No rollback or uninstall attempted. Inspect packages before retrying."
                : "Preparation cancelled. No installation requested.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            // Paths/process diagnostics may contain private data. Return only the failure class.
            return new(FoundryLocalInstallState.Failed, $"Prepared installation failed ({ex.GetType().Name}). No automatic retry, download or cleanup attempted.");
        }
        finally
        {
            if (entered) _gate.Release();
            if (mutationAttempted) _invalidateCapabilities();
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string runtimePath, string? dependencyPath, FoundryLocalAuditedPackageSet package)
    {
        var start = BuildProcess(InstallScript);
        start.Environment["WSLCD_FOUNDRY_MSIX"] = runtimePath;
        start.Environment["WSLCD_FOUNDRY_VCLIBS"] = dependencyPath ?? "";
        start.Environment["WSLCD_FOUNDRY_VERSION"] = package.Runtime.Version;
        start.Environment["WSLCD_FOUNDRY_VCLIBS_VERSION"] = package.VcLibs.Version;
        return start;
    }

    internal static bool CanDisplayModelSelection(string model) => model.Length <= 512
        && !model.Any(character => char.IsControl(character)
            || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);

    private static ProcessStartInfo BuildProcess(string script)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<FileStream> OpenVerifiedAsync(string path, FoundryLocalAuditedArtifact artifact,
        string extension, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A prepared local package path is required.");
        var canonical = Path.GetFullPath(path);
        for (var item = canonical; item is not null; item = Path.GetDirectoryName(item))
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not allowed for prepared packages.");
        var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            if (stream.Length != artifact.Bytes
                || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)),
                    artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Prepared package bytes do not match the approved artifact.");
            return stream;
        }
        catch
        {
            // Preserve the failure while releasing the verification handle.
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public enum FoundryLocalInstallState { Blocked, Declined, Cancelled, Failed, Installed }
public sealed record FoundryLocalInstallResult(FoundryLocalInstallState State, string Guidance);
internal sealed record FoundryLocalInstallPreflight(bool CanInstall, bool NeedsVcLibs, string ExistingVcLibsVersion, string Guidance);
