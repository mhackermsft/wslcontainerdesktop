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
    FoundryLocalArtifactCatalog? catalog = null)
{
    public const string InstallationGuidance =
        "Standalone installation unavailable: no eligible independently audited standalone installer is approved in this app. " +
        "The published Windows package installs for the current user, not machine-wide. " +
        "An exact installer, all dependencies and execution providers require authoritative publication dates (at least seven days old), " +
        "integrity, size and license evidence before a single installation confirmation can be offered. " +
        "The app does not run an unpinned winget install, accept user-entered audit claims, or install an in-process SDK. " +
        "Initial-model download/load is also blocked: CLI model commands can implicitly acquire or update EPs. " +
        "An existing externally prepared server can be discovered and connected without installing, starting, stopping or adopting it.";

    public bool CanInstall => (catalog ?? new FoundryLocalArtifactCatalog()).HasEligibleRuntime;

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
        $"\nCached: {Inventory.IsCached}; loaded: {Inventory.IsLoaded}.\n" +
        "Only this app's endpoint setting changes. Model selection is preserved. No software, model or EP " +
        "is installed/downloaded; no license is accepted. Network: later explicit metadata/inference requests " +
        "use this loopback endpoint. No runtime is started, stopped, uninstalled or adopted. " +
        "Advertised metadata is not an artifact audit or proof of hardware/inference readiness.",
        AiTextSanitizer.DiagnosticLimit);
}
