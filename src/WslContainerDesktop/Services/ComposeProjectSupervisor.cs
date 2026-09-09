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

/// <summary>Per-service outcome of a compose <c>up</c>.</summary>
public sealed record ComposeServiceResult(string Service, bool Success, string Detail, string? Warning = null)
{
    public string? ContainerId { get; init; }
}

/// <summary>Aggregate result of bringing a compose project up.</summary>
public sealed class ComposeUpResult
{
    public IReadOnlyList<ComposeServiceResult> Services { get; init; } = Array.Empty<ComposeServiceResult>();

    public bool AllSucceeded => Services.All(s => s.Success);

    public int Started => Services.Count(s => s.Success);

    public IReadOnlyList<string> Warnings => Services.Where(s => s.Warning is not null)
        .Select(s => $"{s.Service}: {s.Warning}").ToList();
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
public sealed class ComposeProjectSupervisor
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
        RestartSuppressionState? suppression = null)
    {
        _wslc = wslc;
        _store = store;
        _settings = settings;
        _logger = logger;
        _capabilities = capabilities;
        _health = health;
        _monitor = monitor;
        _suppression = suppression;
        _networks = new ComposeNetworkOrchestrator(wslc, logger, suppression);
    }

    /// <summary>
    /// Brings the project up: (re)creates each service container in dependency order, gating any
    /// <c>service_healthy</c> edges on a health probe, and enrolls health/restart policies. The
    /// project is persisted so it can be managed and re-adopted later.
    /// </summary>
    public async Task<ComposeUpResult> UpAsync(ComposeProject project, CancellationToken ct = default)
    {
        var maximumStopVersion = _suppression?.Version ?? long.MaxValue;
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await UpCoreAsync(project, maximumStopVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private sealed record NetworkStartupPlan(bool Native, string? Warning);

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
        var native = endpoints.Count > 1 && ComposeNetworkOrchestrator.SelectNative(desired,
            await _capabilities.GetAsync(ct).ConfigureAwait(false), out warning);
        var desiredHealth = service.Options.Health ?? service.Health?.DesiredHealth;
        if (desiredHealth is not null)
        {
            NativeHealthPolicy.Select(desiredHealth,
                await _capabilities.GetAsync(ct).ConfigureAwait(false), forCreate: native);
        }
        return new(native, warning);
    }

    private async Task<ComposeUpResult> UpCoreAsync(ComposeProject project, long maximumStopVersion, CancellationToken ct,
        IReadOnlyDictionary<string, NetworkStartupPlan>? networkPlans = null)
    {
        _store.Save(project);

        // Provision declared networks/volumes and build any images before starting services.
        await ProvisionResourcesAsync(project, ct).ConfigureAwait(false);
        await BuildImagesAsync(project, ct).ConfigureAwait(false);

        var order = ResolveOrder(project);
        var results = new List<ComposeServiceResult>();
        var started = new HashSet<string>(StringComparer.Ordinal);
        var startedContainers = new Dictionary<string, (string? Id, DateTimeOffset StartedAt)>(StringComparer.Ordinal);

        foreach (var service in order)
        {
            ct.ThrowIfCancellationRequested();

            // Skip services excluded by the active profile set (docker compose profile semantics).
            if (!IsServiceActive(project, service))
            {
                continue;
            }

            var unavailable = service.DependsOn.Where(d => !started.Contains(d.ServiceName)).ToList();
            if (unavailable.Count > 0)
            {
                results.Add(new ComposeServiceResult(service.Name, false,
                    $"Required dependencies are not ready: {string.Join(", ", unavailable.Select(d => d.ServiceName))}."));
                continue;
            }

            // Honor service_healthy dependencies before creating this service.
            var dependencyFailed = false;
            foreach (var dep in service.DependsOn.Where(d => d.Condition == DependencyCondition.ServiceHealthy))
            {
                var depService = project.Services.FirstOrDefault(s =>
                    string.Equals(s.Name, dep.ServiceName, StringComparison.Ordinal));
                if (depService is null || !started.Contains(dep.ServiceName))
                {
                    dependencyFailed = true;
                    break;
                }

                var identity = startedContainers[dep.ServiceName];
                var healthy = await WaitForHealthyAsync(project, depService, identity.Id, identity.StartedAt, ct).ConfigureAwait(false);
                if (!healthy)
                {
                    dependencyFailed = true;
                    break;
                }
            }
            if (dependencyFailed)
            {
                results.Add(new ComposeServiceResult(service.Name, false,
                    "A required service_healthy dependency is missing, failed, or did not become healthy. Service was not started."));
                continue;
            }

            // Honor service_completed_successfully dependencies (one-shot init/migration services).
            foreach (var dep in service.DependsOn.Where(d => d.Condition == DependencyCondition.ServiceCompletedSuccessfully))
            {
                var depService = project.Services.FirstOrDefault(s =>
                    string.Equals(s.Name, dep.ServiceName, StringComparison.Ordinal));
                if (depService is null || !started.Contains(dep.ServiceName))
                {
                    continue;
                }

                var completed = await WaitForCompletedAsync(project, depService, ct).ConfigureAwait(false);
                if (!completed)
                {
                    _logger.LogWarning(
                        "Dependency {Dep} did not complete successfully within {Timeout}s; starting {Service} anyway.",
                        dep.ServiceName, (int)HealthyWaitTimeout.TotalSeconds, service.Name);
                }
            }

            var result = await StartServiceAsync(project, service, maximumStopVersion, ct,
                networkPlans is null ? null : networkPlans[service.Name]).ConfigureAwait(false);
            results.Add(result);
            if (result.Success)
            {
                started.Add(service.Name);
                startedContainers[service.Name] = (result.ContainerId, DateTimeOffset.UtcNow);
                var readyProject = ProjectWithServices(project, [service]);
                SeedHealthChecks(readyProject);
                SeedRestartPolicies(readyProject);
                _monitor.RequestRefresh();
            }
        }

        return new ComposeUpResult { Services = results };
    }

    /// <summary>
    /// A service starts when it declares no profiles, or when one of its profiles is in the project's
    /// <see cref="ComposeProject.ActiveProfiles"/> — matching <c>docker compose</c>'s profile rules.
    /// </summary>
    private static bool IsServiceActive(ComposeProject project, ComposeService service) =>
        service.Profiles.Count == 0 ||
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

        foreach (var volume in project.Volumes.Where(v => !v.External && !string.IsNullOrWhiteSpace(v.Name)))
        {
            try
            {
                await _wslc.CreateVolumeAsync(volume.Name, volume.Driver, volume.DriverOpts, volume.Labels, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Creating volume {Volume} failed (may already exist).", volume.Name);
            }
        }
    }

    /// <summary>
    /// Builds an image for every service that declares a <c>build:</c> section, tagging it so the
    /// service runs the freshly built image. A failed build is logged and left to surface when the
    /// service fails to run.
    /// </summary>
    private async Task BuildImagesAsync(ComposeProject project, CancellationToken ct)
    {
        foreach (var service in project.Services.Where(s => s.Build is { } b && b.IsValid))
        {
            var build = service.Build!;
            if (!Directory.Exists(build.Context))
            {
                _logger.LogWarning(
                    "Build context {Context} for service {Service} does not exist; skipping build.",
                    build.Context, service.Name);
                continue;
            }

            var tag = BuiltImageTag(project, service);
            try
            {
                var result = await _wslc.BuildImageAsync(
                    build.Context, tag, build.Dockerfile, build.Args, build.Target, build.Labels,
                    build.NoCache, build.Pull, ct)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogWarning("Build for service {Service} failed: {Detail}", service.Name, Summarize(result));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Build for service {Service} threw.", service.Name);
            }
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

        var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        foreach (var service in project.Services)
        {
            var name = ResolveContainerName(project, service);
            var existing = FindByName(containers, name);
            if (existing is null)
            {
                RemoveHealthChecks(ProjectWithServices(project, [service]));
                RemoveRestartPolicies(ProjectWithServices(project, [service]));
                continue;
            }

            var state = await _networks.InspectAsync(existing.Id, ct).ConfigureAwait(false);
            if (!state.IsOwnedBy(project, service))
            {
                throw new InvalidOperationException($"Container '{name}' is not owned by this project; it was not removed.");
            }

            ct.ThrowIfCancellationRequested();
            // Deregister only the verified service we are about to stop, not untouched siblings.
            RemoveHealthChecks(ProjectWithServices(project, [service]));
            RemoveRestartPolicies(ProjectWithServices(project, [service]));
            await _wslc.StopContainerAsync(
                existing.Id, service.StopGracePeriodSeconds, service.Options.StopSignal, ct)
                .ConfigureAwait(false);
            var removed = await _wslc.RemoveContainerAsync(existing.Id, force: true, ct).ConfigureAwait(false);
            if (!removed.Success)
            {
                throw new InvalidOperationException($"Remove container '{name}': {removed.ErrorText}");
            }
        }

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
                try
                {
                    await _wslc.RemoveVolumeAsync(volume.Name, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Removing volume {Volume} failed (may be in use).", volume.Name);
                }
            }
        }
    }

    /// <summary>
    /// Restarts the whole project: brings it down (stops and removes its containers) and then back
    /// up in dependency order. Returns the up result so callers can report per-service outcomes.
    /// </summary>
    public async Task<ComposeUpResult> RestartAsync(string projectName, CancellationToken ct = default)
    {
        var maximumStopVersion = _suppression?.Version ?? long.MaxValue;
        var project = _store.Get(projectName);
        if (project is null)
        {
            return new ComposeUpResult();
        }

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var networkPlans = new Dictionary<string, NetworkStartupPlan>(StringComparer.Ordinal);
            foreach (var service in project.Services.Where(s => IsServiceActive(project, s)))
            {
                try
                {
                    networkPlans.Add(service.Name, await PreflightNetworkAsync(project, service, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ComposeUpResult { Services = [new(service.Name, false, ex.Message)] };
                }
            }

            await DownCoreAsync(projectName, removeVolumes: false, ct).ConfigureAwait(false);
            return await UpCoreAsync(project, maximumStopVersion, ct, networkPlans).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

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
                RemoveHealthChecks(project);
                RemoveRestartPolicies(project);
                var ready = new List<ComposeService>();
                foreach (var service in project.Services.Where(s => IsServiceActive(project, s)))
                {
                    var existing = FindByName(containers, ResolveContainerName(project, service));
                    if (existing is null)
                    {
                        continue;
                    }

                    try
                    {
                        var state = await _networks.InspectAsync(existing.Id, ct).ConfigureAwait(false);
                        if (!state.IsOwnedBy(project, service))
                        {
                            throw new InvalidOperationException("Container ownership does not match the saved project.");
                        }

                        var options = service.Options.Clone();
                        PrepareNetworkOptions(project, service, options);
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

    /// <summary>
    /// Computes a start order that respects <c>depends_on</c> edges (Kahn's algorithm). Any services
    /// left over by a dependency cycle are appended in their declared order so nothing is dropped.
    /// </summary>
    public static IReadOnlyList<ComposeService> ResolveOrder(ComposeProject project)
    {
        var byName = project.Services
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var indegree = project.Services.ToDictionary(s => s.Name, _ => 0, StringComparer.Ordinal);
        var dependents = project.Services.ToDictionary(s => s.Name, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var service in project.Services)
        {
            foreach (var dep in service.DependsOn)
            {
                // Only count edges to services that actually exist in this project.
                if (!byName.ContainsKey(dep.ServiceName) || dep.ServiceName == service.Name)
                {
                    continue;
                }

                indegree[service.Name]++;
                dependents[dep.ServiceName].Add(service.Name);
            }
        }

        // Seed the queue with roots, preserving declared order for determinism.
        var queue = new Queue<string>(project.Services
            .Where(s => indegree[s.Name] == 0)
            .Select(s => s.Name));

        var ordered = new List<ComposeService>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!placed.Add(name))
            {
                continue;
            }

            ordered.Add(byName[name]);
            foreach (var next in dependents[name])
            {
                if (--indegree[next] == 0)
                {
                    queue.Enqueue(next);
                }
            }
        }

        // Append any services caught in a cycle, in declared order.
        foreach (var service in project.Services)
        {
            if (placed.Add(service.Name))
            {
                ordered.Add(service);
            }
        }

        return ordered;
    }

    /// <summary>How many times to stage-and-verify a config/secret bind before giving up. Each retry
    /// stages the file at a fresh unique path to bust wslc's per-path 9P negative cache. Kept small:
    /// a single fresh-path retry clears a one-off race, and each abandoned path leaks a wslc mount
    /// slot, so more retries would work against the very limit they guard.</summary>
    private const int MaxStagedMountAttempts = 2;

    private async Task<ComposeServiceResult> StartServiceAsync(ComposeProject project, ComposeService service,
        long maximumStopVersion, CancellationToken ct, NetworkStartupPlan? networkPlan = null)
    {
        var name = ResolveContainerName(project, service);
        bool nativeNetworks;
        string? networkWarning = null;

        try
        {
            networkPlan ??= await PreflightNetworkAsync(project, service, ct).ConfigureAwait(false);
            nativeNetworks = networkPlan.Native;
            networkWarning = networkPlan.Warning;
            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var existing = FindByName(containers, name);
            if (existing is not null)
            {
                var state = await _networks.InspectAsync(existing.Id, ct).ConfigureAwait(false);
                if (!state.IsOwnedBy(project, service))
                {
                    return new ComposeServiceResult(service.Name, false,
                        $"Container '{name}' is not owned by this project and will not be replaced.");
                }

                ct.ThrowIfCancellationRequested();
                RemoveHealthChecks(ProjectWithServices(project, [service]));
                RemoveRestartPolicies(ProjectWithServices(project, [service]));
                await _wslc.StopContainerAsync(
                    existing.Id, service.StopGracePeriodSeconds, service.Options.StopSignal, ct)
                    .ConfigureAwait(false);
                var removed = await _wslc.RemoveContainerAsync(existing.Id, force: true, ct).ConfigureAwait(false);
                if (!removed.Success)
                {
                    return new ComposeServiceResult(service.Name, false, removed.ErrorText);
                }
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

        // A config/secret file bind can silently fail: under wslc session mount pressure runc can't
        // stat the host source and pre-creates it as a DIRECTORY, so the container starts but reads a
        // directory where its config should be (nginx: "Is a directory"). Worse, the raced path is
        // then poisoned — wslc's 9P layer caches the missing-source result per path, so re-running the
        // same staged path keeps failing even in an otherwise clean session. Windows-side detection is
        // unreliable (the file→directory view lags), so before running the real container we verify
        // each staged bind from INSIDE the VM (authoritative). Only a *confirmed* directory mount
        // triggers a re-stage at a FRESH path (which the cache treats as new); if the probe itself
        // can't run we fail open and let the real run surface any genuine error. Only file-mount
        // services pay the verification cost.
        string? nonce = null;
        for (var attempt = 1; attempt <= MaxStagedMountAttempts; attempt++)
        {
            var options = CloneForRun(project, service, name, nonce);
            var stagedSources = options.Volumes
                .Where(v => v.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase))
                .Select(SourceOf)
                .ToList();

            if (stagedSources.Count > 0 && await AnyStagedMountRacedAsync(stagedSources, ct).ConfigureAwait(false))
            {
                if (attempt < MaxStagedMountAttempts)
                {
                    nonce = Guid.NewGuid().ToString("N")[..8];
                    _logger.LogWarning(
                        "Staged config/secret bind for {Name} mounted as a directory (wslc mount " +
                        "pressure); re-staging at a fresh path (attempt {Next}/{Max}).",
                        name, attempt + 1, MaxStagedMountAttempts);
                    continue;
                }

                return new ComposeServiceResult(service.Name, false, MountLimitMessage);
            }

            try
            {
                string containerId;
                if (nativeNetworks)
                {
                    containerId = await _networks.CreateAndStartAsync(options, ct, maximumStopVersion).ConfigureAwait(false);
                }
                else
                {
                    var run = await _wslc.RunContainerAsync(options, ct, maximumStopVersion).ConfigureAwait(false);
                    if (!run.Success)
                    {
                        return new ComposeServiceResult(service.Name, false, Summarize(run), networkWarning);
                    }
                    var state = await _networks.InspectAsync(name, ct).ConfigureAwait(false);
                    if (!state.IsOwnedBy(project, service))
                    {
                        throw new InvalidOperationException("Started container ownership could not be verified.");
                    }
                    containerId = state.Id;
                }

                await ApplyExtraHostsAsync(name, service, ct).ConfigureAwait(false);
                return new ComposeServiceResult(service.Name, true, $"Started as {name}", networkWarning)
                {
                    ContainerId = containerId,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ComposeServiceResult(service.Name, false, ex.Message);
            }
        }

        return new ComposeServiceResult(service.Name, false, MountLimitMessage);
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
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits until the dependency container has exited with code 0 (compose
    /// <c>service_completed_successfully</c>). Returns false on timeout or a non-zero/unreadable exit.
    /// </summary>
    private async Task<bool> WaitForCompletedAsync(ComposeProject project, ComposeService dep, CancellationToken ct)
    {
        var name = ResolveContainerName(project, dep);
        var deadline = DateTimeOffset.UtcNow + HealthyWaitTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var container = FindByName(containers, name);

            // Only inspect the exit code once the container has actually stopped.
            if (container is not null &&
                container.State is ContainerState.Stopped or ContainerState.Created)
            {
                return await ExitedCleanlyAsync(container.Id, ct).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
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
        catch
        {
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

        PrepareNetworkOptions(project, service, options);

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
        return options;
    }

    private static void PrepareNetworkOptions(ComposeProject project, ComposeService service, RunContainerOptions options)
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
        }
    }

    private static ComposeProject ProjectWithServices(ComposeProject project, IEnumerable<ComposeService> services) =>
        new() { Name = project.Name, ActiveProfiles = project.ActiveProfiles, Services = services.ToList() };

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
            return;
        }

        if (!File.Exists(def.File))
        {
            _logger.LogWarning(
                "Compose {Kind} '{Name}' source file '{File}' not found; skipping mount into {Target}.",
                kind, def.Name, def.File, reference.Target);
            return;
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

            // A prior raced run may have left a directory at dest (runc pre-created the missing bind
            // source). File.Copy can't overwrite a directory, so clear it first.
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
            }

            File.Copy(source, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to stage compose {Kind} '{Name}' from '{Source}'; binding source in place.",
                kind, name, source);
            return source;
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
        string.IsNullOrWhiteSpace(service.Options.Name)
            ? project.ContainerNameFor(service.Name)
            : service.Options.Name!.Trim();

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

        if (toAdd.Count == 0)
        {
            return;
        }

        var names = new HashSet<string>(toAdd.Select(c => c.ContainerName), StringComparer.Ordinal);

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
