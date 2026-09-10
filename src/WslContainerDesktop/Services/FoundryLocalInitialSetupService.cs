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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>One explicit approval binds runtime installation, pinned files and the initial CPU model.</summary>
public sealed class FoundryLocalInitialSetupService(FoundryLocalCli cli,
    FoundryLocalStandaloneRuntimeService runtime, FoundryLocalSetupService setup,
    FoundryLocalModelArtifacts artifacts, FoundryLocalModelRegistration registration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public const string Consent =
        "Prepare Foundry Local with the pinned Qwen2.5-0.5B CPU model?\n" +
        "On success this app selects the detected loopback endpoint and qwen2.5-0.5b-instruct-generic-cpu:4; other providers are unchanged.\n" +
        "A pre-existing runtime is preserved, never upgraded or uninstalled. A stopped runtime may be started with an OS-assigned port and five-minute daemon idle timeout. " +
        "Only a new app-owned cache directory is populated; existing cache entries are not overwritten or deleted. " +
        "Staging plus registration needs approximately 1.76 GB of model disk space, in addition to the runtime and metadata. " +
        "Setup loads this model and sends one synthetic 'Reply OK only' prompt locally. No project data is sent. Other models are not unloaded.\n" +
        FoundryLocalStandaloneRuntimeService.AcquisitionGuidance + "\n" +
        "Cancellation is not rollback: registered packages, completed/partial files, and a started daemon or loaded model may remain. No automatic uninstall, stop or deletion.\n";

    public async Task<FoundryLocalInitialSetupResult> PrepareAsync(AiChatConfiguration original,
        Func<string, CancellationToken, Task<bool>> confirm, Func<bool> isCurrent,
        IProgress<string>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            void Check()
            {
                ct.ThrowIfCancellationRequested();
                if (original.Kind != AiProviderKind.FoundryLocal || !isCurrent())
                    throw new InvalidOperationException("Original setup approval/configuration changed. No further mutation requested.");
            }
            Check();
            FoundryLocalServerStatus? observed = null;
            if (cli.IsInstalled)
            {
                progress?.Report("Inspecting existing standalone version and process; preserving this installation...");
                observed = await cli.ReadServerStatusAsync(ct);
                Check();
                if (!await confirm(Consent + FoundryLocalModelArtifacts.AcquisitionTerms, ct))
                    return new(false, "Initial-model setup declined. No acquisition, start or load requested.");
            }
            else
            {
                var installation = await setup.InstallRuntimeAsync(original,
                    (details, token) => confirm(details + "\n" + Consent + FoundryLocalModelArtifacts.AcquisitionTerms, token),
                    isCurrent, progress, ct, initialModelSetup: true);
                if (installation.State != FoundryLocalInstallState.Installed)
                    return new(false, installation.Guidance);
            }
            Check();
            var staged = await artifacts.StageAsync(progress, ct);
            Check();
            var cache = await cli.ReadCacheLocationAsync(ct);
            Check();
            await registration.RegisterAsync(staged, cache.Path, progress, ct, createCacheRoot: true);
            Check();
            var status = await cli.ReadServerStatusAsync(ct);
            if (observed is { Running: true })
            {
                if (!SameProcess(observed, status))
                    throw new InvalidOperationException("The existing server changed during preparation. Files retained; review the new server before loading.");
            }
            if (!status.Running)
            {
                progress?.Report("Starting Foundry; Windows may install Microsoft-selected execution-provider packages...");
                Check();
                runtime.InvalidateLoadProof();
                await cli.StartServerAsync(ct);
                status = await cli.ReadServerStatusAsync(ct);
            }
            Check();
            if (!status.Running || status.Endpoints.Count != 1)
                throw new InvalidDataException("No unambiguous running endpoint was observed. Prepared files retained; no endpoint guessed.");
            var target = original with { Endpoint = status.Endpoints[0], Model = FoundryLocalModelArtifacts.ModelId };
            var expectedIdentity = FoundryLocalStandaloneRuntimeService.RuntimeIdentity(status, target.Endpoint);
            progress?.Report("Checking the live model catalog (requires network on standalone 0.10.3)...");
            var inventory = await runtime.ReadInventoryAsync(target, ct);
            if (inventory.RuntimeIdentity != expectedIdentity)
                throw new InvalidOperationException("The prepared server was replaced during catalog inspection. No replacement server will be loaded.");
            if (inventory.Selected is null)
                throw new InvalidDataException("The runtime did not discover the registered model. No catalog alias or different variant will be loaded.");
            Check();
            var loaded = await runtime.LoadRegisteredAsync(target, progress, ct, isCurrent, expectedIdentity);
            Check();
            return new(loaded.IsConfirmed, loaded.Guidance, target);
        }
        catch (OperationCanceledException)
        {
            return new(false, "Setup cancelled. Files/packages retained; a requested start or load may have completed. No automatic rollback, stop or uninstall.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
            or HttpRequestException or AiProviderException or System.Text.Json.JsonException
            or UnauthorizedAccessException or ArgumentException or TimeoutException)
        {
            return new(false, "Initial-model setup did not confirm readiness: " + AiTextSanitizer.Sanitize(ex.Message)
                + " Prepared files/packages are retained. No fallback, automatic retry or cleanup.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FoundryLocalInitialSetupResult> StopAsync(AiChatConfiguration original,
        Func<string, CancellationToken, Task<bool>> confirm, Func<bool> isCurrent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (original.Kind != AiProviderKind.FoundryLocal || !isCurrent())
                return new(false, "Configuration changed; no stop requested.");
            var observed = await cli.ReadServerStatusAsync(ct);
            FoundryLocalStandaloneRuntimeService.RequireMatchingHost(observed, original.Endpoint);
            if (!await confirm($"Stop the shared Foundry server process {observed.Pid}, started {observed.StartedAt:O}, at {original.Endpoint}?\n" +
                "This stops inference for all clients and unloads its models, including those opened outside this app. Packages and model files remain. No server is claimed as app-owned.", ct))
                return new(false, "Stop declined; server preserved.");
            ct.ThrowIfCancellationRequested();
            var current = await cli.ReadServerStatusAsync(ct);
            if (!isCurrent() || !SameProcess(observed, current))
                return new(false, "Server/configuration changed while confirmation was open. No stop requested.");
            runtime.InvalidateLoadProof();
            await cli.StopServerAsync(ct);
            var after = await cli.ReadServerStatusAsync(ct);
            return new(!after.Running, after.Running
                ? "A server is still running. Stop outcome is uncertain; no retry."
                : "Foundry is stopped. Runtime packages and model files retained.");
        }
        finally
        {
            runtime.InvalidateLoadProof();
            _gate.Release();
        }
    }

    private static bool SameProcess(FoundryLocalServerStatus a, FoundryLocalServerStatus b) =>
        a.Running && b.Running && a.Pid == b.Pid && a.StartedAt == b.StartedAt
        && a.Endpoints.SequenceEqual(b.Endpoints, StringComparer.Ordinal);
}

public sealed record FoundryLocalInitialSetupResult(bool Success, string Guidance, AiChatConfiguration? Configuration = null);
