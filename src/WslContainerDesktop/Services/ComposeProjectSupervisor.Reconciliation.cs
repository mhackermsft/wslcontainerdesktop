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

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed partial class ComposeProjectSupervisor
{
    private const string ApplyOperationLabel = "com.wsldesktop.apply-operation";

    private Action SuspendSupervision(ComposeProject project, ComposeService service)
    {
        var name = ResolveContainerName(project, service);
        var health = _settings.HealthChecks.Where(h => h.ContainerName == name).ToList();
        var restart = _settings.RestartPolicies.Where(r => r.ContainerName == name).ToList();
        var selected = ProjectWithServices(project, [service]);
        RemoveHealthChecks(selected);
        RemoveRestartPolicies(selected);
        return () =>
        {
            _settings.HealthChecks = _settings.HealthChecks.Where(h => h.ContainerName != name).Concat(health).ToList();
            _settings.RestartPolicies = _settings.RestartPolicies.Where(r => r.ContainerName != name).Concat(restart).ToList();
            _settings.Save();
        };
    }

    private async Task CleanupPartialRunAsync(string name, string operation)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await _wslc.InspectContainerAsync(name, cleanup.Token).ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.ErrorText.Contains("WSLC_E_CONTAINER_NOT_FOUND", StringComparison.Ordinal)) return;
            throw new InvalidOperationException($"Cannot inspect a partial Compose run: {result.ErrorText}");
        }
        var state = ContainerNetworkState.Parse(result.StandardOutput);
        if (!state.HasLabel(ApplyOperationLabel, operation)) return;
        var removed = await _wslc.RemoveContainerAsync(state.Id, force: true, cleanup.Token).ConfigureAwait(false);
        if (!removed.Success) throw new InvalidOperationException($"Cannot clean up a partial Compose run: {removed.ErrorText}");
    }

    /// <summary>Read-only desired/observed plan shared with compatibility and assistant previews.</summary>
    public async Task<ComposeReconciliationPlan> PlanAsync(ComposeProject project,
        ComposeOperationRequest? request = null, CancellationToken ct = default)
    {
        var snapshot = SnapshotProject(project);
        request = SnapshotRequest(request ?? new());
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            InheritPersistedState(snapshot, _store.Get(project.Name));
            if (request.Operation != ComposeLifecycleOperation.Up)
                snapshot = ExistingProject(snapshot);
            return await ReadResolvedPlanAsync(snapshot, request, ct).ConfigureAwait(false);
        }

        finally { _lifecycleGate.Release(); }
    }

    private async Task<ComposeReconciliationPlan> ReadResolvedPlanAsync(ComposeProject project,
        ComposeOperationRequest request, CancellationToken ct)
    {
        var plan = await ReadPlanAsync(project, request, ct).ConfigureAwait(false);
        if (!plan.CanApply) return plan;
        if (request.Operation == ComposeLifecycleOperation.Restart)
            return (await PreflightNetworksAsync(project, plan, ct).ConfigureAwait(false)).Plan;
        if (request.Operation != ComposeLifecycleOperation.Up) return plan;
        ValidateResourceDeclarations(project, plan);
        plan = (await PreflightNetworksAsync(project, plan, ct).ConfigureAwait(false)).Plan;
        if (!plan.CanApply) return plan;
        plan = await PreserveCompletedDependenciesAsync(project, plan, ct).ConfigureAwait(false);
        return await PrepareImagesAsync(project, plan, request, ct, execute: false).ConfigureAwait(false);
    }

    private async Task<ComposeReconciliationPlan> ReadPlanAsync(ComposeProject project,
        ComposeOperationRequest request, CancellationToken ct)
    {
        var selected = ComposeReconciliationPlanner.SelectServices(project, request);
        var inventory = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var inspections = new Dictionary<string, ContainerNetworkState>(StringComparer.Ordinal);
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in ComposeReconciliationPlanner.ExpandInstances(project, request, selected, inventory))
        {
            var existing = FindByName(inventory, ResolveContainerName(project, service));
            if (existing is not null)
            {
                try { inspections[existing.Id] = await _networks.InspectAsync(existing.Id, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { failures[ComposeReconciliationPlanner.InstanceKey(service)] = "Inventory is unavailable or unusable; technical values withheld."; }
            }
        }
        var plan = ComposeReconciliationPlanner.Plan(project, request, inventory, inspections);
        return new()
        {
            Services = plan.Services.Select(p => failures.TryGetValue(p.InstanceKey, out var error)
                ? p with { Action = ComposeServiceAction.Blocked, Change = ComposeServiceChange.Incompatible,
                    Reason = $"Container inspection could not establish compatibility: {error}" } : p).ToList(),
        };
    }

    private async Task<(ComposeReconciliationPlan Plan, Dictionary<string, NetworkStartupPlan> Networks)>
        PreflightNetworksAsync(ComposeProject project, ComposeReconciliationPlan plan, CancellationToken ct)
    {
        var decisions = new Dictionary<string, NetworkStartupPlan>(StringComparer.Ordinal);
        var entries = new List<ComposeServicePlan>();
        foreach (var entry in plan.Services)
        {
            if (entry.Action == ComposeServiceAction.Remove || entry.ContainerId is null && entry.Action == ComposeServiceAction.Keep)
            {
                entries.Add(entry);
                continue;
            }
            try
            {
                var decision = await PreflightNetworkAsync(project, entry.Service, ct).ConfigureAwait(false);
                decisions.Add(entry.InstanceKey, decision);
                var drift = false;
                if (entry.Action is ComposeServiceAction.Keep or ComposeServiceAction.Start)
                {
                    var observed = await _networks.InspectAsync(entry.ContainerId!, ct).ConfigureAwait(false);
                    var options = entry.Service.Options.Clone();
                    PrepareNetworkOptions(project, entry.Service, options);
                    foreach (var desired in options.GetNetworkAttachments().Take(decision.Native ? int.MaxValue : 1))
                    {
                        drift |= !observed.Networks.TryGetValue(desired.Network, out var actual) ||
                            desired.Aliases.Any(a => !actual.Aliases.Contains(a, StringComparer.Ordinal)) ||
                            (!string.IsNullOrWhiteSpace(desired.Ipv4Address) && desired.Ipv4Address != actual.Ipv4Address);
                    }
                }
                var resolved = entry with
                {
                    Backend = decision.Native ? ComposeExecutionBackend.NativeCreateConnectStart : ComposeExecutionBackend.LegacyRun,
                    NetworkSupport = decision.NetworkSupport, HealthOwner = decision.HealthOwner,
                    CompatibilityWarning = decision.Warning,
                };
                entries.Add(drift ? resolved with
                {
                    Action = ComposeServiceAction.Recreate, Change = ComposeServiceChange.Changed,
                    Reason = "Observed network endpoints differ from the supported effective configuration.",
                } : resolved);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                entries.Add(entry with
                {
                    Action = ComposeServiceAction.Blocked, Change = ComposeServiceChange.Incompatible,
                    Reason = "Network or health compatibility could not be established. Unknown capability evidence blocks deployment; invalid endpoint/health settings must be corrected.",
                    NetworkSupport = entry.Service.Options.GetNetworkAttachments().Count > 1
                        ? (await _capabilities.GetAsync(ct).ConfigureAwait(false))[WslcFeature.NetworkConnect].Support : null,
                });
            }
        }
        return (new() { Services = entries }, decisions);
    }

    /// <summary>
    /// Service-targeted restart/stop/down. Stop/down never expand to dependencies or remove shared
    /// project resources; whole-project DownAsync owns resource cleanup.
    /// </summary>
    public async Task<ComposeUpResult> OperateAsync(string projectName, ComposeOperationRequest request,
        CancellationToken ct = default)
    {
        request = SnapshotRequest(request);
        if (request.Operation == ComposeLifecycleOperation.Up)
            throw new ArgumentException("Use UpAsync to apply a desired project.", nameof(request));
        var maximumStopVersion = _suppression?.Version ?? long.MaxValue;
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var project = _store.Get(projectName) ??
                throw new InvalidOperationException($"Unknown Compose project '{projectName}'.");
            return await ExistingCoreAsync(SnapshotProject(project), request, maximumStopVersion, ct).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<ComposeUpResult> ExistingCoreAsync(ComposeProject project, ComposeOperationRequest request,
        long maximumStopVersion, CancellationToken ct, ComposeReconciliationPlan? reviewedPlan = null)
    {
        project.AppliedStateKnown = true;
        // Desired edits must not become the configuration of an existing instance on restart.
        var applied = ExistingProject(project);
        var plan = reviewedPlan ?? await ReadPlanAsync(applied, request, ct).ConfigureAwait(false);
        if (!plan.CanApply) return RejectedPlan(plan);
        if (request.Operation == ComposeLifecycleOperation.Restart && reviewedPlan is null)
        {
            foreach (var entry in plan.Services.Where(p => p.ContainerId is not null))
            {
                try { await PreflightNetworkAsync(applied, entry.Service, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return PreflightFailure(plan, entry.InstanceKey, ex.Message); }
            }
        }

        var results = new List<ComposeServiceResult>();
        var restarted = new Dictionary<string, (string? Id, DateTimeOffset StartedAt)>(StringComparer.Ordinal);
        foreach (var entry in plan.Services)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (entry.ContainerId is null)
                {
                    var absent = ProjectWithServices(applied, [entry.Service]);
                    RemoveHealthChecks(absent);
                    RemoveRestartPolicies(absent);
                    if (request.Operation == ComposeLifecycleOperation.Down)
                    {
                        project.AppliedServices.Remove(entry.InstanceKey);
                        _store.Save(project);
                    }
                    results.Add(new(entry.Service.Name, true, "No existing container; nothing to do.")
                        { Action = ComposeServiceAction.Keep, InstanceIndex = entry.InstanceIndex });
                    continue;
                }
                ComposeServiceResult result;
                if (request.Operation == ComposeLifecycleOperation.Restart)
                {
                    if (!await DependenciesReadyAsync(applied, entry, plan, restarted, ct, selectedOnly: true).ConfigureAwait(false))
                    {
                        results.Add(new(entry.Service.Name, false, "Not all selected dependency instances satisfied readiness/completion.")
                        {
                            Action = ComposeServiceAction.Blocked, ContainerId = entry.ContainerId,
                            InstanceIndex = entry.InstanceIndex,
                        });
                        continue;
                    }
                    result = await StartExistingAsync(applied, entry, maximumStopVersion, ct).ConfigureAwait(false);
                    if (result.Success)
                    {
                        restarted[entry.InstanceKey] = (result.ContainerId, DateTimeOffset.UtcNow);
                        var readyProject = ProjectWithServices(applied, [entry.Service]);
                        SeedHealthChecks(readyProject);
                        SeedRestartPolicies(readyProject);
                        if (project.AppliedServices.TryGetValue(entry.InstanceKey, out var saved))
                        {
                            saved.ManuallyStopped = _suppression?.IsSuppressed(entry.ContainerName) == true;
                            _store.Save(project);
                        }
                    }
                }
                else
                {
                    Action? restore = null;
                    var stopIssued = false;
                    try
                    {
                        await RequireCurrentAsync(applied, entry, ct).ConfigureAwait(false);
                        _suppression?.Suppress(entry.ContainerName);
                        // Persist stop intent before issuing a mutation that may commit remotely.
                        if (project.AppliedServices.TryGetValue(entry.InstanceKey, out var savedStop))
                        {
                            savedStop.ManuallyStopped = true;
                            _store.Save(project);
                        }
                        else RecordApplied(project, entry, entry.ContainerId, manuallyStopped: true);
                        restore = SuspendSupervision(applied, entry.Service);
                        stopIssued = true;
                        var stopped = await _wslc.StopContainerAsync(entry.ContainerId, entry.Service.StopGracePeriodSeconds,
                            entry.Service.Options.StopSignal, ct).ConfigureAwait(false);
                        if (!stopped.Success) throw new InvalidOperationException(Summarize(stopped));
                        if (request.Operation == ComposeLifecycleOperation.Down)
                        {
                            var removed = await _wslc.RemoveContainerAsync(entry.ContainerId, force: true, ct).ConfigureAwait(false);
                            if (!removed.Success) throw new InvalidOperationException(Summarize(removed));
                            project.AppliedServices.Remove(entry.InstanceKey);
                            _store.Save(project);
                        }
                        result = new(entry.Service.Name, true, entry.Reason) { ContainerId = entry.ContainerId };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = new(entry.Service.Name, false, ex.Message); }
                    finally { if (!stopIssued) restore?.Invoke(); }
                }
                results.Add(result with { Action = entry.Action, InstanceIndex = entry.InstanceIndex });
                _monitor.RequestRefresh();
            }
            catch (OperationCanceledException)
            {
                return CancelledResult(plan, results);
            }
            catch (Exception ex)
            {
                RecordFailedOutcome(results, entry, ex);
            }
        }
        return new() { Services = results, Plan = plan };
    }

    private async Task RequireCurrentAsync(ComposeProject project, ComposeServicePlan entry, CancellationToken ct)
    {
        var inventory = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var existing = FindByName(inventory, entry.ContainerName);
        if (existing is null || entry.ContainerId is null ||
            ContainerIdentity.ResolveId(inventory.Select(c => c.Id), entry.ContainerId) != existing.Id)
            throw new InvalidOperationException($"Container '{entry.ContainerName}' changed since preflight.");
        var inspected = await _networks.InspectAsync(entry.ContainerId!, ct).ConfigureAwait(false);
        if (ContainerIdentity.ResolveId([inspected.Id], entry.ContainerId) != inspected.Id ||
            !ComposeReconciliationPlanner.IsOwnedInstance(inspected, project, entry.Service))
            throw new InvalidOperationException($"Container '{entry.ContainerName}' is not owned by this project.");
    }

    private async Task<ComposeServiceResult> ReuseExistingAsync(ComposeProject project, ComposeServicePlan entry,
        string? warning, long maximumStopVersion, CancellationToken ct)
    {
        try
        {
            await RequireCurrentAsync(project, entry, ct).ConfigureAwait(false);
            if (_suppression is not null)
                _suppression.CompleteExplicitStart(_suppression.CaptureExplicitStart(entry.ContainerName, maximumStopVersion), true);
            return new(entry.Service.Name, true, entry.Reason, warning) { ContainerId = entry.ContainerId };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(entry.Service.Name, false, ex.Message); }
    }

    private async Task<ComposeServiceResult> StartExistingAsync(ComposeProject project, ComposeServicePlan entry,
        long maximumStopVersion, CancellationToken ct)
    {
        Action? restoreSupervision = null;
        try
        {
            await RequireCurrentAsync(project, entry, ct).ConfigureAwait(false);
            var resume = _suppression?.CaptureExplicitStart(entry.ContainerName, maximumStopVersion);
            restoreSupervision = SuspendSupervision(project, entry.Service);
            if (entry.Action == ComposeServiceAction.Restart)
            {
                var stop = await _wslc.StopContainerAsync(entry.ContainerId!, entry.Service.StopGracePeriodSeconds,
                    entry.Service.Options.StopSignal, ct).ConfigureAwait(false);
                if (!stop.Success) throw new InvalidOperationException(Summarize(stop));
            }
            var start = await _wslc.StartContainerAsync(entry.ContainerId!, ct, explicitStart: false).ConfigureAwait(false);
            if (resume is { } token) _suppression!.CompleteExplicitStart(token, start.Success);
            if (!start.Success) return new(entry.Service.Name, false, Summarize(start));
            return new(entry.Service.Name, true, entry.Reason) { ContainerId = entry.ContainerId };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(entry.Service.Name, false, ex.Message); }
        finally { restoreSupervision?.Invoke(); }
    }

    private static ComposeProject SnapshotProject(ComposeProject project) =>
        JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(project))!;

    private static void InheritPersistedState(ComposeProject desired, ComposeProject? saved)
    {
        if (saved is null) return;
        var snapshot = SnapshotProject(saved);
        desired.AppliedServices = snapshot.AppliedServices;
        if (!desired.AppliedStateKnown)
            foreach (var entry in snapshot.ReplicaOverrides.Where(entry =>
                desired.Services.Any(service => service.Name == entry.Key)))
                desired.ReplicaOverrides.TryAdd(entry.Key, entry.Value);
    }

    private static ComposeOperationRequest SnapshotRequest(ComposeOperationRequest request) => request with
    {
        Services = request.Services.ToArray(),
        Replicas = new Dictionary<string, int>(request.Replicas, StringComparer.Ordinal),
    };

    private static ComposeProject ExistingProject(ComposeProject project)
    {
        var applied = SnapshotProject(project);
        foreach (var saved in applied.AppliedServices.Values.GroupBy(s => s.Service.Name).Select(g => g.OrderBy(s => s.InstanceIndex).First()))
        {
            var index = applied.Services.FindIndex(s => s.Name == saved.Service.Name);
            var service = ComposeReconciliationPlanner.ForInstance(saved.Service, 1);
            if (index < 0) applied.Services.Add(service);
            else applied.Services[index] = service;
        }
        return applied;
    }

    private void RecordApplied(ComposeProject project, ComposeServicePlan entry, string containerId, bool manuallyStopped = false)
    {
        project.AppliedStateKnown = true;
        var resources = SnapshotProject(ResourcesForPlan(project, new() { Services = [entry] }));
        project.AppliedServices[entry.InstanceKey] = new()
        {
            InstanceIndex = entry.InstanceIndex,
            ContainerId = containerId, Fingerprint = entry.Fingerprint, ImageId = entry.ImageId,
            Service = ComposeReconciliationPlanner.CloneService(entry.Service),
            Networks = resources.Networks, Volumes = resources.Volumes,
            ManuallyStopped = manuallyStopped || _suppression?.IsSuppressed(entry.ContainerName) == true,
        };
        _store.Save(project);
    }

    private static ComposeUpResult RejectedPlan(ComposeReconciliationPlan plan) => new()
    {
        Plan = plan,
        Services = plan.Services.Select(p => new ComposeServiceResult(p.Service.Name, false,
            p.Action == ComposeServiceAction.Blocked ? p.Reason : "Not applied because project preflight is blocked.")
            { Action = ComposeServiceAction.Blocked, ContainerId = p.ContainerId, InstanceIndex = p.InstanceIndex }).ToList(),
    };

    private static ComposeUpResult PreflightFailure(ComposeReconciliationPlan plan, string instanceKey, string detail) =>
        RejectedPlan(new(plan.Services.Select(p => p.InstanceKey == instanceKey ? p with
        {
            Action = ComposeServiceAction.Blocked, Change = ComposeServiceChange.Incompatible, Reason = detail,
        } : p).ToArray()));

    private async Task<RunContainerOptions> PrepareRunAsync(ComposeProject project, ComposeServicePlan entry, CancellationToken ct)
    {
        if (entry.InstanceIndex < 1 || entry.InstanceIndex != ComposeReconciliationPlanner.InstanceIndex(entry.Service) ||
            entry.ContainerName != ComposeReconciliationPlanner.ContainerName(project, entry.Service, entry.InstanceIndex))
            throw new InvalidOperationException("The planned instance identity is inconsistent.");
        for (var attempt = 0; attempt < MaxStagedMountAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Never overwrite a stable source that a still-running container may have mounted.
            var options = CloneForRun(project, entry.Service, entry.ContainerName, Guid.NewGuid().ToString("N"));
            options.Labels[ComposeProject.InstanceLabel] = entry.InstanceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            options.Image = entry.ImageId ?? throw new InvalidOperationException("Image identity is unavailable after preflight.");
            options.Labels[ComposeProject.ConfigHashLabel] = entry.Fingerprint;
            if (entry.ImageId is not null) options.Labels[ComposeProject.ImageIdLabel] = entry.ImageId;
            if (entry.ContainerId is not null)
                await PreserveAnonymousVolumesAsync(project, entry, options, ct).ConfigureAwait(false);
            options.ToArguments();
            var sources = options.Volumes.Where(v => v.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase))
                .Select(SourceOf).ToList();
            if (!await AnyStagedMountRacedAsync(sources, ct).ConfigureAwait(false))
            {
                if (ComposeReconciliationPlanner.Fingerprint(project, entry.Service) != entry.Fingerprint)
                    throw new InvalidOperationException($"Configuration inputs for '{entry.Service.Name}' changed during preflight; apply again.");
                return options;
            }
        }
        throw new InvalidOperationException(MountLimitMessage);
    }

    private async Task PreserveAnonymousVolumesAsync(ComposeProject project, ComposeServicePlan entry,
        RunContainerOptions options, CancellationToken ct)
    {
        var inspect = await _wslc.InspectContainerAsync(entry.ContainerId!, ct).ConfigureAwait(false);
        if (!inspect.Success) throw new InvalidOperationException(inspect.ErrorText);
        var mounts = ContainerMounts.Parse(inspect.StandardOutput);
        if (!mounts.IsComplete)
            throw new InvalidOperationException($"Cannot safely recreate '{entry.Service.Name}': existing volume metadata is incomplete.");
        var explicitTargets = options.Volumes.Where(HasMountSource).Select(MountTarget).ToHashSet(StringComparer.Ordinal);
        var priorExplicitTargets = project.AppliedServices.TryGetValue(entry.InstanceKey, out var applied)
            ? applied.Service.Options.Volumes.Where(HasMountSource).Select(MountTarget).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts.Items.Where(m => m.VolumeName is not null))
        {
            var target = mount.Destination!;
            if (explicitTargets.Contains(target)) continue;
            var desiredAnonymous = options.Volumes.FirstOrDefault(v => !HasMountSource(v) && MountTarget(v) == target);
            var declaredAnonymous = desiredAnonymous is not null;
            if (!declaredAnonymous && (mount.IsAnonymous == false || priorExplicitTargets.Contains(target))) continue;
            var readOnly = desiredAnonymous is not null ? desiredAnonymous.EndsWith(":ro", StringComparison.Ordinal) : mount.ReadOnly;
            if (readOnly is null)
                throw new InvalidOperationException($"Cannot safely preserve a volume for '{entry.Service.Name}': mount mode is unknown.");
            options.Volumes.RemoveAll(v => !HasMountSource(v) && MountTarget(v) == target);
            options.Volumes.Add($"{mount.VolumeName}:{target}{(readOnly.Value ? ":ro" : "")}");
        }
    }

    private static bool HasMountSource(string mount) => mount.Contains(":/", StringComparison.Ordinal);

    private static string MountTarget(string mount)
    {
        var boundary = mount.IndexOf(":/", StringComparison.Ordinal);
        return (boundary >= 0 ? mount[(boundary + 1)..] : mount).Split(':', 2)[0];
    }

    private async Task<ComposeReconciliationPlan> PrepareImagesAsync(ComposeProject project, ComposeReconciliationPlan plan,
        ComposeOperationRequest request, CancellationToken ct, bool execute = true)
    {
        if (plan.Services.All(p => p.Action == ComposeServiceAction.Remove ||
            p.Action == ComposeServiceAction.Keep && p.ContainerId is null))
            return plan;
        var images = await _wslc.ListImagesAsync(ct).ConfigureAwait(false);
        var entries = new List<ComposeServicePlan>();
        var built = new HashSet<string>(StringComparer.Ordinal);
        var pulled = new HashSet<string>(StringComparer.Ordinal);
        var observedImages = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var group in plan.Services.Where(p => p.Service.Build is not null)
            .GroupBy(p => ResolveImage(project, p.Service), StringComparer.Ordinal))
        {
            if (group.Select(p => ComposeReconciliationPlanner.BuildFingerprint(p.Service.Build)).Distinct().Skip(1).Any())
                throw new InvalidOperationException("Selected services have conflicting build definitions for the same image tag.");
        }
        // Build providers before consumers sharing their image; service startup order is unchanged.
        foreach (var entry in plan.Services.OrderByDescending(p => p.Service.Build is not null))
        {
            if (entry.Action == ComposeServiceAction.Remove || entry.ContainerId is null && entry.Action == ComposeServiceAction.Keep)
            {
                entries.Add(entry);
                continue;
            }
            var service = entry.Service;
            var reference = ResolveImage(project, service);
            var image = FindImage(images, reference);
            var build = service.Build;
            var prior = project.AppliedServices.Values.Where(p => p.Service.Name == service.Name)
                .OrderBy(p => p.InstanceIndex).FirstOrDefault();
            var buildChanged = build is not null && prior is not null &&
                ComposeReconciliationPlanner.BuildFingerprint(prior.Service.Build) != ComposeReconciliationPlanner.BuildFingerprint(build);
            var shouldBuild = build is not null &&
                (request.Build || service.PullPolicy == ComposeImagePolicy.Build ||
                 (service.PullPolicy == ComposeImagePolicy.Missing && (buildChanged || image is null)));
            var shouldPull = !shouldBuild && (service.PullPolicy == ComposeImagePolicy.Always ||
                (image is null && service.PullPolicy == ComposeImagePolicy.Missing));
            if (shouldBuild)
            {
                var enginePath = build!.Context.StartsWith('/') && !build.Context.StartsWith("//", StringComparison.Ordinal);
                if (!build.IsValid || (!enginePath && !Directory.Exists(build.Context)))
                    throw new InvalidOperationException($"Build context for service '{service.Name}' is unavailable.");
                if (execute && built.Add(reference))
                {
                    var result = await _wslc.BuildImageAsync(build.Context, reference, build.Dockerfile, build.Args,
                        build.Target, build.Labels, build.NoCache, build.Pull, ct).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException($"Build '{service.Name}': {Summarize(result)}");
                    images = await _wslc.ListImagesAsync(ct).ConfigureAwait(false);
                    image = FindImage(images, reference);
                }
            }
            else if (shouldPull && execute && !built.Contains(reference) && pulled.Add(reference))
            {
                var result = await _wslc.PullImageAsync(reference, ct).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException($"Pull '{service.Name}': {Summarize(result)}");
                images = await _wslc.ListImagesAsync(ct).ConfigureAwait(false);
                image = FindImage(images, reference);
            }
            if ((image is null || string.IsNullOrWhiteSpace(image.Id)) && (execute || !(shouldBuild || shouldPull)))
                throw new InvalidOperationException($"No usable local image for service '{service.Name}' after image preflight.");
            string? previousImageId = null;
            if (entry.ContainerId is not null)
            {
                var observed = await _networks.InspectAsync(entry.ContainerId, ct).ConfigureAwait(false);
                observed.Labels.TryGetValue(ComposeProject.ImageIdLabel, out previousImageId);
            }
            observedImages[entry.InstanceKey] = previousImageId;
            var changedImage = previousImageId is not null && image is not null && previousImageId != image.Id;
            entries.Add(entry with
            {
                ImageId = image?.Id,
                ImageAction = shouldBuild ? ComposeImageAction.Build : shouldPull ? ComposeImageAction.Pull : ComposeImageAction.None,
                Action = entry.ContainerId is not null && (changedImage || shouldBuild) ?
                    ComposeServiceAction.Recreate : entry.Action,
                Change = entry.ContainerId is not null && (changedImage || shouldBuild) ?
                    ComposeServiceChange.Changed : entry.Change,
                Reason = changedImage ? "Resolved local image identity changed." :
                    shouldBuild ? "Requested or changed build requires replacement." :
                    shouldPull && !execute ? "Image pull required; replacement depends on the resolved image identity." : entry.Reason,
            });
        }
        entries = plan.Services.Select(p => entries.Single(e => e.InstanceKey == p.InstanceKey)).ToList();
        // A later provider/pull can update a shared tag used by an earlier entry.
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Action == ComposeServiceAction.Remove || !observedImages.ContainsKey(entry.InstanceKey)) continue;
            var finalImage = FindImage(images, ResolveImage(project, entry.Service));
            if (finalImage is null) continue;
            var changed = entry.ContainerId is not null && observedImages[entry.InstanceKey] is { } oldImage &&
                oldImage != finalImage.Id;
            entries[i] = entry with
            {
                ImageId = finalImage.Id,
                Action = changed ? ComposeServiceAction.Recreate : entry.Action,
                Change = changed ? ComposeServiceChange.Changed : entry.Change,
                Reason = changed ? "Resolved local image identity changed." : entry.Reason,
            };
        }
        // Explicit dependency restart propagation only; ordinary dependencies do not recreate peers.
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var mode = entry.Service.Options.NetworkMode ?? entry.Service.Options.Network;
            if (entry.ContainerId is not null && mode?.StartsWith("service:", StringComparison.Ordinal) == true &&
                entries.Any(p => p.Service.Name == mode["service:".Length..] &&
                    p.Action is ComposeServiceAction.Create or ComposeServiceAction.Recreate))
            {
                entries[i] = entry with
                {
                    Change = ComposeServiceChange.Changed, Action = ComposeServiceAction.Recreate,
                    Reason = "The service providing this container's network namespace is being replaced.",
                };
                continue;
            }
            if (entry.Action == ComposeServiceAction.Keep && ComposeReconciliationPlanner.Dependencies(entry.Service).Any(d => d.Restart &&
                entries.Any(p => p.Service.Name == d.ServiceName &&
                    (p.Action is ComposeServiceAction.Recreate or ComposeServiceAction.Restart ||
                     p.Action == ComposeServiceAction.Create && !entries.Any(retained =>
                        retained.Service.Name == p.Service.Name && retained.ContainerId is not null &&
                        retained.Action != ComposeServiceAction.Remove)))))
                entries[i] = entry with { Action = ComposeServiceAction.Restart, Reason = "A dependency with restart: true changed." };
        }
        return ComposeReconciliationPlanner.ValidateStartupDependencies(project, request, new() { Services = entries });
    }

    private async Task<ComposeReconciliationPlan> PreserveCompletedDependenciesAsync(ComposeProject project,
        ComposeReconciliationPlan plan, CancellationToken ct)
    {
        var completedDependencies = plan.Services.SelectMany(p => ComposeReconciliationPlanner.Dependencies(p.Service))
            .Where(d => d.Condition == DependencyCondition.ServiceCompletedSuccessfully)
            .Select(d => d.ServiceName).ToHashSet(StringComparer.Ordinal);
        if (!plan.Services.Any(p => p.Action == ComposeServiceAction.Start && completedDependencies.Contains(p.Service.Name)))
            return plan;
        var inventory = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var entries = new List<ComposeServicePlan>();
        foreach (var entry in plan.Services)
        {
            var existing = FindByName(inventory, entry.ContainerName);
            if (entry.Action == ComposeServiceAction.Start && completedDependencies.Contains(entry.Service.Name) &&
                existing?.State == ContainerState.Stopped && entry.ContainerId is not null &&
                ContainerIdentity.ResolveId(inventory.Select(c => c.Id), entry.ContainerId) == existing.Id &&
                await ExitedCleanlyAsync(entry.ContainerId!, ct).ConfigureAwait(false))
                entries.Add(entry with { Action = ComposeServiceAction.Keep, Reason = "Unchanged one-shot dependency already completed successfully." });
            else entries.Add(entry);
        }
        return new() { Services = entries };
    }

    private static ImageInfo? FindImage(IReadOnlyList<ImageInfo> images, string reference)
    {
        var normalized = reference.Contains('@') || reference.LastIndexOf(':') > reference.LastIndexOf('/')
            ? reference : reference + ":latest";
        return images.FirstOrDefault(i => i.Reference == normalized || i.Reference == reference || i.Id == reference);
    }

    private static ComposeProject ResourcesForPlan(ComposeProject project, ComposeReconciliationPlan plan)
    {
        var services = plan.Services.Where(p => p.Action != ComposeServiceAction.Remove).Select(p => p.Service).ToList();
        var networks = services.SelectMany(s => s.Options.GetNetworkAttachments()).Select(n => n.Network).ToHashSet(StringComparer.Ordinal);
        return new()
        {
            Name = project.Name, Services = services,
            Networks = project.Networks.Where(n => networks.Contains(n.Name)).ToList(),
            Volumes = project.Volumes.Where(v => services.Any(s => s.Options.Volumes.Any(m =>
                m.StartsWith(v.Name + ":", StringComparison.Ordinal)))).ToList(),
        };
    }

    private static void ValidateResourceDeclarations(ComposeProject project, ComposeReconciliationPlan plan)
    {
        foreach (var entry in plan.Services)
        {
            if (entry.Action == ComposeServiceAction.Remove ||
                !project.AppliedServices.TryGetValue(entry.InstanceKey, out var applied)) continue;
            var resources = ResourcesForPlan(project, new() { Services = [entry] });
            foreach (var network in resources.Networks)
            {
                var previous = applied.Networks.FirstOrDefault(n => n.Name == network.Name);
                if (previous is not null && ResourceSignature(previous) != ResourceSignature(network))
                    throw new InvalidOperationException($"Network '{network.Name}' has changed declarations. Existing shared networks are not reconfigured by apply.");
            }
            foreach (var volume in resources.Volumes)
            {
                var previous = applied.Volumes.FirstOrDefault(v => v.Name == volume.Name);
                if (previous is not null && ResourceSignature(previous) != ResourceSignature(volume))
                    throw new InvalidOperationException($"Volume '{volume.Name}' has changed declarations. Existing persistent volumes are not reconfigured by apply.");
            }
        }
    }

    private static string ResourceSignature(ComposeNetwork network) => JsonSerializer.Serialize(new
    {
        network.Driver, network.External, network.Subnet, network.Gateway, network.IpRange,
        Options = network.DriverOpts.Order(StringComparer.Ordinal),
        Labels = network.Labels.OrderBy(p => p.Key, StringComparer.Ordinal),
    });

    private static string ResourceSignature(ComposeVolume volume) => JsonSerializer.Serialize(new
    {
        volume.Driver, volume.External,
        Options = volume.DriverOpts.Order(StringComparer.Ordinal),
        Labels = volume.Labels.OrderBy(p => p.Key, StringComparer.Ordinal),
    });
}
