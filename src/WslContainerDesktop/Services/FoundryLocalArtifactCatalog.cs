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

using System.Text.RegularExpressions;

namespace WslContainerDesktop.Services;

/// <summary>
/// Code-reviewed package-registration allowlist, not editable settings or an imported audit.
/// Immutable archive hashes/publication dates cover every embedded binary. This does not
/// approve runtime initialization, additional native downloads, execution providers or models.
/// See docs/FOUNDRY-LOCAL.md for archive inspection, identities and remaining runtime gates.
/// </summary>
public sealed class FoundryLocalArtifactCatalog
{
    private readonly FoundryLocalAuditedPackageSet[] _approved;
    public const string StandalonePackageSetId = "foundry-local-cli-0.10.3-x64";

    public FoundryLocalArtifactCatalog() => _approved =
    [
        new(StandalonePackageSetId,
            new("0.10.3.0", 29982055,
                "86A01C52265BD9C9167C1F8A04F34621A2A63EC5C8276166C2EECC8C6A56553F",
                new(2026, 8, 7, 21, 38, 48, TimeSpan.Zero),
                new("https://github.com/microsoft/Foundry-Local/releases/expanded_assets/cli-preview-0.10.3"),
                "Microsoft Foundry Local CLI license terms (proprietary; separate installation, not redistribution)",
                new("https://github.com/microsoft/Foundry-Local/blob/da50cfea8a43d22a63214f8bd9e58949a5177fb0/LICENSE")),
            new("14.0.33728.0", 6757465,
                "077A3D1A5D0622BD3004DCA85F5E192D6E98EC79B83D4AA06766759EA6C09C3D",
                new(2024, 11, 1, 16, 37, 27, TimeSpan.Zero),
                new("https://github.com/microsoft/winget-cli/releases/expanded_assets/v1.9.25180"),
                "Microsoft Visual C++ 2015-2022 Runtime terms (Cpp_2015-2022_ENU.1033)",
                new("https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/")),
            new("https://github.com/microsoft/Foundry-Local/releases/tag/cli-preview-0.10.3"),
            new("https://github.com/microsoft/Foundry-Local/releases/download/cli-preview-0.10.3/foundry-0.10.3-win-x64-winml.msix"),
            new(new("https://github.com/microsoft/winget-cli/releases/download/v1.9.25180/DesktopAppInstaller_Dependencies.zip"),
                50182148, "EEBA62F08531C8669B3E5FB895CE8800AE66D798739CDA2B4AEF2A1D80A5F3C5",
                new(2024, 11, 1, 16, 37, 27, TimeSpan.Zero),
                new("https://github.com/microsoft/winget-cli/releases/expanded_assets/v1.9.25180"),
                "x64/Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx"))
    ];
    internal FoundryLocalArtifactCatalog(params FoundryLocalAuditedPackageSet[] approved) =>
        _approved = approved.ToArray();

    public bool HasEligibleRuntime => _approved.Any(package => package.IsEligible(DateTimeOffset.UtcNow));
    internal FoundryLocalAuditedPackageSet? Find(string id, DateTimeOffset now) =>
        _approved.SingleOrDefault(package => package.Id == id && package.IsEligible(now));
    internal FoundryLocalAuditedPackageSet? GetStandalone() => Find(StandalonePackageSetId, DateTimeOffset.UtcNow);
}

// Only compiled, independently reviewed catalog entries can carry provenance. No public
// constructor/parser accepts claimed timestamps, checksums, licenses or audit acknowledgements.
internal sealed record FoundryLocalAuditedArtifact(string Version, long Bytes, string Sha256,
    DateTimeOffset PublishedAt, Uri PublicationEvidence, string License, Uri LicenseEvidence)
{
    internal bool IsEligible(DateTimeOffset now) =>
        System.Version.TryParse(Version, out _) && Bytes > 0
        && Regex.IsMatch(Sha256, @"\A[0-9a-fA-F]{64}\z")
        && PublishedAt > DateTimeOffset.UnixEpoch && PublishedAt <= now.AddDays(-7)
        && !string.IsNullOrWhiteSpace(License)
        && PublicationEvidence.IsAbsoluteUri && PublicationEvidence.Scheme == "https"
        && LicenseEvidence.IsAbsoluteUri && LicenseEvidence.Scheme == "https";
}

