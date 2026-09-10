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
/// Code-reviewed allowlist, not editable settings or an imported user audit. The known
/// 0.10.3.0 user-scope MSIX hash alone does not audit VCLibs, bundled dependencies, or EPs.
/// No production package set is eligible until that complete provenance is committed here.
/// </summary>
public sealed class FoundryLocalArtifactCatalog
{
    private readonly FoundryLocalAuditedPackageSet[] _approved;
    public FoundryLocalArtifactCatalog() => _approved = [];
    internal FoundryLocalArtifactCatalog(params FoundryLocalAuditedPackageSet[] approved) =>
        _approved = approved.ToArray();

    public bool HasEligibleRuntime => _approved.Any(package => package.IsEligible(DateTimeOffset.UtcNow));
    internal FoundryLocalAuditedPackageSet? Find(string id, DateTimeOffset now) =>
        _approved.SingleOrDefault(package => package.Id == id && package.IsEligible(now));
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
    FoundryLocalAuditedArtifact VcLibs, Uri CompleteBundledDependencyAudit)
{
    internal bool IsEligible(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(Id) && Runtime.IsEligible(now) && VcLibs.IsEligible(now)
        && CompleteBundledDependencyAudit.IsAbsoluteUri && CompleteBundledDependencyAudit.Scheme == "https";

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
