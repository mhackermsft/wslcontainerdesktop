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

using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Establishes all required endpoints before starting a newly created workload. Re-adoption only
/// adds missing endpoints; it never rewrites or removes pre-existing endpoint configuration.
/// </summary>
public sealed class ComposeNetworkOrchestrator(IWslcService wslc, ILogger logger)
{
    private const string OperationLabel = "com.wsldesktop.network-operation";

    public static bool SelectNative(RunContainerOptions options, WslcCapabilities capabilities, out string? warning)
    {
        warning = null;
        var endpoints = options.GetNetworkAttachments();
        if (endpoints.Count < 2)
        {
            return false;
        }

        var capability = capabilities[WslcFeature.NetworkConnect];
        switch (capability.Support)
        {
            case WslcCapabilitySupport.Supported:
                return true;
            case WslcCapabilitySupport.Unsupported:
                warning = $"Only network '{endpoints[0].Network}' will be attached: this WSLC does not support " +
                    $"network connect. Networks {string.Join(", ", endpoints.Skip(1).Select(n => $"'{n.Network}'"))} " +
                    "and their aliases/IP settings remain saved but cannot be honored.";
                return false;
            default:
                throw new InvalidOperationException($"Cannot determine multi-network support: {capability.Diagnostic}");
        }
    }

    public async Task<string> CreateAndStartAsync(RunContainerOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        var request = options.Clone();
        var operation = Guid.NewGuid().ToString("N");
        request.Labels[OperationLabel] = operation;
        var complete = false;
        Exception? failure = null;
        try
        {
            RequireSuccess(await wslc.CreateContainerAsync(request, ct).ConfigureAwait(false), "Create container");
            var state = await InspectAsync(request.Name!, ct).ConfigureAwait(false);
            if (!state.HasLabel(OperationLabel, operation))
            {
                throw new InvalidOperationException("Created container ownership could not be verified.");
            }

            await EnsureAttachmentsAsync(state.Id, request.GetNetworkAttachments(), rollback: false, ct).ConfigureAwait(false);
            RequireSuccess(await wslc.StartContainerAsync(state.Id, ct).ConfigureAwait(false), "Start container");
            complete = true;
            return state.Id;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            if (!complete)
            {
                // A failed/cancelled create may have committed remotely. Inspect the unique operation
                // label before removing anything, rather than deleting a name that another caller owns.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var result = await wslc.InspectContainerAsync(request.Name!, cleanup.Token).ConfigureAwait(false);
                    if (result.Success)
                    {
                        var state = ContainerNetworkState.Parse(result.StandardOutput);
                        if (state.HasLabel(OperationLabel, operation))
                        {
                            RequireSuccess(await wslc.RemoveContainerAsync(state.Id, force: true, cleanup.Token)
                                .ConfigureAwait(false), "Remove partially configured container");
                        }
                    }
                    else if (!result.ErrorText.Contains("WSLC_E_CONTAINER_NOT_FOUND", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Cannot inspect partial container: {result.ErrorText}");
                    }
                }
                catch (Exception cleanupError)
                {
                    logger.LogError(cleanupError, "Network startup cleanup failed for {Name}", request.Name);
                    throw new InvalidOperationException(
                        $"{failure?.Message} Cleanup failed for '{request.Name}': {cleanupError.Message}", failure);
                }
            }
        }
    }

    public async Task ReconcileAsync(string id, RunContainerOptions options, WslcCapabilities capabilities, CancellationToken ct)
    {
        var desired = options.GetNetworkAttachments();
        if (desired.Count < 2)
        {
            return;
        }

        if (!SelectNative(options, capabilities, out var warning))
        {
            logger.LogWarning("{Warning}", warning);
            return;
        }

        var state = await InspectAsync(id, ct).ConfigureAwait(false);
        // Preflight every existing endpoint before mutating anything.
        foreach (var endpoint in desired)
        {
            state.RequireCompatible(endpoint);
        }

        if (desired.All(n => state.Networks.ContainsKey(n.Network)))
        {
            return;
        }

        var disconnect = capabilities[WslcFeature.NetworkDisconnect];
        if (disconnect.Support != WslcCapabilitySupport.Supported)
        {
            throw new InvalidOperationException(
                $"Cannot safely repair missing networks without rollback support: {disconnect.Diagnostic}");
        }

        await EnsureAttachmentsAsync(id, desired, rollback: true, ct).ConfigureAwait(false);
    }

    private async Task EnsureAttachmentsAsync(string id, IReadOnlyList<NetworkAttachment> desired, bool rollback, CancellationToken ct)
    {
        var state = await InspectAsync(id, ct).ConfigureAwait(false);
        foreach (var endpoint in desired)
        {
            state.RequireCompatible(endpoint);
        }

        var connected = new List<string>();
        string? pending = null;
        try
        {
            foreach (var endpoint in desired.Where(n => !state.Networks.ContainsKey(n.Network)))
            {
                ct.ThrowIfCancellationRequested();
                pending = endpoint.Network;
                RequireSuccess(await wslc.ConnectNetworkAsync(endpoint, id, ct).ConfigureAwait(false),
                    $"Connect network '{endpoint.Network}'");
                connected.Add(endpoint.Network);
                pending = null;
            }

            var actual = await InspectAsync(id, ct).ConfigureAwait(false);
            foreach (var endpoint in desired)
            {
                if (!actual.Networks.ContainsKey(endpoint.Network))
                {
                    throw new InvalidOperationException($"Required network '{endpoint.Network}' is missing after attachment.");
                }

                actual.RequireCompatible(endpoint);
            }
        }
        catch (Exception ex) when (rollback && (connected.Count > 0 || pending is not null))
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string? unresolved = null;
            try
            {
                var actual = await InspectAsync(id, cleanup.Token).ConfigureAwait(false);
                // A failed connect may race another caller or commit without returning success.
                // Presence alone cannot establish ownership of that endpoint.
                if (pending is not null && actual.Networks.ContainsKey(pending))
                {
                    unresolved = $"Network '{pending}' was preserved because its connection was not confirmed. " +
                        "Inspect the endpoint before retrying; cleanup ownership is unresolved.";
                    logger.LogWarning("{Diagnostic}", unresolved);
                }

                foreach (var network in connected.AsEnumerable().Reverse().Where(actual.Networks.ContainsKey))
                {
                    RequireSuccess(await wslc.DisconnectNetworkAsync(network, id, cleanup.Token).ConfigureAwait(false),
                        $"Roll back network '{network}'");
                }
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException($"{ex.Message} Endpoint rollback failed: {cleanupError.Message}", ex);
            }

            if (unresolved is not null)
            {
                if (ex is OperationCanceledException)
                {
                    throw new OperationCanceledException($"{ex.Message} {unresolved}", ex, ct);
                }

                throw new InvalidOperationException($"{ex.Message} {unresolved}", ex);
            }

            throw;
        }
    }

    public async Task<ContainerNetworkState> InspectAsync(string id, CancellationToken ct)
    {
        var result = await wslc.InspectContainerAsync(id, ct).ConfigureAwait(false);
        RequireSuccess(result, $"Inspect container '{id}'");
        return ContainerNetworkState.Parse(result.StandardOutput);
    }

    private static void RequireSuccess(CommandResult result, string action)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException($"{action}: {result.ErrorText}");
        }
    }
}