internal sealed record FoundryLocalAuditedPackageSet(string Id, FoundryLocalAuditedArtifact Runtime,
    FoundryLocalAuditedArtifact VcLibs, Uri CompleteBundledDependencyAudit,
    Uri? RuntimeDownloadUri = null, FoundryLocalAuditedArchive? VcLibsArchive = null)
{
    internal bool IsEligible(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(Id) && Runtime.IsEligible(now) && VcLibs.IsEligible(now)
        && CompleteBundledDependencyAudit.IsAbsoluteUri && CompleteBundledDependencyAudit.Scheme == "https";

    internal bool IsDownloadable(DateTimeOffset now) => IsEligible(now)
        && RuntimeDownloadUri is { IsAbsoluteUri: true, Scheme: "https", Host: "github.com" }
        && string.IsNullOrEmpty(RuntimeDownloadUri.UserInfo)
        && VcLibsArchive is not null && VcLibsArchive.IsEligible(now);

    internal string Confirmation(string model) => AiTextSanitizer.Sanitize(
        $"Install only the standalone Foundry Local runtime {Runtime.Version} for this Windows user?\n" +
        $"Runtime: {Runtime.Bytes} bytes; SHA256 {Runtime.Sha256}; published {Runtime.PublishedAt:O}.\n" +
        $"License: {Runtime.License} ({Runtime.LicenseEvidence}). Evidence: {Runtime.PublicationEvidence}.\n" +
        $"VCLibs dependency: {VcLibs.Version}; {VcLibs.Bytes} bytes; SHA256 {VcLibs.Sha256}; published {VcLibs.PublishedAt:O}.\n" +
        $"License: {VcLibs.License} ({VcLibs.LicenseEvidence}). Evidence: {VcLibs.PublicationEvidence}.\n" +
        $"Bundled dependency audit: {CompleteBundledDependencyAudit}.\n" +
        $"Selected model (unchanged): {model}. Model/EP version, size and license: unaudited; download/load BLOCKED.\n" +
        "This is runtime-only installation, NOT working initial-model setup. You accept the listed runtime/dependency terms only. " +
        "Prepared local files only; no package/model/EP downloads are requested. Windows may use network access for signature trust checks. " +
        "No existing Foundry installation will knowingly be upgraded/adopted; no server start, stop, uninstall or forced application shutdown. " +
        "Cancellation is not rollback: Windows deployment may finish after cancellation. Inspect installed packages before retrying.",
        AiTextSanitizer.DiagnosticLimit);
}

internal sealed record FoundryLocalAuditedArchive(Uri DownloadUri, long Bytes, string Sha256,
    DateTimeOffset PublishedAt, Uri PublicationEvidence, string EntryPath)
{
    internal bool IsEligible(DateTimeOffset now) =>
        Bytes > 0 && Regex.IsMatch(Sha256, @"\A[0-9a-fA-F]{64}\z")
        && PublishedAt > DateTimeOffset.UnixEpoch && PublishedAt <= now.AddDays(-7)
        && DownloadUri is { IsAbsoluteUri: true, Scheme: "https", Host: "github.com" }
        && string.IsNullOrEmpty(DownloadUri.UserInfo)
        && PublicationEvidence.IsAbsoluteUri && PublicationEvidence.Scheme == "https"
        && EntryPath.StartsWith("x64/", StringComparison.Ordinal)
        && EntryPath.EndsWith(".appx", StringComparison.Ordinal)
        && !EntryPath.Contains("..", StringComparison.Ordinal)
        && !EntryPath.Contains('\\');
}
