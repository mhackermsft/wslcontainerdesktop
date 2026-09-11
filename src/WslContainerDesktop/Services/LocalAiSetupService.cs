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
using System.ComponentModel;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <inheritdoc cref="ILocalAiSetupService"/>
public sealed class LocalAiSetupService(IWslcService wslc, IWslcCapabilitiesService engineCapabilities,
    IAiCapabilityService aiCapabilities, ILogger<LocalAiSetupService> logger) : ILocalAiSetupService
{
    private const string ImageReference = "ollama/ollama";
    private const string ManagedContainerName = "wslcd-ollama";
    private const string ModelVolumeName = "wslcd-ollama";
    internal const string OwnerLabel = "com.wslcontainerdesktop.managed";
    internal const string OperationLabel = "com.wslcontainerdesktop.local-ai.operation";
    internal const string VolumeLabel = "com.wslcontainerdesktop.local-ai.volume";
    private const int Port = 11434;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string ContainerName => ManagedContainerName;

    public int HostPort => Port;

    public async Task<LocalAiSetupResult> EnsureOllamaContainerAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var operation = Guid.NewGuid().ToString("N");
        string? createdId = null;
        var creationAttempted = false;
        var runtime = LocalRuntimeResourceState.Unknown;
        var modelData = LocalRuntimeResourceState.Unknown;
        try
        {
            var existing = await FindContainerAsync(ct).ConfigureAwait(false);
            runtime = existing is null ? LocalRuntimeResourceState.Absent : LocalRuntimeResourceState.Retained;
            var volume = await FindVolumeAsync(ct).ConfigureAwait(false);
            modelData = volume is null ? LocalRuntimeResourceState.Absent : LocalRuntimeResourceState.Retained;
            if (existing is not null)
            {
                Require(volume is not null, "The runtime's model volume is missing. No existing runtime was started.");
                await VerifyMountAsync(existing, volume!, ct).ConfigureAwait(false);
                if (!existing.Running)
                {
                    Require(existing.CanStart, "Runtime state is unknown or not startable. Inspect it before retrying.");
                    aiCapabilities.Invalidate();
                    progress?.Report("Starting the ownership-verified Ollama container...");
                    await RecheckAsync(existing, volume!, ct).ConfigureAwait(false);
                    Check(await wslc.StartContainerAsync(existing.Id, ct).ConfigureAwait(false), "Runtime start");
                    var started = await ReadContainerAsync(existing.Id, ct).ConfigureAwait(false);
                    Require(started.Id == existing.Id && started.Operation == existing.Operation && started.Running,
                        "Start was not confirmed. The existing runtime and models were retained; inspect before retrying.");
                }
                return new(true, existing.Running ? LocalAiContainerState.AlreadyRunning : LocalAiContainerState.StartedExisting,
                    "Owned Ollama container is running. API readiness and model capabilities require separate observation.",
                    existing.Id, LocalRuntimeResourceState.Retained, modelData);
            }

            var capabilities = await engineCapabilities.GetAsync(ct).ConfigureAwait(false);
            var gpu = capabilities[WslcFeature.CreateGpus].Support;
            Require(gpu != WslcCapabilitySupport.Unknown,
                "GPU creation support is unknown. Check the configured WSLC executable and its create help; no runtime was created.");
            Require(capabilities[WslcFeature.CreatePull].Support == WslcCapabilitySupport.Supported,
                "Cached-only creation cannot be guaranteed: WSLC create --pull support is unavailable or unknown. " +
                "Existing verified runtimes can still be reused. Prepare the runtime manually with an age-audited image; no automatic download was attempted.");
            var image = await FindImageAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            aiCapabilities.Invalidate();
            if (volume is null)
            {
                modelData = LocalRuntimeResourceState.Unknown;
                Check(await wslc.CreateVolumeAsync(ModelVolumeName,
                    labels: new Dictionary<string, string> { [OwnerLabel] = "local-ai", [OperationLabel] = operation },
                    ct: ct).ConfigureAwait(false), "Model volume creation");
                volume = await FindVolumeAsync(ct).ConfigureAwait(false);
                Require(volume?.Operation == operation,
                    "Model volume creation could not be attributed to this operation. Data was not deleted.");
                modelData = LocalRuntimeResourceState.Retained;
            }

            // Create separately so ownership and the actual mounted volume are checked before any workload starts.
            Require(await FindContainerAsync(ct).ConfigureAwait(false) is null,
                "A runtime appeared during preparation. It was not adopted or removed; retry after inspection.");
            Require(await FindVolumeAsync(ct).ConfigureAwait(false) == volume, "Model volume changed during preparation.");
            progress?.Report(gpu == WslcCapabilitySupport.Supported
                ? "Creating Ollama with GPU access requested..."
                : "WSLC create definitively lacks GPU support; creating Ollama for CPU use...");
            creationAttempted = true;
            ct.ThrowIfCancellationRequested();
            aiCapabilities.Invalidate();
            var creation = await wslc.CreateContainerAsync(BuildOptions(image, gpu == WslcCapabilitySupport.Supported,
                operation, volume!.Operation), ct).ConfigureAwait(false);
            var returnedId = creation.StandardOutput.Trim();
            if (IsContainerId(returnedId))
                createdId = returnedId;
            Check(creation, "Runtime creation");
            var created = createdId is null ? await FindContainerAsync(ct).ConfigureAwait(false)
                : await ReadContainerAsync(createdId, ct).ConfigureAwait(false);
            Require(created?.Operation == operation, "Creation identity is uncertain. No replacement will be started or removed.");
            createdId = created!.Id;
            await RecheckAsync(created, volume, ct).ConfigureAwait(false);
            aiCapabilities.Invalidate();
            Check(await wslc.StartContainerAsync(createdId, ct).ConfigureAwait(false), "Runtime start");
            var running = await ReadContainerAsync(createdId, ct).ConfigureAwait(false);
            Require(running.Operation == operation && running.Running, "Runtime start was not confirmed.");
            await VerifyMountAsync(running, volume, ct).ConfigureAwait(false);
            return new(true, gpu == WslcCapabilitySupport.Supported
                ? LocalAiContainerState.CreatedWithGpu : LocalAiContainerState.CreatedCpuOnly,
                "Owned Ollama container is running. GPU access is not proof of acceleration; API/model readiness is checked separately.",
                createdId, LocalRuntimeResourceState.Retained, modelData);
        }
        catch (Exception ex) when (IsLifecycleFailure(ex))
        {
            logger.LogWarning("Local AI setup stopped ({FailureType}); no failed mutation will be retried.", ex.GetType().Name);
            var cleanup = creationAttempted
                ? await CleanupAsync(operation, createdId).ConfigureAwait(false)
                : (runtime, "No automatic runtime cleanup was attempted.");
            var reason = ex is LifecycleException ? ex.Message
                : ex is OperationCanceledException ? "Setup cancelled; an interrupted engine operation may have partially completed."
                : "Runtime setup could not be confirmed. Inspect engine state before retrying.";
            return new(false, ex is OperationCanceledException ? LocalAiContainerState.Cancelled : LocalAiContainerState.Failed,
                $"{reason} {cleanup.Item2} Model data was not deleted ({modelData}). " +
                "Recovery: inspect wslcd-ollama labels and immutable container ID; retry setup only after resolving conflicts." +
                (creationAttempted ? $" Creation operation: {operation}; observed container ID: {createdId ?? "unknown"}." : ""),
                createdId, cleanup.Item1, modelData);
        }
        finally
        {
            aiCapabilities.Invalidate();
            _gate.Release();
        }
    }

    public async Task<LocalAiRemovalResult> RemoveOllamaContainerAsync(bool removeModelVolume, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var runtime = LocalRuntimeResourceState.Unknown;
        var data = LocalRuntimeResourceState.Unknown;
        try
        {
            var existing = await FindContainerAsync(ct).ConfigureAwait(false);
            var volume = await FindVolumeAsync(ct).ConfigureAwait(false);
            data = volume is null ? LocalRuntimeResourceState.Absent : LocalRuntimeResourceState.Retained;
            runtime = existing is null ? LocalRuntimeResourceState.Absent : LocalRuntimeResourceState.Retained;
            if (existing is not null)
            {
                // Removal only needs confirmed ownership and an immutable ID. Mount/port checks guard
                // starting a workload; requiring them here would block the very recovery action a user
                // needs when a runtime is misconfigured.
                var current = await ReadContainerAsync(existing.Id, ct).ConfigureAwait(false);
                Require(current.Id == existing.Id, "Runtime identity changed. Nothing was removed.");
                aiCapabilities.Invalidate();
                runtime = LocalRuntimeResourceState.Unknown;
                Check(await wslc.RemoveContainerAsync(existing.Id, force: true, ct).ConfigureAwait(false), "Runtime removal");
                Require(!await ContainsIdAsync(existing.Id, ct).ConfigureAwait(false), "Runtime removal is not confirmed.");
                runtime = LocalRuntimeResourceState.Removed;
            }
            // Delete the app's own model volume when asked. The engine deletes volumes by name, and
            // this name is app-specific, so re-inspect immediately before deleting and accept that
            // narrow window rather than stranding gigabytes of model data on every removal.
            if (removeModelVolume && volume is not null)
            {
                data = LocalRuntimeResourceState.Unknown;
                var confirmed = await FindVolumeAsync(ct).ConfigureAwait(false);
                if (confirmed is null)
                {
                    data = LocalRuntimeResourceState.Absent;
                }
                else if (confirmed != volume)
                {
                    // A different volume now holds this name; deleting it would destroy data this
                    // operation never inspected.
                    return new(false, runtime, LocalRuntimeResourceState.Retained,
                        $"Runtime: {runtime}. The model volume changed during removal, so it could not be deleted. " +
                        "Inspect 'wslcd-ollama' in Volumes before deleting it.");
                }
                else
                {
                    var removal = await wslc.RemoveVolumeAsync(ModelVolumeName, ct).ConfigureAwait(false);
                    var stillPresent = await FindVolumeAsync(ct).ConfigureAwait(false) is not null;
                    if (!removal.Success || stillPresent)
                    {
                        return new(false, runtime, LocalRuntimeResourceState.Retained,
                            $"Runtime: {runtime}. The model data could not be deleted" +
                            (stillPresent ? " and is still present." : ".") +
                            " Remove the 'wslcd-ollama' volume from the Volumes page if you want that space back.");
                    }
                    data = LocalRuntimeResourceState.Removed;
                }
            }
            return new(true, runtime, data, removeModelVolume
                ? $"Removed the Ollama container and its downloaded models."
                : $"Removed the Ollama container. Downloaded models were kept and will be reused if you set it up again.");
        }
        catch (Exception ex) when (IsLifecycleFailure(ex))
        {
            logger.LogWarning("Local AI removal stopped ({FailureType}); data was not deleted.", ex.GetType().Name);
            return new(false, runtime, data, $"Runtime: {runtime}; model data: {data}, not deleted. " +
                (ex is LifecycleException ? ex.Message : "Removal was interrupted or could not be confirmed. Inspect engine state before retrying."));
        }
        finally
        {
            aiCapabilities.Invalidate();
            _gate.Release();
        }
    }

    public async Task<bool> IsRuntimePresentAsync(CancellationToken ct = default)
    {
        try
        {
            var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            return containers.Any(c => string.Equals(c.Name, ManagedContainerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Presence only drives whether a Remove affordance is offered; an unreadable inventory
            // must not surface as an error here.
            logger.LogDebug(ex, "Local AI runtime presence check failed.");
            return false;
        }
    }

    private static RunContainerOptions BuildOptions(string image, bool useGpu, string operation, string volume) => new()
    {
        Image = image,
        Name = ManagedContainerName,
        Detached = true,
        AllGpus = useGpu,
        NeverPull = true,
        PortMappings = { $"127.0.0.1:{Port}:{Port}" },
        Volumes = { $"{ModelVolumeName}:/root/.ollama" },
        Labels = { [OwnerLabel] = "local-ai", [OperationLabel] = operation, [VolumeLabel] = volume },
    };

    private async Task<string> FindImageAsync(CancellationToken ct)
    {
        var images = await wslc.ListImagesAsync(ct).ConfigureAwait(false);
        var image = images.FirstOrDefault(i => i.Repository is ImageReference or "docker.io/ollama/ollama"
            && i.Tag == "latest" && IsImageId(i.Id));
        Require(image is not null,
            "The Ollama image is not on this machine yet. Pull ollama/ollama:latest from the Images page, then run setup again. " +
            "Setup does not download images on your behalf.");
        return image!.Id;
    }

    private async Task<OwnedContainer?> FindContainerAsync(CancellationToken ct)
    {
        var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var matches = containers.Where(c => string.Equals(c.Name, ManagedContainerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(matches.Length <= 1, "Multiple same-name runtimes conflict; nothing was adopted.");
        return matches.Length == 0 ? null : await ReadContainerAsync(matches[0].Id, ct).ConfigureAwait(false);
    }

    private async Task<OwnedContainer> ReadContainerAsync(string target, CancellationToken ct)
    {
        var result = await wslc.InspectContainerAsync(target, ct).ConfigureAwait(false);
        Check(result, "Runtime ownership inspection");
        var root = Parse(result.StandardOutput);
        var id = Text(root, "Id");
        Require(IsContainerId(id) && ContainerIdentity.ResolveId([id], target) == id,
            "Runtime immutable identity is missing or changed. No adoption or deletion is permitted.");
        Require(Text(root, "Name").TrimStart('/') == ManagedContainerName, "Runtime name changed; inspect before retrying.");
        var labels = Property(Property(root, "Config"), "Labels");
        if (labels.ValueKind == JsonValueKind.Undefined)
            labels = Property(root, "Labels");
        var operation = Text(labels, OperationLabel);
        // The owner label is written only by this app, so it is the ownership anchor. Runtimes created
        // before per-operation IDs carry no operation label; they are still ours and are adopted after the
        // loopback port and model mount are verified below. Anything without the owner label stays foreign.
        Require(Text(labels, OwnerLabel) == "local-ai",
            $"A container named {ManagedContainerName} exists but was not created by this app, so it was not adopted, started, or deleted. " +
            "Remove or rename that container, then retry.");
        Require(operation.Length == 0 || Guid.TryParseExact(operation, "N", out _),
            "The runtime's ownership operation label is malformed. It was not adopted, started, or deleted.");
        var state = Property(root, "State");
        var running = Property(state, "Running");
        var status = state.ValueKind == JsonValueKind.String ? state.GetString() : Text(state, "Status");
        var numericState = state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var number) ? number : -1;
        var isRunning = running.ValueKind == JsonValueKind.True || status == "running" ||
            numericState == (int)ContainerState.Running;
        var canStart = status is "created" or "exited" or "stopped" ||
            numericState is (int)ContainerState.Created or (int)ContainerState.Stopped;
        Require(!(isRunning && canStart) && !(running.ValueKind == JsonValueKind.False && isRunning),
            "Runtime state metadata is contradictory; inspect before retrying.");
        return new(id, operation, Text(labels, VolumeLabel), isRunning, canStart, root);
    }

    private async Task<OwnedVolume?> FindVolumeAsync(CancellationToken ct)
    {
        var volumes = await wslc.ListVolumesAsync(ct).ConfigureAwait(false);
        var matches = volumes.Where(v => v.Name.Equals(ModelVolumeName, StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(matches.Length <= 1, "Multiple model volumes conflict.");
        if (matches.Length == 0)
            return null;
        var result = await wslc.InspectVolumeAsync(ModelVolumeName, ct).ConfigureAwait(false);
        Check(result, "Model volume ownership inspection");
        var root = Parse(result.StandardOutput);
        var labels = Property(root, "Labels");
        var operation = Text(labels, OperationLabel);
        var created = Text(root, "CreatedAt");
        var mountpoint = Text(root, "Mountpoint");
        var owner = Text(labels, OwnerLabel);
        // Volumes created before ownership labels carry none at all; the owning container's verified mount
        // is what ties this volume to the app. Reject only a volume explicitly owned by something else.
        Require(owner is "local-ai" or "",
            $"A volume named {ModelVolumeName} is owned by something else. Data was not adopted or deleted.");
        Require(operation.Length == 0 || Guid.TryParseExact(operation, "N", out _),
            "The model volume's ownership operation label is malformed. Data was not adopted or deleted.");
        Require(Text(root, "Name") == ModelVolumeName && DateTimeOffset.TryParse(created, out _) &&
            !string.IsNullOrWhiteSpace(mountpoint),
            "The same-name model volume lacks verified creation identity. Data was not adopted or deleted.");
        return new(operation, created, mountpoint);
    }

    private async Task RecheckAsync(OwnedContainer container, OwnedVolume volume, CancellationToken ct)
    {
        var current = await ReadContainerAsync(container.Id, ct).ConfigureAwait(false);
        Require(current.Id == container.Id && current.Operation == container.Operation, "Runtime identity changed.");
        await VerifyMountAsync(current, volume, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private async Task VerifyMountAsync(OwnedContainer container, OwnedVolume volume, CancellationToken ct)
    {
        Require(container.Volume == volume.Operation, "Runtime and model volume ownership identities do not match.");
        Require(ContainerPortParser.TryInspect(container.Inspect, new JsonSerializerOptions(), out var ports) &&
            ports.Count == 1 && ports[0].ContainerPort == Port && ports[0].HostPort == Port &&
            ports[0].Protocol == 6 && ports[0].BindingAddress == "127.0.0.1",
            "Runtime endpoint is not the verified loopback-only Ollama port. Configure external runtimes separately.");
        var mounts = ContainerMounts.Parse(container.Inspect);
        // Inspect schemas differ: some report the volume name as the source, others the host mountpoint.
        // Both identify the same named volume, so accept either rather than refusing a valid runtime.
        Require(mounts.IsComplete && mounts.Items.Count == 1 && mounts.Items[0].VolumeName == ModelVolumeName &&
            mounts.Items[0].Destination == "/root/.ollama" &&
            (mounts.Items[0].Source == volume.Mountpoint || mounts.Items[0].Source == ModelVolumeName),
            "Runtime model mount identity is missing or unexpected. It was not started or adopted.");
        Require(await FindVolumeAsync(ct).ConfigureAwait(false) == volume,
            "Model volume was replaced during the operation. No workload will be started.");
    }

    private async Task<(LocalRuntimeResourceState, string)> CleanupAsync(string operation, string? id)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var candidate = id is null
                ? await FindContainerAsync(cleanup.Token).ConfigureAwait(false)
                : await ReadContainerAsync(id, cleanup.Token).ConfigureAwait(false);
            if (candidate is null)
                return (LocalRuntimeResourceState.Unknown, "No partial runtime was observed; an interrupted creation may still finish. Inspect before retrying.");
            if (candidate.Operation != operation)
                return (LocalRuntimeResourceState.Unknown, "A different runtime was retained; this operation does not own it.");
            Require(id is null || candidate.Id == id, "Cleanup identity changed.");
            aiCapabilities.Invalidate();
            Check(await wslc.RemoveContainerAsync(candidate.Id, force: true, cleanup.Token).ConfigureAwait(false), "Partial runtime cleanup");
            Require(!await ContainsIdAsync(candidate.Id, cleanup.Token).ConfigureAwait(false), "Partial runtime cleanup was not confirmed.");
            return (LocalRuntimeResourceState.Removed, "Only this operation's verified partial runtime was removed.");
        }
        catch (Exception ex) when (IsLifecycleFailure(ex))
        {
            logger.LogWarning("Local AI partial cleanup could not be confirmed ({FailureType}).", ex.GetType().Name);
            return (LocalRuntimeResourceState.Unknown, "Partial runtime cleanup could not be confirmed; inspect before retrying.");
        }
    }

    private async Task<bool> ContainsIdAsync(string id, CancellationToken ct) =>
        (await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false))
            .Any(c => ContainerIdentity.ResolveId([c.Id], id) is not null);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
            root = root[0];
        Require(root.ValueKind == JsonValueKind.Object, "Ownership inspection was not a single resource.");
        ValidateProperties(root);
        return root.Clone();
    }

    private static void ValidateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "Ownership inspection contains ambiguous duplicate properties.");
                ValidateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                ValidateProperties(item);
    }

    private static JsonElement Property(JsonElement root, string name) => ContainerInfoJsonConverter.Property(root, name);
    private static string Text(JsonElement root, string name) => ContainerInfoJsonConverter.ReadString(root, name);
    private static bool IsImageId(string id) => id.StartsWith("sha256:", StringComparison.Ordinal)
        ? IsImageDigest(id[7..])
        : IsImageDigest(id);

    // Image listings report the engine's short content-addressed ID (12 hex digits), while inspect
    // returns the full digest. Accept either; requiring only the long form makes cached images invisible.
    private static bool IsImageDigest(string id) =>
        id.Length is >= 12 and <= 64 && id.All(char.IsAsciiHexDigit);

    private static bool IsContainerId(string id) => IsHash(id) || Guid.TryParse(id, out _);
    private static bool IsHash(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigit);
    private static void Check(CommandResult result, string operation) =>
        Require(result.Success, $"{operation} failed or was interrupted. No automatic CPU retry will be attempted.");
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new LifecycleException(message);
    }
    private static bool IsLifecycleFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or Win32Exception or JsonException or OperationCanceledException or TimeoutException;
    private sealed class LifecycleException(string message) : InvalidOperationException(message);
    private sealed record OwnedVolume(string Operation, string CreatedAt, string Mountpoint);
    private sealed record OwnedContainer(string Id, string Operation, string Volume, bool Running, bool CanStart, JsonElement Inspect);
}
