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

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Per-service outcome of a compose <c>up</c>.</summary>
public sealed record ComposeServiceResult(string Service, bool Success, string Detail, string? Warning = null)
{
    public int InstanceIndex { get; init; } = 1;
    public string InstanceKey => InstanceIndex == 1 ? Service : $"{Service}#{InstanceIndex}";
    public string? ContainerId { get; init; }
    public ComposeServiceAction Action { get; init; }
    public ComposeInstanceOutcome? Outcome { get; init; }
}

public enum ComposeInstanceOutcome { Started, Reused, Skipped, Failed, Cancelled, Stopped, Removed }

/// <summary>Aggregate result of bringing a compose project up.</summary>
public sealed class ComposeUpResult
{
    public IReadOnlyList<ComposeServiceResult> Services { get; init; } = Array.Empty<ComposeServiceResult>();
    public ComposeReconciliationPlan? Plan { get; init; }
    public bool IsCancelled { get; init; }

    public bool AllSucceeded => !IsCancelled && Services.All(s => s.Success);

    public int Started => Services.Count(s => s.Success && s.Action is
        ComposeServiceAction.Start or ComposeServiceAction.Create or ComposeServiceAction.Recreate or ComposeServiceAction.Restart);

    public IReadOnlyList<string> Warnings => Services.Where(s => s.Warning is not null)
        .Select(s => $"{s.InstanceKey}: {s.Warning}").ToList();
}

/// <summary>
/// The desktop app's compose orchestration layer ("desktop-as-daemon"). It runs each service as a
/// labelled <c>wslc</c> container in <c>depends_on</c> order, gates <c>service_healthy</c>
/// dependencies on a health probe, and enrolls services with a health check into the existing
/// <see cref="HealthWatchdog"/> so their restart policy is enforced while the app runs.
///
/// <para><b>Requires the app to be running:</b> restart and app-owned health enforcement is performed
/// in-process (there is no background daemon), so it pauses when the app is closed and resumes via
/// <see cref="ReconcileAsync"/> on the next launch.</para>
/// </summary>
public sealed partial class ComposeProjectSupervisor
{
    private readonly IWslcService _wslc;
    private readonly IComposeProjectStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger<ComposeProjectSupervisor> _logger;
    private readonly HealthWatchdog _health;
    private readonly StatusMonitor _monitor;
    private readonly RestartSuppressionState? _suppression;
    private readonly IWslcCapabilitiesService _capabilities;
    private readonly ComposeNetworkOrchestrator _networks;
    private readonly IComposeReviewPresenter? _reviewPresenter;
    private readonly TimeProvider _reviewClock;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    public IReadOnlyList<string> ReconciliationWarnings { get; private set; } = Array.Empty<string>();

    /// <summary>How long to wait for a <c>service_healthy</c> dependency before starting dependents.</summary>
    private static readonly TimeSpan HealthyWaitTimeout = TimeSpan.FromSeconds(90);

    public ComposeProjectSupervisor(
        IWslcService wslc,
        IComposeProjectStore store,
        ISettingsService settings,
        ILogger<ComposeProjectSupervisor> logger,
        IWslcCapabilitiesService capabilities,
        HealthWatchdog health,
        StatusMonitor monitor,
        RestartSuppressionState? suppression = null,
        IComposeReviewPresenter? reviewPresenter = null,
        TimeProvider? reviewClock = null)
    {
        _wslc = wslc;
        _store = store;
        _settings = settings;
        _logger = logger;
        _capabilities = capabilities;
        _health = health;
        _monitor = monitor;
        _suppression = suppression;
        _reviewPresenter = reviewPresenter;
        _reviewClock = reviewClock ?? TimeProvider.System;
        _networks = new ComposeNetworkOrchestrator(wslc, logger, suppression);
    }

    /// <summary>
    /// Applies a resolved project non-disruptively: keeps unchanged running instances, starts
    /// stopped instances and selectively recreates changes, with dependency readiness gates.
    /// </summary>
    public Task<ComposeUpResult> UpAsync(ComposeProject project, CancellationToken ct = default) =>
        UpAsync(project, new ComposeOperationRequest(), ct);

    public Task<ComposeUpResult> UpAsync(ComposeProject project, ComposeOperationRequest request,
        CancellationToken ct = default) => UpAsync(project, request, null, ct);

    /// <summary>Checkpoints successful actions before fallible local completion or later services.
    /// The observer must not re-enter lifecycle operations; errors abort after local completion.</summary>
    internal async Task<ComposeUpResult> UpAsync(ComposeProject project, ComposeOperationRequest request,
        Action<ComposeServiceResult>? onServiceSucceeded, CancellationToken ct)
    {
        if (request.Operation != ComposeLifecycleOperation.Up)
            throw new ArgumentException("Up requires an Up operation.", nameof(request));
        var review = await PrepareReviewAsync(project, request, ct).ConfigureAwait(false);
        var confirmed = _reviewPresenter is not null &&
            await _reviewPresenter.ConfirmAsync(review.Preview, ct).ConfigureAwait(false);
        return (await ApplyReviewedAsync(review, confirmed, false, onServiceSucceeded, ct).ConfigureAwait(false)).ToUpResult();
    }

    private sealed record NetworkStartupPlan(bool Native, string? Warning,
        ComposePolicyOwner HealthOwner = ComposePolicyOwner.None, WslcCapabilitySupport? NetworkSupport = null);

