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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Opaque in-process, single-use review handle. Serialization exposes only safe evidence.</summary>
public sealed class ComposeReviewToken
{
    internal ComposeReviewToken(Guid owner, ComposeProject project, ComposeOperationRequest request,
        string stamp, ComposeCompatibilityPreview preview, ComposeReconciliationPlan plan, long maximumStopVersion,
        DateTimeOffset expiresAt)
    {
        Owner = owner;
        Project = project;
        Request = request;
        Stamp = stamp;
        Preview = preview;
        Plan = plan;
        MaximumStopVersion = maximumStopVersion;
        ExpiresAt = expiresAt;
    }

    internal Guid Owner { get; }
    internal ComposeProject Project { get; }
    internal ComposeOperationRequest Request { get; }
    internal string Stamp { get; }
    internal ComposeReconciliationPlan Plan { get; }
    internal long MaximumStopVersion { get; }
    internal int Consumed;
    public ComposeCompatibilityPreview Preview { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public enum ComposePlanValidation { Valid, Blocked, Stale, Cancelled, AlreadyUsed, ForeignToken, Expired }
public enum ComposeReviewOutcomeKind { Applied, PartialFailure, Blocked, Stale, Cancelled, AlreadyUsed, ForeignToken, Expired }

public sealed record ComposeRetainedResource(string Kind, string Name, string State, string Detail);

/// <summary>Safe for UI/assistant audit. Raw execution state is deliberately assembly-internal.</summary>
public sealed class ComposeReviewOutcome
{
    public required ComposeReviewOutcomeKind Kind { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<ComposeServiceResult> Services { get; init; } = [];
    public IReadOnlyList<ComposeRetainedResource> RetainedResources { get; init; } = [];
    public bool AllSucceeded => Kind == ComposeReviewOutcomeKind.Applied;
    internal ComposeUpResult? Execution { get; init; }
    internal ComposeUpResult ToUpResult() => Execution ?? new()
    {
        IsCancelled = Kind == ComposeReviewOutcomeKind.Cancelled,
        Services = [new("Project", false, Message) { Action = ComposeServiceAction.Blocked }],
    };
}

public sealed partial class ComposeProjectSupervisor
{
    private readonly Guid _reviewOwner = Guid.NewGuid();
    private sealed record ReviewEvidence(ComposeReconciliationPlan Plan, ComposeCompatibilityPreview Preview, string Stamp);

    /// <summary>Read-only review of exactly the shared PlanAsync normalization; never persists intent.</summary>
    public async Task<ComposeReviewToken> PrepareReviewAsync(ComposeProject project,
        ComposeOperationRequest? request = null, CancellationToken ct = default)
    {
        var desired = SnapshotProject(project);
        var maximumStopVersion = _suppression?.Version ?? long.MaxValue;
        var operation = SnapshotRequest(request ?? new());
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _capabilities.Invalidate();
            // Resolve and capture evidence together. A plan from before taking the gate must
            // never be approved using a newer inventory/capability stamp.
            var evidence = await ReadReviewEvidenceAsync(desired, operation, ct).ConfigureAwait(false);
            return new(_reviewOwner, desired, operation, evidence.Stamp, evidence.Preview, evidence.Plan,
                maximumStopVersion, _reviewClock.GetUtcNow().AddMinutes(10));
        }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>Advisory validation only. ApplyReviewedAsync always repeats this under the lifecycle gate.</summary>
    public async Task<ComposePlanValidation> ValidateReviewAsync(ComposeReviewToken token, CancellationToken ct = default)
    {
        if (token.Owner != _reviewOwner) return ComposePlanValidation.ForeignToken;
        if (Volatile.Read(ref token.Consumed) != 0) return ComposePlanValidation.AlreadyUsed;
        if (ct.IsCancellationRequested) return ComposePlanValidation.Cancelled;
        if (_reviewClock.GetUtcNow() >= token.ExpiresAt) return ComposePlanValidation.Expired;
        if (!token.Preview.CanApply) return ComposePlanValidation.Blocked;
        try { await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return ComposePlanValidation.Cancelled; }
        try
        {
            if (Volatile.Read(ref token.Consumed) != 0) return ComposePlanValidation.AlreadyUsed;
            _capabilities.Invalidate();
            var evidence = await ReadReviewEvidenceAsync(SnapshotProject(token.Project), token.Request, ct).ConfigureAwait(false);
            return _reviewClock.GetUtcNow() >= token.ExpiresAt ? ComposePlanValidation.Expired :
                !evidence.Preview.CanApply ? ComposePlanValidation.Blocked :
                token.Stamp == evidence.Stamp ? ComposePlanValidation.Valid : ComposePlanValidation.Stale;
        }
        catch (OperationCanceledException) { return ComposePlanValidation.Cancelled; }
        finally { _lifecycleGate.Release(); }
    }

    /// <summary>
    /// Consumes a reviewed snapshot once. Cancellation, refusal, blockers and stale evidence perform
    /// no workload/settings mutations. A caller cannot supply an edited plan or bypass blockers.
    /// </summary>
    public Task<ComposeReviewOutcome> ApplyReviewedAsync(ComposeReviewToken token, bool confirmed,
        bool saveReplicaOverrides = false, CancellationToken ct = default) =>
        ApplyReviewedAsync(token, confirmed, saveReplicaOverrides, null, ct);

    internal async Task<ComposeReviewOutcome> ApplyReviewedAsync(ComposeReviewToken token, bool confirmed,
        bool saveReplicaOverrides, Action<ComposeServiceResult>? onServiceSucceeded, CancellationToken ct)
    {
        if (token.Owner != _reviewOwner) return ReviewRefusal(ComposeReviewOutcomeKind.ForeignToken);
        if (Interlocked.Exchange(ref token.Consumed, 1) != 0) return ReviewRefusal(ComposeReviewOutcomeKind.AlreadyUsed);
        if (!confirmed || ct.IsCancellationRequested) return ReviewRefusal(ComposeReviewOutcomeKind.Cancelled);
        if (_reviewClock.GetUtcNow() >= token.ExpiresAt) return ReviewRefusal(ComposeReviewOutcomeKind.Expired);
        if (!token.Preview.CanApply)
            return new()
            {
                Kind = ComposeReviewOutcomeKind.Blocked,
                Message = "Compatibility checks blocked deployment. Resolve the blockers and review again.",
                Execution = !token.Plan.CanApply ? RejectedPlan(token.Plan) : null,
            };
        var maximumStopVersion = token.MaximumStopVersion;
        try { await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return ReviewRefusal(ComposeReviewOutcomeKind.Cancelled); }
        try
        {
            _capabilities.Invalidate();
            var desired = SnapshotProject(token.Project);
            var evidence = await ReadReviewEvidenceAsync(desired, token.Request, ct).ConfigureAwait(false);
            if (_reviewClock.GetUtcNow() >= token.ExpiresAt) return ReviewRefusal(ComposeReviewOutcomeKind.Expired);
            if (!evidence.Preview.CanApply) return ReviewRefusal(ComposeReviewOutcomeKind.Blocked);
            if (evidence.Stamp != token.Stamp) return ReviewRefusal(ComposeReviewOutcomeKind.Stale);
            ct.ThrowIfCancellationRequested();
            desired.AppliedStateKnown = true;
            if (saveReplicaOverrides && token.Request.Operation == ComposeLifecycleOperation.Up)
            {
                foreach (var pair in token.Request.Replicas) desired.ReplicaOverrides[pair.Key] = pair.Value;
                _store.Save(desired);
            }
            var result = token.Request.Operation == ComposeLifecycleOperation.Up
                ? await UpCoreAsync(desired, maximumStopVersion, ct, token.Request, evidence.Plan, onServiceSucceeded).ConfigureAwait(false)
                : await ExistingCoreAsync(desired, token.Request, maximumStopVersion, ct, evidence.Plan).ConfigureAwait(false);
            var projection = new ComposePreviewProjection(desired);
            return new()
            {
                Kind = result.IsCancelled ? ComposeReviewOutcomeKind.Cancelled :
                    result.AllSucceeded ? ComposeReviewOutcomeKind.Applied : ComposeReviewOutcomeKind.PartialFailure,
                Message = result.IsCancelled ? "Apply cancelled; refresh actual state before retrying." :
                    result.AllSucceeded ? "Reviewed plan applied." : "Apply incomplete; review actual state before retrying.",
                Services = result.Services.Select(s => s with
                {
                    Service = projection.Redact(s.Service),
                    Detail = s.Success ? "Reviewed instance action completed." :
                        "Instance action did not complete. Refresh actual state before retrying; technical values withheld.",
                    Warning = s.Warning is null ? null : "Execution reported a warning; refresh actual state before retrying.",
                    ContainerId = null,
                    Outcome = s.Outcome ?? (s.Success
                        ? s.Action switch
                        {
                            ComposeServiceAction.Keep => ComposeInstanceOutcome.Reused,
                            ComposeServiceAction.Stop => ComposeInstanceOutcome.Stopped,
                            ComposeServiceAction.Remove => ComposeInstanceOutcome.Removed,
                            _ => ComposeInstanceOutcome.Started,
                        }
                        : s.Action == ComposeServiceAction.Blocked ? ComposeInstanceOutcome.Skipped : ComposeInstanceOutcome.Failed),
                }).ToList().AsReadOnly(),
                RetainedResources = token.Request.Operation == ComposeLifecycleOperation.Up
                    ? RetainedReviewResources(desired, evidence.Plan, result.AllSucceeded) : [],
                Execution = result,
            };
        }
        catch (OperationCanceledException) when (onServiceSucceeded is null)
        {
            return new() { Kind = ComposeReviewOutcomeKind.Cancelled,
                Message = "Apply cancelled. Preparation or execution may have completed partially; refresh actual state before retrying.",
                RetainedResources = token.Request.Operation == ComposeLifecycleOperation.Up
                    ? RetainedReviewResources(token.Project, token.Plan, false) : [] };
        }
        catch (Exception) when (onServiceSucceeded is null)
        {
            // Never log the exception: engine failures can echo environment/stdin or file contents.
            _logger.LogWarning("Reviewed Compose apply failed. Preparation or execution may be partial; refresh actual state before retrying. Technical values withheld.");
            return new()
            {
                Kind = ComposeReviewOutcomeKind.PartialFailure,
                Message = "Apply failed. Preparation or execution may have completed partially; refresh actual state and review again. Technical values withheld.",
                RetainedResources = token.Request.Operation == ComposeLifecycleOperation.Up
                    ? RetainedReviewResources(token.Project, token.Plan, false) : [],
            };
        }
        finally { _lifecycleGate.Release(); }
    }

    private static IReadOnlyList<ComposeRetainedResource> RetainedReviewResources(
        ComposeProject project, ComposeReconciliationPlan plan, bool completed)
    {
        var projection = new ComposePreviewProjection(project);
        var resources = ResourcesForPlan(project, plan);
        var state = completed ? "retained" : "unverified";
        var detail = completed ? "Available after reviewed apply; not removed." :
            "May remain after partial preparation/execution. Refresh inventory before cleanup; do not delete automatically.";
        return resources.Networks.Select(n => new ComposeRetainedResource("network", projection.Redact(n.Name), state,
                n.External ? "External resource; never deleted by this operation. " + detail : detail))
            .Concat(resources.Volumes.Select(v => new ComposeRetainedResource("volume", projection.Redact(v.Name), state,
                v.External ? "External resource; never deleted by this operation. " + detail : detail)))
            .Concat(plan.Services.SelectMany(p => p.Service.Options.Volumes).Distinct(StringComparer.Ordinal)
                .Select(mount => new ComposeRetainedResource("mount", projection.Redact(mount), "not_removed",
                    "Mounted storage is not deleted by this operation. Workloads may have changed its contents.")))
            .Concat(completed ? [] : plan.Services.Where(p => p.DesiredReplicas > 0)
                .Select(p => new ComposeRetainedResource("container", projection.Redact(p.ContainerName), "unverified", detail)))
            .ToList().AsReadOnly();
    }

    private static ComposeReviewOutcome ReviewRefusal(ComposeReviewOutcomeKind kind) => new()
    {
        Kind = kind,
        Message = kind switch
        {
            ComposeReviewOutcomeKind.Stale => "Inventory, capabilities or saved settings changed. Nothing applied; review again.",
            ComposeReviewOutcomeKind.Blocked => "Compatibility checks blocked deployment. Resolve the blockers and review again.",
            ComposeReviewOutcomeKind.Cancelled => "Review cancelled. No new apply was authorized.",
            ComposeReviewOutcomeKind.AlreadyUsed => "This review was already consumed. Create a fresh review.",
            ComposeReviewOutcomeKind.ForeignToken => "This review belongs to another supervisor. Create a fresh review.",
            ComposeReviewOutcomeKind.Expired => "This review expired after ten minutes. Nothing applied; create a fresh review.",
            _ => "Apply failed. Preparation or execution may have completed partially; refresh actual state and review again. Technical values withheld.",
        },
    };

    private async Task<ReviewEvidence> ReadReviewEvidenceAsync(ComposeProject desired,
        ComposeOperationRequest request, CancellationToken ct)
    {
        var safe = new ComposePreviewProjection(desired);
        var rows = new List<ComposeCompatibilitySetting>();
        var plan = new ComposeReconciliationPlan();
        var effective = desired;
        var evidence = new List<string>();
        void Block(string key, string reason) => rows.Add(new("Project", key, ComposeSettingDisposition.Blocked,
            "Not applied", reason, "Read-only preflight"));
        try
        {
            var stored = _store.Get(desired.Name);
            var saved = stored is null ? null : SnapshotProject(stored);
            evidence.Add(JsonSerializer.Serialize(saved));
            InheritPersistedState(desired, saved);
            effective = request.Operation == ComposeLifecycleOperation.Up ? desired : ExistingProject(desired);
            // PlanAsync and apply both call this same resolver, never a preview-specific planner.
            plan = await ReadResolvedPlanAsync(effective, request, ct).ConfigureAwait(false);
            var capabilities = await _capabilities.GetAsync(ct).ConfigureAwait(false);
            evidence.Add(JsonSerializer.Serialize(new
            {
                capabilities.ExecutablePath, capabilities.Version,
                Features = Enum.GetValues<WslcFeature>().Select(f => new { Feature = f, capabilities[f].Support }),
            }));
            if (request.Operation == ComposeLifecycleOperation.Up)
            {
                var resources = ResourcesForPlan(effective, plan);
                foreach (var network in resources.Networks)
                    await ResourceAsync("network", network.Name, network.External,
                        await _wslc.InspectNetworkAsync(network.Name, ct).ConfigureAwait(false), network.Driver,
                        $"subnet: {network.Subnet ?? "engine default"}; gateway: {network.Gateway ?? "engine default"}; " +
                        $"IP range: {network.IpRange ?? "engine default"}; driver options: {network.DriverOpts.Count}; labels: {network.Labels.Count}").ConfigureAwait(false);
                foreach (var volume in resources.Volumes)
                    await ResourceAsync("volume", volume.Name, volume.External,
                        await _wslc.InspectVolumeAsync(volume.Name, ct).ConfigureAwait(false), volume.Driver,
                        $"driver options: {volume.DriverOpts.Count}; labels: {volume.Labels.Count}").ConfigureAwait(false);
                foreach (var entry in plan.Services.Where(p => p.ContainerId is not null &&
                    (p.Action == ComposeServiceAction.Recreate || p.ImageAction == ComposeImageAction.Pull)))
                {
                    var options = entry.Service.Options.Clone();
                    await PreserveAnonymousVolumesAsync(effective, entry, options, ct).ConfigureAwait(false);
                    evidence.Add(JsonSerializer.Serialize(options.Volumes));
                }
                var inventory = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
                evidence.Add(JsonSerializer.Serialize(inventory.OrderBy(c => c.Id).Select(c =>
                    new { c.Id, c.Name, c.StateValue, c.PortsKnown, c.Ports })));
                var portBlocker = ValidatePublishedPorts(plan, inventory);
                if (portBlocker is not null) Block("ports", portBlocker);
            }
            evidence.Add(JsonSerializer.Serialize(effective));
            evidence.Add(JsonSerializer.Serialize(request));
            evidence.Add(JsonSerializer.Serialize(plan));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // OS/engine errors may echo arbitrary stdin, environment or credentials. Do not turn
            // an unknown engine error into a copyable raw diagnostic; only curated errors survive.
            Block("preflight", ex is ComposeConfigurationException
                ? safe.Redact(ex.Message)
                : "Input, inventory, resource, image or storage validation failed. Check the configuration and engine availability; technical values withheld.");
        }
        var preview = safe.Create(effective, request, plan, rows);
        return new(plan, preview, Hash(string.Join("\n", evidence)));

        Task ResourceAsync(string kind, string name, bool external, CommandResult result, string? driver, string declaration)
        {
            var missing = ComposeResourceErrors.IsNotFound(kind, result.ErrorText);
            if (!result.Success && (!missing || external))
                Block(kind, external && missing ? "A required external resource is missing." : "Resource inventory is unavailable (Unknown), not empty.");
            if (result.Success)
            {
                using var json = JsonDocument.Parse(result.StandardOutput);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().Single();
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                if (driver is not null)
                {
                    if (!TryProperty(root, "Driver", out var actual) || actual.ValueKind != JsonValueKind.String)
                        Block(kind, "Existing resource driver metadata is Unknown; the desired driver cannot be verified.");
                    else if (actual.GetString() != driver)
                        Block(kind, "Existing resource driver conflicts with the desired declaration.");
                }
                if (!external)
                {
                    if (!TryProperty(root, "Labels", out var labels) || labels.ValueKind != JsonValueKind.Object ||
                        !labels.TryGetProperty(ComposeProject.ProjectLabel, out var owner) || owner.ValueKind != JsonValueKind.String)
                        Block(kind, "Existing resource ownership is Unknown. Verify it and declare it external, or use a different name.");
                    else if (owner.GetString() != desired.Name)
                        Block(kind, "A resource with this name is owned by another project. Use an explicit external declaration or a different name.");
                }
            }
            evidence.Add($"{kind}:{name}:{result.Success}:{(result.Success ? result.StandardOutput : missing.ToString())}");
            rows.Add(new("Project", kind, !result.Success && (!missing || external)
                ? ComposeSettingDisposition.Blocked : ComposeSettingDisposition.Supported,
                $"{name}; external: {external}; driver: {driver ?? "engine default"}; {declaration}",
                (result.Success ? external ? "Existing external resource; never deleted." : "Existing resource retained; not destructively reconfigured." :
                missing && !external ? "Will be created after confirmation." : "Unavailable; cannot proceed.") +
                " Driver-option and label values are withheld. Creation-only settings are not reapplied to existing resources.",
                $"Resolved {kind} declaration"));
            return Task.CompletedTask;
        }
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static string? ValidatePublishedPorts(ComposeReconciliationPlan plan, IReadOnlyList<ContainerInfo> inventory)
    {
        var ports = new List<(string Host, int Port, string Protocol, string Instance)>();
        foreach (var entry in plan.Services.Where(p => p.Action != ComposeServiceAction.Remove && p.DesiredReplicas > 0))
        foreach (var mapping in entry.Service.Options.PortMappings)
        {
            var match = Regex.Match(mapping, @"^(?:(?<host>\[[^\]]+\]|[^:]+):)?(?<port>\d+):\d+(?:/(?<protocol>tcp|udp))?$");
            if (!match.Success)
            {
                if (mapping.Contains(':')) return "Published port syntax cannot be validated. Expand published ranges into individual numeric mappings and review again.";
                continue; // Container-only port; not a published binding.
            }
            var host = match.Groups["host"].Value.Trim('[', ']');
            var port = int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var protocol = match.Groups["protocol"].Success ? match.Groups["protocol"].Value : "tcp";
            if (port == 0) continue;
            bool Overlap(string? other) => string.IsNullOrEmpty(host) || host is "0.0.0.0" or "::" ||
                string.IsNullOrEmpty(other) || other is "0.0.0.0" or "::" || host == other.Trim('[', ']');
            if (ports.Any(p => p.Port == port && p.Protocol == protocol && Overlap(p.Host)))
                return "Selected instances have conflicting published host bindings.";
            foreach (var container in inventory.Where(c => c.State == ContainerState.Running &&
                c.Id != entry.ContainerId))
            {
                if (!container.PortsKnown) return "Published-port inventory is Unknown; bindings cannot be safely validated.";
                if (container.Ports.Any(p => p.HostPort == port && p.ProtocolName == protocol && Overlap(p.BindingAddress)))
                    return "Published host binding is already occupied by another running instance. Stop the conflicting instance and review again; planned removal does not guarantee release before startup.";
            }
            ports.Add((host, port, protocol, entry.InstanceKey));
        }
        return null;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