    private async Task<NetworkStartupPlan> PreflightNetworkAsync(
        ComposeProject project, ComposeService service, CancellationToken ct)
    {
        var desired = service.Options.Clone();
        PrepareNetworkOptions(project, service, desired);
        var endpoints = desired.GetNetworkAttachments();
        foreach (var endpoint in endpoints)
        {
            endpoint.AddEndpointArguments(new List<string>());
        }

        string? warning = null;
        var capabilities = await _capabilities.GetAsync(ct).ConfigureAwait(false);
        var native = endpoints.Count > 1 && ComposeNetworkOrchestrator.SelectNative(desired, capabilities, out warning);
        var desiredHealth = service.Options.Health ?? service.Health?.DesiredHealth;
        var owner = service.Health is null ? ComposePolicyOwner.Engine : ComposePolicyOwner.Application;
        if (desiredHealth is not null)
        {
            var health = NativeHealthPolicy.Select(desiredHealth, capabilities, forCreate: native);
            owner = desiredHealth.IsDisabled ? ComposePolicyOwner.None :
                health.Native ? ComposePolicyOwner.Engine : ComposePolicyOwner.Application;
            warning = string.Join(" ", new[] { warning, health.Diagnostic }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        return new(native, warning, owner, endpoints.Count > 1 ? capabilities[WslcFeature.NetworkConnect].Support : null);
    }

    private async Task<ComposeUpResult> UpCoreAsync(ComposeProject project, long maximumStopVersion, CancellationToken ct,
        ComposeOperationRequest request, ComposeReconciliationPlan plan, Action<ComposeServiceResult>? onServiceSucceeded)
    {
        if (!plan.CanApply)
            return RejectedPlan(plan);
        var networkPlans = plan.Services.Where(entry => entry.Action != ComposeServiceAction.Remove &&
            !(entry.ContainerId is null && entry.Action == ComposeServiceAction.Keep)).ToDictionary(
                entry => entry.InstanceKey, entry => new NetworkStartupPlan(
                    entry.Backend == ComposeExecutionBackend.NativeCreateConnectStart,
                    entry.CompatibilityWarning, entry.HealthOwner, entry.NetworkSupport), StringComparer.Ordinal);

        // Finish the complete selected graph's fallible preparation before stopping any workload.
        // Backend/selection came from the freshly revalidated review. Image acquisition is the
        // explicitly reviewed conditional work; it may resolve a pull to destructive recreation.
        plan = await PrepareImagesAsync(project, plan, request, ct).ConfigureAwait(false);
        if (!plan.CanApply) return RejectedPlan(plan);
        var prepared = new Dictionary<string, RunContainerOptions>(StringComparer.Ordinal);
        foreach (var entry in plan.Services.Where(p => p.Action is ComposeServiceAction.Create or ComposeServiceAction.Recreate))
            prepared.Add(entry.InstanceKey, await PrepareRunAsync(project, entry, ct).ConfigureAwait(false));
        await ProvisionResourcesAsync(ResourcesForPlan(project, plan), ct).ConfigureAwait(false);
        _store.Save(project);

        var results = new List<ComposeServiceResult>();
        var startedContainers = new Dictionary<string, (string? Id, DateTimeOffset StartedAt)>(StringComparer.Ordinal);

        foreach (var entry in plan.Services)
        {
            var completionErrors = new List<Exception>();
            try
            {
                ct.ThrowIfCancellationRequested();
                var service = entry.Service;
                if (entry.Action == ComposeServiceAction.Remove)
                {
                    results.Add(await RemoveExcessAsync(project, entry, ct).ConfigureAwait(false));
                    continue;
                }
                if (entry.ContainerId is null && entry.Action == ComposeServiceAction.Keep)
                {
                    var absent = ProjectWithServices(project, [entry.Service]);
                    RemoveHealthChecks(absent);
                    RemoveRestartPolicies(absent);
                    project.AppliedServices.Remove(entry.InstanceKey);
                    _store.Save(project);
                    results.Add(new(service.Name, true, "Instance is already absent.")
                        { InstanceIndex = entry.InstanceIndex, Action = entry.Action, Outcome = ComposeInstanceOutcome.Skipped });
                    continue;
                }
                // Unchanged running dependents are retained even when another instance fails.
                if (entry.Action != ComposeServiceAction.Keep &&
                    !await DependenciesReadyAsync(project, entry, plan, startedContainers, ct).ConfigureAwait(false))
                {
                    results.Add(new(service.Name, false, "Not all required dependency instances satisfied readiness/completion.")
                        { InstanceIndex = entry.InstanceIndex, Action = ComposeServiceAction.Blocked, ContainerId = entry.ContainerId });
                    continue;
                }
                var result = entry.Action switch
                {
                    ComposeServiceAction.Keep => await ReuseExistingAsync(project, entry,
                        networkPlans[entry.InstanceKey].Warning, maximumStopVersion, ct).ConfigureAwait(false),
                    ComposeServiceAction.Start or ComposeServiceAction.Restart =>
                        await StartExistingAsync(project, entry, maximumStopVersion, ct).ConfigureAwait(false),
                    _ => await StartServiceAsync(project, service, maximumStopVersion, ct,
                        networkPlans[entry.InstanceKey], prepared[entry.InstanceKey], entry.ContainerId).ConfigureAwait(false),
                };
                result = result with { Action = entry.Action, InstanceIndex = entry.InstanceIndex };
                results.Add(result);
                if (result.Success)
                {
                    Complete(() => onServiceSucceeded?.Invoke(result));
                    startedContainers[entry.InstanceKey] = (result.ContainerId,
                        entry.Action == ComposeServiceAction.Keep ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow);
                    var readyProject = ProjectWithServices(project, [service]);
                    Complete(() => SeedHealthChecks(readyProject));
                    Complete(() => SeedRestartPolicies(readyProject));
                    Complete(() => RecordApplied(project, entry, result.ContainerId!));
                    Complete(() => _monitor.RequestRefresh());
                }
            }
            catch (OperationCanceledException)
            {
                return CancelledResult(plan, results);
            }
            catch (Exception ex)
            {
                RecordFailedOutcome(results, entry, ex);
            }
            // Completion errors must abort siblings, not become recoverable per-instance failures.
            if (completionErrors.Count == 1)
                ExceptionDispatchInfo.Capture(completionErrors[0]).Throw();
            if (completionErrors.Count > 1)
                throw new AggregateException($"Could not finalize instance '{entry.InstanceKey}'.", completionErrors);

            void Complete(Action action)
            {
                try { action(); }
                catch (Exception ex) { completionErrors.Add(ex); }
            }
        }

        return new ComposeUpResult { Services = results, Plan = plan };
    }

    // Deliberate local policy: every replica must satisfy completion, not just the first exited
    // container observed by Docker Compose v2.39.4's isServiceCompleted. One early success must
    // never release a dependent while another replica is still running or has failed.
    private async Task<bool> DependenciesReadyAsync(ComposeProject project, ComposeServicePlan entry,
        ComposeReconciliationPlan plan, Dictionary<string, (string? Id, DateTimeOffset StartedAt)> started,
        CancellationToken ct, bool selectedOnly = false)
    {
        foreach (var dependency in ComposeReconciliationPlanner.Dependencies(entry.Service))
        {
            if (selectedOnly && !plan.Services.Any(p => p.Service.Name == dependency.ServiceName)) continue;
            var instances = plan.Services.Where(p => p.Service.Name == dependency.ServiceName &&
                p.Action != ComposeServiceAction.Remove &&
                !(p.Action == ComposeServiceAction.Keep && p.ContainerId is null)).ToList();
            var satisfied = instances.Count > 0;
            foreach (var instance in instances)
            {
                if (!started.TryGetValue(instance.InstanceKey, out var identity)) { satisfied = false; break; }
                satisfied = dependency.Condition switch
                {
                    DependencyCondition.ServiceHealthy => await WaitForHealthyAsync(project, instance.Service,
                        identity.Id, identity.StartedAt, ct).ConfigureAwait(false),
                    DependencyCondition.ServiceCompletedSuccessfully => await WaitForCompletedAsync(project,
                        instance.Service, identity.Id, ct).ConfigureAwait(false),
                    _ => true,
                };
                if (!satisfied) break;
            }
            if (!satisfied && dependency.Required) return false;
        }
        return true;
    }

    private static ComposeUpResult CancelledResult(ComposeReconciliationPlan plan, List<ComposeServiceResult> results) => new()
    {
        Plan = plan, IsCancelled = true,
        Services = results.Concat(plan.Services.Where(p => results.All(r => r.InstanceKey != p.InstanceKey))
            .Select(p => new ComposeServiceResult(p.Service.Name, false,
                "Cancelled or not attempted. An in-flight engine operation may have committed; refresh before retrying.")
                { InstanceIndex = p.InstanceIndex, Action = ComposeServiceAction.Blocked, ContainerId = p.ContainerId,
                    Outcome = ComposeInstanceOutcome.Cancelled })).ToArray(),
    };

    private static void RecordFailedOutcome(List<ComposeServiceResult> results, ComposeServicePlan entry, Exception error)
    {
        var completed = results.FirstOrDefault(r => r.InstanceKey == entry.InstanceKey);
        results.RemoveAll(r => r.InstanceKey == entry.InstanceKey);
        results.Add(new(entry.Service.Name, false,
            completed?.Success == true ? $"Engine action completed, but follow-up failed: {error.Message}" : error.Message)
        {
            InstanceIndex = entry.InstanceIndex, Action = entry.Action,
            ContainerId = completed?.ContainerId ?? entry.ContainerId,
        });
    }

    private async Task<ComposeServiceResult> RemoveExcessAsync(ComposeProject project, ComposeServicePlan entry, CancellationToken ct)
    {
        try
        {
            await RequireCurrentAsync(project, entry, ct).ConfigureAwait(false);
            _suppression?.Suppress(entry.ContainerName);
            if (project.AppliedServices.TryGetValue(entry.InstanceKey, out var saved))
            {
                saved.ManuallyStopped = true;
                _store.Save(project);
            }
            else RecordApplied(project, entry, entry.ContainerId!, manuallyStopped: true);
            SuspendSupervision(project, entry.Service);
            var stop = await _wslc.StopContainerAsync(entry.ContainerId!, entry.Service.StopGracePeriodSeconds,
                entry.Service.Options.StopSignal, ct).ConfigureAwait(false);
            if (!stop.Success) throw new InvalidOperationException(Summarize(stop));
            var remove = await _wslc.RemoveContainerAsync(entry.ContainerId!, force: true, ct).ConfigureAwait(false);
            if (!remove.Success) throw new InvalidOperationException(Summarize(remove));
            project.AppliedServices.Remove(entry.InstanceKey);
            _store.Save(project);
            _monitor.RequestRefresh();
            return new(entry.Service.Name, true, entry.Reason)
                { InstanceIndex = entry.InstanceIndex, Action = entry.Action, ContainerId = entry.ContainerId };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(entry.Service.Name, false, ex.Message)
                { InstanceIndex = entry.InstanceIndex, Action = entry.Action, ContainerId = entry.ContainerId };
        }
    }

    /// <summary>
    /// Profile predicate for enrolling selected service snapshots; selection itself belongs to the planner.
    /// </summary>
    private static bool IsServiceActive(ComposeProject project, ComposeService service) =>
        service.Profiles.Count == 0 ||
        project.ActiveProfiles.Contains("*", StringComparer.Ordinal) ||
        service.Profiles.Any(p => project.ActiveProfiles.Contains(p, StringComparer.Ordinal));

    /// <summary>
    /// Creates the project's declared networks and volumes before its services start. External
    /// resources must already exist. Only networks created by this project receive ownership labels.
    /// </summary>
    private async Task ProvisionResourcesAsync(ComposeProject project, CancellationToken ct)
    {
        foreach (var network in project.Networks.Where(n => !string.IsNullOrWhiteSpace(n.Name)))
        {
            var existing = await _wslc.InspectNetworkAsync(network.Name, ct).ConfigureAwait(false);
            if (existing.Success)
            {
                continue;
            }

            if (network.External ||
                !existing.ErrorText.Contains("WSLC_E_NETWORK_NOT_FOUND", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Network '{network.Name}': {existing.ErrorText}");
            }

            var labels = new Dictionary<string, string>(network.Labels, StringComparer.Ordinal)
            {
                [ComposeProject.ProjectLabel] = project.Name,
            };
            var created = await _wslc.CreateNetworkAsync(network.Name, network.Driver, network.DriverOpts, labels,
                network.Subnet, network.Gateway, network.IpRange, ct)
                .ConfigureAwait(false);
            if (!created.Success)
            {
                throw new InvalidOperationException($"Create network '{network.Name}': {created.ErrorText}");
            }
        }

        foreach (var volume in project.Volumes.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            var existing = await _wslc.InspectVolumeAsync(volume.Name, ct).ConfigureAwait(false);
            if (existing.Success) continue;
            if (volume.External || !existing.ErrorText.Contains("WSLC_E_VOLUME_NOT_FOUND", StringComparison.Ordinal))
                throw new InvalidOperationException($"Volume '{volume.Name}': {existing.ErrorText}");
            var labels = new Dictionary<string, string>(volume.Labels, StringComparer.Ordinal)
            {
                [ComposeProject.ProjectLabel] = project.Name,
            };
            var created = await _wslc.CreateVolumeAsync(volume.Name, volume.Driver, volume.DriverOpts, labels, ct).ConfigureAwait(false);
            if (!created.Success)
                throw new InvalidOperationException($"Create volume '{volume.Name}': {created.ErrorText}");
        }
    }

    /// <summary>
    /// Brings the project down: stops and removes every container the app created for it, and
    /// unregisters its health/restart policies. Leaves the stored project definition intact.
    /// When <paramref name="removeVolumes"/> is <c>true</c>, also removes the project-created
    /// volumes (like <c>docker compose down --volumes</c>); external volumes are always preserved.
    /// </summary>
    public async Task DownAsync(string projectName, bool removeVolumes = false, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DownCoreAsync(projectName, removeVolumes, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DownCoreAsync(string projectName, bool removeVolumes, CancellationToken ct)
    {
        var project = _store.Get(projectName);
        if (project is null)
        {
            return;
        }

        var result = await ExistingCoreAsync(project,
            new ComposeOperationRequest { Operation = ComposeLifecycleOperation.Down }, long.MaxValue, ct).ConfigureAwait(false);
        if (!result.AllSucceeded)
            throw new InvalidOperationException(string.Join("; ", result.Services.Where(s => !s.Success).Select(s => s.Detail)));

        // Remove project-created networks (like `docker compose down`). Volumes are preserved
        // unless the caller requested their removal (like `docker compose down --volumes`).
        foreach (var network in project.Networks.Where(n => !n.External && !string.IsNullOrWhiteSpace(n.Name)))
        {
            var inspect = await _wslc.InspectNetworkAsync(network.Name, ct).ConfigureAwait(false);
            if (!inspect.Success)
            {
                if (inspect.ErrorText.Contains("WSLC_E_NETWORK_NOT_FOUND", StringComparison.Ordinal))
                {
                    continue;
                }

                throw new InvalidOperationException($"Inspect network '{network.Name}': {inspect.ErrorText}");
            }

            if (!NetworkIsOwned(inspect.StandardOutput, project.Name))
            {
                _logger.LogWarning("Preserving network {Network}: project ownership could not be verified.", network.Name);
                continue;
            }

            var removed = await _wslc.RemoveNetworkAsync(network.Name, ct).ConfigureAwait(false);
            if (!removed.Success)
            {
                throw new InvalidOperationException($"Remove network '{network.Name}': {removed.ErrorText}");
            }
        }

        if (removeVolumes)
        {
            foreach (var volume in project.Volumes.Where(v => !v.External && !string.IsNullOrWhiteSpace(v.Name)))
            {
                var inspect = await _wslc.InspectVolumeAsync(volume.Name, ct).ConfigureAwait(false);
                if (!inspect.Success && inspect.ErrorText.Contains("WSLC_E_VOLUME_NOT_FOUND", StringComparison.Ordinal))
                    continue;
                if (!inspect.Success) throw new InvalidOperationException(inspect.ErrorText);
                if (!NetworkIsOwned(inspect.StandardOutput, project.Name))
                {
                    _logger.LogWarning("Preserving volume {Volume}: project ownership could not be verified.", volume.Name);
                    continue;
                }
                var removed = await _wslc.RemoveVolumeAsync(volume.Name, ct).ConfigureAwait(false);
                if (!removed.Success) throw new InvalidOperationException(removed.ErrorText);
            }
        }
    }

    /// <summary>
    /// Restarts existing containers without applying edited configuration, rebuilding images,
    /// recreating containers, or removing shared resources.
    /// </summary>
    public Task<ComposeUpResult> RestartAsync(string projectName, CancellationToken ct = default) =>
        OperateAsync(projectName, new ComposeOperationRequest { Operation = ComposeLifecycleOperation.Restart }, ct);

    /// <summary>
    /// Startup reconciliation: re-enrolls health/restart policies for stored projects whose
    /// containers still exist, so supervision resumes after the app is relaunched. Never throws.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        var warnings = new List<string>();
        try
        {
            var projects = _store.GetAll();
            if (projects.Count == 0)
            {
                return;
            }

            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            foreach (var project in projects)
            {
                var ready = new List<ComposeService>();
                var appliedProject = ExistingProject(project);
                var services = ComposeReconciliationPlanner.ExpandInstances(appliedProject,
                    new() { Operation = ComposeLifecycleOperation.Restart }, appliedProject.Services, containers);
                foreach (var desired in services)
                {
                    var service = project.AppliedServices.TryGetValue(ComposeReconciliationPlanner.InstanceKey(desired), out var applied)
                        ? ComposeReconciliationPlanner.ForInstance(applied.Service, applied.InstanceIndex) : desired;
                    var existing = FindByName(containers, ResolveContainerName(project, service));
                    if (existing is null)
                    {
                        var absent = ProjectWithServices(project, [service]);
                        RemoveHealthChecks(absent);
                        RemoveRestartPolicies(absent);
                        continue;
                    }

                    try
                    {
                        var state = await _networks.InspectAsync(existing.Id, ct).ConfigureAwait(false);
                        if (!ComposeReconciliationPlanner.IsOwnedInstance(state, project, service))
                        {
                            var unsafeService = ProjectWithServices(project, [service]);
                            RemoveHealthChecks(unsafeService);
                            RemoveRestartPolicies(unsafeService);
                            throw new InvalidOperationException("Container ownership does not match the saved project.");
                        }
                        if (applied is not null &&
                            (ContainerIdentity.ResolveId(containers.Select(c => c.Id), applied.ContainerId) != existing.Id ||
                             ContainerIdentity.ResolveId([state.Id], applied.ContainerId) != state.Id))
                        {
                            var replacedService = ProjectWithServices(project, [service]);
                            RemoveHealthChecks(replacedService);
                            RemoveRestartPolicies(replacedService);
                            throw new InvalidOperationException("The saved applied instance was replaced outside Compose; apply the project to reconcile it.");
                        }
                        if (applied?.ManuallyStopped == true)
                        {
                            if (_suppression?.IsSuppressed(ResolveContainerName(project, service)) == false)
                                _suppression.Suppress(ResolveContainerName(project, service));
                            var stoppedService = ProjectWithServices(project, [service]);
                            RemoveHealthChecks(stoppedService);
                            RemoveRestartPolicies(stoppedService);
                            continue;
                        }
                        if (applied is null && ComposeReconciliationPlanner.InstanceIndex(service) >
                            ComposeReconciliationPlanner.DesiredReplicas(project, service, new()))
                        {
                            var untracked = ProjectWithServices(project, [service]);
                            RemoveHealthChecks(untracked);
                            RemoveRestartPolicies(untracked);
                            throw new InvalidOperationException("An untracked owned instance exceeds the desired replica count; apply explicitly to reconcile it.");
                        }
                        if (applied is null && state.Labels.TryGetValue(ComposeProject.ConfigHashLabel, out var fingerprint) &&
                            fingerprint != ComposeReconciliationPlanner.Fingerprint(project, service))
                            throw new InvalidOperationException("Existing configuration differs from the desired project; apply it explicitly.");

                        var options = service.Options.Clone();
                        PrepareNetworkOptions(appliedProject, service, options);
                        if (options.GetNetworkAttachments().Count > 1)
                        {
                            var capabilities = await _capabilities.GetAsync(ct).ConfigureAwait(false);
                            ComposeNetworkOrchestrator.SelectNative(options, capabilities, out var warning);
                            if (warning is not null)
                            {
                                warnings.Add($"{project.Name}/{service.Name}: {warning}");
                            }

                            await _networks.ReconcileAsync(existing.Id, options, capabilities, ct).ConfigureAwait(false);
                        }

                        ready.Add(service);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"{project.Name}/{service.Name}: {ex.Message}");
                        _logger.LogWarning(ex, "Network reconciliation failed for {Project}/{Service}", project.Name, service.Name);
                    }
                }

                var readyProject = ProjectWithServices(project, ready);
                SeedHealthChecks(readyProject);
                SeedRestartPolicies(readyProject);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add(ex.Message);
            _logger.LogDebug(ex, "Compose reconcile on startup failed.");
        }
        finally
        {
            ReconciliationWarnings = warnings;
            _lifecycleGate.Release();
        }
    }

    /// <summary>How many times to stage-and-verify a config/secret bind before giving up. Each retry
    /// stages the file at a fresh unique path to bust wslc's per-path 9P negative cache. Kept small:
    /// a single fresh-path retry clears a one-off race, and each abandoned path leaks a wslc mount
    /// slot, so more retries would work against the very limit they guard.</summary>
    private const int MaxStagedMountAttempts = 2;

    private async Task<ComposeServiceResult> StartServiceAsync(ComposeProject project, ComposeService service,
        long maximumStopVersion, CancellationToken ct, NetworkStartupPlan networkPlan,
        RunContainerOptions prepared, string? expectedId)
    {
        var name = ResolveContainerName(project, service);
        bool nativeNetworks;
        string? networkWarning = null;
        Action? restoreSupervision = null;
        var oldRemoved = false;

        try
        {
            nativeNetworks = networkPlan.Native;
            networkWarning = networkPlan.Warning;
            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var existing = FindByName(containers, name);
            if (expectedId is null ? existing is not null :
                existing is null || ContainerIdentity.ResolveId(containers.Select(c => c.Id), expectedId) != existing.Id)
                throw new InvalidOperationException($"Container '{name}' changed since preflight; apply again.");
            if (existing is not null)
            {
                var state = await _networks.InspectAsync(existing.Id, ct).ConfigureAwait(false);
                if (!ComposeReconciliationPlanner.IsOwnedInstance(state, project, service) || expectedId is null ||
                    ContainerIdentity.ResolveId([state.Id], expectedId) != state.Id)
                {
                    return new ComposeServiceResult(service.Name, false,
                        $"Container '{name}' is not owned by this project and will not be replaced.");
                }

                ct.ThrowIfCancellationRequested();
                restoreSupervision = SuspendSupervision(project, service);
                var previous = project.AppliedServices.TryGetValue(ComposeReconciliationPlanner.InstanceKey(service), out var applied)
                    ? applied.Service : service;
                var stop = await _wslc.StopContainerAsync(
                    state.Id, previous.StopGracePeriodSeconds, previous.Options.StopSignal, ct)
                    .ConfigureAwait(false);
                if (!stop.Success)
                    return new(service.Name, false, Summarize(stop));
                var removed = await _wslc.RemoveContainerAsync(state.Id, force: true, ct).ConfigureAwait(false);
                if (!removed.Success)
                {
                    return new ComposeServiceResult(service.Name, false, removed.ErrorText);
                }
                oldRemoved = true;
                project.AppliedServices.Remove(ComposeReconciliationPlanner.InstanceKey(service));
                _store.Save(project);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                RemoveHealthChecks(ProjectWithServices(project, [service]));
                RemoveRestartPolicies(ProjectWithServices(project, [service]));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ComposeServiceResult(service.Name, false, ex.Message);
        }
        finally
        {
            if (!oldRemoved) restoreSupervision?.Invoke();
        }

        var operation = Guid.NewGuid().ToString("N");
        prepared.Labels[ApplyOperationLabel] = operation;
        var launched = false;
        try
        {
            string containerId;
            if (nativeNetworks)
            {
                containerId = await _networks.CreateAndStartAsync(prepared, ct, maximumStopVersion).ConfigureAwait(false);
            }
            else
            {
                var run = await _wslc.RunContainerAsync(prepared, ct, maximumStopVersion).ConfigureAwait(false);
                if (!run.Success)
                    return new(service.Name, false, Summarize(run), networkWarning);
                // Complete observation of an acknowledged run even if cancellation arrives now.
                using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var state = await _networks.InspectAsync(name, observation.Token).ConfigureAwait(false);
                if (!ComposeReconciliationPlanner.IsOwnedInstance(state, project, service) || !state.HasLabel(ApplyOperationLabel, operation))
                    throw new InvalidOperationException("Started container ownership could not be verified.");
                containerId = state.Id;
            }
            launched = true;
            await ApplyExtraHostsAsync(name, service, ct).ConfigureAwait(false);
            return new(service.Name, true, $"Started as {name}", networkWarning) { ContainerId = containerId };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(service.Name, false, ex.Message); }
        finally
        {
            if (!launched && !nativeNetworks)
                await CleanupPartialRunAsync(name, operation).ConfigureAwait(false);
        }
    }

    /// <summary>The host source of a "<c>source:/target:ro</c>" bind string.</summary>
    private static string SourceOf(string volume)
    {
        var boundary = volume.IndexOf(":/", StringComparison.Ordinal);
        return boundary > 0 ? volume[..boundary] : volume;
    }

    /// <summary>True when any staged source is confirmed (from inside the wslc VM) to mount as a
    /// directory — a raced/poisoned bind that must be re-staged fresh. A probe that can't run
    /// (<see cref="BindMountProbeResult.ProbeUnavailable"/>) is treated as "not raced" so the real
    /// run proceeds and its own error handling reports any genuine failure, rather than masking an
    /// image/engine problem as a mount-limit failure.</summary>
    private async Task<bool> AnyStagedMountRacedAsync(IEnumerable<string> sources, CancellationToken ct)
    {
        foreach (var source in sources)
        {
            if (await _wslc.VerifyBindMountAsync(source, ct).ConfigureAwait(false)
                == BindMountProbeResult.MountsAsDirectory)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Appends compose <c>extra_hosts:</c> entries to the container's <c>/etc/hosts</c> via
    /// <c>exec</c>, since <c>wslc run</c> has no <c>--add-host</c>. The special <c>host-gateway</c>
    /// address resolves to the container's default gateway at runtime. Best-effort: failures are logged.
    /// </summary>
    private async Task ApplyExtraHostsAsync(string name, ComposeService service, CancellationToken ct)
    {
        if (service.ExtraHosts.Count == 0)
        {
            return;
        }

        try
        {
            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var container = FindByName(containers, name);
            if (container is null)
            {
                return;
            }

            var lines = new List<string>();
            foreach (var entry in service.ExtraHosts)
            {
                var sep = entry.IndexOf(':');
                if (sep <= 0 || sep >= entry.Length - 1)
                {
                    continue;
                }

                var host = entry[..sep].Trim();
                var ip = entry[(sep + 1)..].Trim();
                var addr = string.Equals(ip, "host-gateway", StringComparison.OrdinalIgnoreCase)
                    ? "$GW"
                    : ip;
                lines.Add($"echo \"{addr} {host}\" >> /etc/hosts");
            }

            if (lines.Count == 0)
            {
                return;
            }

            var script = "GW=$(ip route 2>/dev/null | awk '/^default/{print $3; exit}'); " +
                string.Join("; ", lines);
            var result = await _wslc.ExecAsync(container.Id, script, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogDebug("Applying extra_hosts to {Name} failed: {Detail}", name, Summarize(result));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Applying extra_hosts to {Name} threw.", name);
        }
    }

    /// <summary>Consumes the same health evidence as the badges; never launches duplicate probes.</summary>
    private async Task<bool> WaitForHealthyAsync(ComposeProject project, ComposeService dep,
        string? expectedId, DateTimeOffset notBefore, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedId))
        {
            _logger.LogWarning("Cannot establish the new container identity for dependency {Name}.", dep.Name);
            return false;
        }
        if (dep.Health is null && dep.Options.Health is null)
        {
            _logger.LogWarning("Dependency {Name} has no configured health check.", dep.Name);
            return false;
        }
        var name = ResolveContainerName(project, dep);
        var deadline = DateTimeOffset.UtcNow + HealthyWaitTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var containers = _monitor.Latest?.Containers ?? Array.Empty<ContainerInfo>();
            var container = FindByName(containers, name);
            if (container is not null && container.State == ContainerState.Running)
            {
                var health = _health.Latest.Containers.FirstOrDefault(h => h.ContainerName == name);
                if (health is not null && NativeHealthPolicy.IsDependencyReady(container, health, expectedId, notBefore))
                    return true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
        }

        return false;
    }

    /// <summary>
    /// Waits until the dependency container has exited with code 0 (compose
    /// <c>service_completed_successfully</c>). Returns false on timeout or a non-zero/unreadable exit.
    /// </summary>
    private async Task<bool> WaitForCompletedAsync(ComposeProject project, ComposeService dep, string? expectedId, CancellationToken ct)
    {
        var name = ResolveContainerName(project, dep);
        var deadline = DateTimeOffset.UtcNow + HealthyWaitTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var container = FindByName(containers, name);
            if (container is null || expectedId is null ||
                ContainerIdentity.ResolveId(containers.Select(c => c.Id), expectedId) != container.Id)
                return false;

            // Only inspect the exit code once the container has actually stopped.
            if (container is not null &&
                container.State is ContainerState.Stopped)
            {
                return await ExitedCleanlyAsync(container.Id, ct).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
        }

        return false;
    }

    /// <summary>
    /// Best-effort exit-code check via <c>wslc inspect</c>: true only when the container's last exit
    /// code can be read and is zero. Unknown/unreadable exits are treated as failures.
    /// </summary>
    private async Task<bool> ExitedCleanlyAsync(string id, CancellationToken ct)
    {
        try
        {
            var inspect = await _wslc.InspectContainerAsync(id, ct).ConfigureAwait(false);
            if (!inspect.Success || string.IsNullOrWhiteSpace(inspect.StandardOutput))
            {
                return false;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                inspect.StandardOutput, "\"ExitCode\"\\s*:\\s*(-?\\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var code) && code == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Compose dependency exit status.");
            return false;
        }
    }

    private RunContainerOptions CloneForRun(ComposeProject project, ComposeService service, string name, string? stagingNonce = null)
    {
        var src = service.Options;
        var options = new RunContainerOptions
        {
            Image = ResolveImage(project, service),
            Name = name,
            Detached = true,
            RemoveOnExit = false,
            Interactive = false,
            AllGpus = src.AllGpus,
            Command = src.Command,
            Health = src.Health?.Clone() ?? service.Health?.DesiredHealth?.Clone(),
            Entrypoint = src.Entrypoint,
            User = src.User,
            WorkingDir = src.WorkingDir,
            Hostname = src.Hostname,
            CpuLimit = src.CpuLimit,
            MemoryLimit = src.MemoryLimit,
            Network = src.Network,
            Networks = new List<string>(src.Networks),
            NetworkAttachments = src.NetworkAttachments.Select(n => n.Clone()).ToList(),
            NetworkMode = src.NetworkMode,
            PortMappings = new List<string>(src.PortMappings),
            EnvironmentVariables = new List<string>(src.EnvironmentVariables),
            Volumes = new List<string>(src.Volumes),
            Labels = new Dictionary<string, string>(src.Labels, StringComparer.Ordinal),
            Dns = new List<string>(src.Dns),
            DnsSearch = new List<string>(src.DnsSearch),
            DnsOptions = new List<string>(src.DnsOptions),
            Tmpfs = new List<string>(src.Tmpfs),
            Ulimits = new List<string>(src.Ulimits),
            ShmSize = src.ShmSize,
            StopSignal = src.StopSignal,
            Domainname = src.Domainname,
            Aliases = new List<string>(src.Aliases),
        };

        PrepareNetworkOptions(project, service, options, includeFirstContainerAlias: true);

        // Bind-mount file-backed secrets/configs read-only (wslc has no secret store).
        foreach (var mount in service.Secrets)
        {
            AddFileMount(options, project, project.Secrets, mount, "secret", stagingNonce);
        }

        foreach (var mount in service.Configs)
        {
            AddFileMount(options, project, project.Configs, mount, "config", stagingNonce);
        }

        // Tag the container so the project can be re-adopted and torn down as a unit.
        options.Labels[ComposeProject.ProjectLabel] = project.Name;
        options.Labels[ComposeProject.ServiceLabel] = service.Name;
        // The sole issuer is PrepareRunAsync, after validating the plan's explicit ordinal.
        options.Labels.Remove(ComposeProject.InstanceLabel);
        return options;
    }

    private static void PrepareNetworkOptions(ComposeProject project, ComposeService service, RunContainerOptions options,
        bool includeFirstContainerAlias = false)
    {
        var mode = options.NetworkMode ?? options.Network;
        if (mode?.StartsWith("service:", StringComparison.Ordinal) == true)
        {
            var referenced = project.Services.FirstOrDefault(s => s.Name == mode["service:".Length..]) ??
                throw new InvalidOperationException($"Unknown network_mode service '{mode}'.");
            options.NetworkMode = $"container:{ResolveContainerName(project, referenced)}";
            options.Network = options.NetworkMode;
        }

        options.NetworkAttachments = options.GetNetworkAttachments();
        foreach (var endpoint in options.NetworkAttachments)
        {
            if (!endpoint.Aliases.Contains(service.Name, StringComparer.Ordinal))
            {
                endpoint.Aliases.Add(service.Name);
            }
            // New containers get an explicit unique alias on every endpoint. Existing legacy
            // instance one is not recreated/reconnected merely to retrofit this implicit alias.
            if (includeFirstContainerAlias || ComposeReconciliationPlanner.InstanceIndex(service) > 1)
            {
                var containerName = ResolveContainerName(project, service);
                if (!endpoint.Aliases.Contains(containerName, StringComparer.Ordinal))
                    endpoint.Aliases.Add(containerName);
            }
        }
    }

    private static ComposeProject ProjectWithServices(ComposeProject project, IEnumerable<ComposeService> services) =>
        new() { Name = project.Name, ActiveProfiles = ["*"], Services = services.ToList() };

    private static bool NetworkIsOwned(string json, string project)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() == 1)
        {
            root = root[0];
        }

        return root.ValueKind == System.Text.Json.JsonValueKind.Object &&
            root.TryGetProperty("Labels", out var labels) && labels.ValueKind == System.Text.Json.JsonValueKind.Object &&
            labels.TryGetProperty(ComposeProject.ProjectLabel, out var owner) &&
            owner.ValueKind == System.Text.Json.JsonValueKind.String && owner.GetString() == project;
    }

    /// <summary>
    /// The image a service runs. When the service has a <c>build:</c> section the supervisor built and
    /// tagged an image as <c>project_service</c>; otherwise the declared <c>image:</c> is used.
    /// </summary>
    private static string ResolveImage(ComposeProject project, ComposeService service) =>
        service.Build is not null && service.Build.IsValid
            ? BuiltImageTag(project, service)
            : service.Options.Image;

    /// <summary>The deterministic tag the supervisor assigns to a service built from source.</summary>
    private static string BuiltImageTag(ComposeProject project, ComposeService service) =>
        string.IsNullOrWhiteSpace(service.Options.Image)
            ? $"{project.Name}_{service.Name}:latest"
            : service.Options.Image.Trim();

    /// <summary>
    /// Root under which config/secret source files are materialized before binding. Two problems are
    /// avoided by staging (rather than binding source files in place):
    /// <list type="bullet">
    /// <item>Some Windows directories do not enumerate reliably inside the <c>wslc</c> VM's 9P share,
    /// so a file bind's parent isn't found and runc falls back to <c>mkdir</c> on the read-only share
    /// ("read-only file system").</item>
    /// <item>MSIX <b>AppData redirection</b>: for a packaged app, writes to
    /// <c>%LOCALAPPDATA%</c> are transparently redirected into the package's
    /// <c>...\Packages\&lt;PFN&gt;\LocalCache\Local</c> store, but the literal (unredirected) path is
    /// what would be handed to <c>wslc</c>. <c>wslc</c> runs without the package's redirection view,
    /// looks at the literal path, finds nothing, and runc pre-creates the bind source as a directory
    /// — so the container reads a directory where its config/secret file should be. Staging under the
    /// package's real <c>LocalCache</c> folder (which is <i>not</i> further redirected) makes the path
    /// the app writes identical to the path <c>wslc</c> reads.</item>
    /// </list>
    /// </summary>
    private static string StagingRoot => Path.Combine(StagingBase.Value, "compose-stage");

    private static readonly Lazy<string> StagingBase = new(() =>
    {
        try
        {
            // Packaged: the package's real LocalCache folder. Not redirected, so app-write path ==
            // wslc-read path. Throws when running unpackaged.
            return Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch
        {
            // Unpackaged: %LOCALAPPDATA% is not redirected and binds reliably.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WslContainerDesktop");
        }
    });

    /// <summary>Resolves a secret/config reference to its source file and adds a read-only bind mount.</summary>
    private void AddFileMount(
        RunContainerOptions options,
        ComposeProject project,
        IReadOnlyList<ComposeSecret> definitions,
        ComposeFileMount reference,
        string kind,
        string? stagingNonce = null)
    {
        var def = definitions.FirstOrDefault(d =>
            string.Equals(d.Name, reference.Source, StringComparison.Ordinal));
        if (def is null || string.IsNullOrWhiteSpace(def.File) || string.IsNullOrWhiteSpace(reference.Target))
        {
            throw new InvalidOperationException($"Compose {kind} reference cannot be materialized.");
        }

        if (!File.Exists(def.File))
        {
            throw new InvalidOperationException($"Compose {kind} source is missing.");
        }

        var mountSource = MaterializeForMount(project, kind, def.Name, def.File, stagingNonce);
        options.Volumes.Add($"{mountSource}:{reference.Target}:ro");
    }

    /// <summary>
    /// Copies a config/secret source file into a per-project staging directory and returns the staged
    /// path to bind. See <see cref="StagingRoot"/> for why in-place binds are avoided. When
    /// <paramref name="stagingNonce"/> is set (a retry) or the stable staged path has been poisoned
    /// (left as a directory by a prior raced run), a fresh unique subdirectory is used so wslc's
    /// per-path 9P negative cache treats the source as new.
    /// </summary>
    private string MaterializeForMount(
        ComposeProject project, string kind, string name, string source, string? stagingNonce)
    {
        try
        {
            var baseDir = Path.Combine(StagingRoot, Sanitize(project.Name), kind, Sanitize(name));
            var stablePath = Path.Combine(baseDir, Path.GetFileName(source));

            string dir;
            if (!string.IsNullOrEmpty(stagingNonce))
            {
                dir = Path.Combine(baseDir, stagingNonce);
            }
            else if (Directory.Exists(stablePath))
            {
                // Stable path was raced into a directory in a prior run; reusing it keeps failing.
                dir = Path.Combine(baseDir, Guid.NewGuid().ToString("N")[..8]);
            }
            else
            {
                dir = baseDir;
            }

            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(source));

            // Do not delete an unexpected directory while preparing a file mount.
            if (Directory.Exists(dest))
                throw new IOException("The staged mount destination is a directory.");

            File.Copy(source, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stage Compose file mount.");
            throw new InvalidOperationException($"Compose {kind} could not be staged; existing containers were preserved.");
        }
    }

    /// <summary>Best-effort removal of a project's staged config/secret files. Call when a project
    /// is deleted so its materialized configs/secrets don't linger under the staging root.</summary>
    public void CleanStaging(string projectName)
    {
        try
        {
            var dir = Path.Combine(StagingRoot, Sanitize(projectName));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean compose staging for {Project}.", projectName);
        }
    }

    /// <summary>Replaces characters that are invalid in a path segment with underscores.</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>The container name a service runs as: an explicit <c>container_name</c>, else <c>project_service</c>.</summary>
    private static string ResolveContainerName(ComposeProject project, ComposeService service) =>
        ComposeReconciliationPlanner.ContainerName(project, service);

    private static ContainerInfo? FindByName(IReadOnlyList<ContainerInfo> containers, string name) =>
        containers.FirstOrDefault(c =>
            string.Equals(c.Name.TrimStart('/'), name, StringComparison.Ordinal));

    /// <summary>Enrolls each service's health check (bound to its resolved container name) into the watchdog.</summary>
    private void SeedHealthChecks(ComposeProject project)
    {
        var toAdd = new List<HealthCheckConfig>();
        foreach (var service in project.Services)
        {
            if (!IsServiceActive(project, service))
            {
                continue;
            }

            if (service.Health is null)
            {
                continue;
            }

            toAdd.Add(new HealthCheckConfig
            {
                ContainerName = ResolveContainerName(project, service),
                Kind = service.Health.Kind,
                Command = service.Health.Command,
                TcpPort = service.Health.TcpPort,
                IntervalSeconds = service.Health.IntervalSeconds,
                MaxRestarts = service.Health.MaxRestarts,
                Enabled = true,
                DesiredHealth = service.Health.DesiredHealth?.Clone(),
            });
        }

        var names = project.Services.Select(s => ResolveContainerName(project, s)).ToHashSet(StringComparer.Ordinal);

        // Replace the list reference atomically so the watchdog never enumerates a mutating list.
        var merged = _settings.HealthChecks
            .Where(c => !names.Contains(c.ContainerName))
            .Concat(toAdd)
            .ToList();

        _settings.HealthChecks = merged;
        _settings.Save();
    }

    /// <summary>Removes the health checks the app seeded for a project's containers.</summary>
    private void RemoveHealthChecks(ComposeProject project)
    {
        var names = new HashSet<string>(
            project.Services.Select(s => ResolveContainerName(project, s)),
            StringComparer.Ordinal);

        var remaining = _settings.HealthChecks
            .Where(c => !names.Contains(c.ContainerName))
            .ToList();

        if (remaining.Count == _settings.HealthChecks.Count)
        {
            return;
        }

        _settings.HealthChecks = remaining;
        _settings.Save();
    }

    /// <summary>
    /// Enrolls restart policies for services that declare one but have <em>no</em> health check
    /// (health-checked services are supervised by the watchdog). Bound to resolved container names.
    /// </summary>
    private void SeedRestartPolicies(ComposeProject project)
    {
        var toAdd = new List<RestartPolicyConfig>();
        foreach (var service in project.Services)
        {
            if (!IsServiceActive(project, service))
            {
                continue;
            }

            var hasHealth = service.Health is { MaxRestarts: > 0 } &&
                service.Health.DesiredHealth?.IsDisabled != true;
            if (service.Restart == RestartPolicyKind.No || hasHealth)
            {
                continue;
            }

            var budget = service.Restart is RestartPolicyKind.Always or RestartPolicyKind.UnlessStopped
                ? RestartPolicyConfig.MaxRestartLimit
                : 3;

            toAdd.Add(new RestartPolicyConfig
            {
                ContainerName = ResolveContainerName(project, service),
                Policy = service.Restart,
                MaxRestarts = budget,
                Enabled = true,
            });
        }

        var names = new HashSet<string>(
            project.Services.Select(s => ResolveContainerName(project, s)),
            StringComparer.Ordinal);

        // Replace the list reference atomically so the watchdog never enumerates a mutating list.
        var merged = _settings.RestartPolicies
            .Where(p => !names.Contains(p.ContainerName))
            .Concat(toAdd)
            .ToList();

        if (merged.Count == _settings.RestartPolicies.Count && toAdd.Count == 0)
        {
            return;
        }

        _settings.RestartPolicies = merged;
        _settings.Save();
    }

    /// <summary>Removes the restart policies the app seeded for a project's containers.</summary>
    private void RemoveRestartPolicies(ComposeProject project)
    {
        var names = new HashSet<string>(
            project.Services.Select(s => ResolveContainerName(project, s)),
            StringComparer.Ordinal);

        var remaining = _settings.RestartPolicies
            .Where(p => !names.Contains(p.ContainerName))
            .ToList();

        if (remaining.Count == _settings.RestartPolicies.Count)
        {
            return;
        }

        _settings.RestartPolicies = remaining;
        _settings.Save();
    }

    private static string Summarize(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        text = (text ?? string.Empty).Trim();

        if (IsMountLimitFailure(text))
        {
            return MountLimitMessage;
        }

        return string.IsNullOrEmpty(text) ? $"wslc exited with code {result.ExitCode}" : text;
    }

    /// <summary>User-facing message for both the explicit wslc mount-limit error and the silent
    /// directory-race symptom. The Compose page detects this to offer a session restart.</summary>
    public const string MountLimitMessage =
        "wslc hit its session bind-mount limit (it leaks a slot per distinct host path, cap 15), so "
        + "this config/secret could not be mounted. Use \"Restart WSL session\" to release the slots, "
        + "then bring the project up again. (Restarting stops all running containers.)";

    /// <summary>
    /// True when a <c>wslc run</c> failure is the session bind-mount limit. wslc leaks a slot per
    /// distinct host bind source (cap 15, never freed until the session is terminated); once
    /// exhausted, new binds either report the explicit limit error or degrade to a runc
    /// <c>mkdir ... read-only file system</c> on the mount source.
    /// </summary>
    public static bool IsMountLimitFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (string.Equals(text, MountLimitMessage, StringComparison.Ordinal))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        return lower.Contains("too many volumes")
            || (lower.Contains("limit: 15") && lower.Contains("volume"))
            || (lower.Contains("creating mount source path") && lower.Contains("read-only file system"));
    }
}
