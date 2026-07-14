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
public sealed record ComposeServiceResult(string Service, bool Success, string Detail);

/// <summary>Aggregate result of bringing a compose project up.</summary>
public sealed class ComposeUpResult
{
    public IReadOnlyList<ComposeServiceResult> Services { get; init; } = Array.Empty<ComposeServiceResult>();

    public bool AllSucceeded => Services.All(s => s.Success);

    public int Started => Services.Count(s => s.Success);
}

/// <summary>
/// The desktop app's compose orchestration layer ("desktop-as-daemon"). It runs each service as a
/// labelled <c>wslc</c> container in <c>depends_on</c> order, gates <c>service_healthy</c>
/// dependencies on a health probe, and enrolls services with a health check into the existing
/// <see cref="HealthWatchdog"/> so their restart policy is enforced while the app runs.
///
/// <para><b>Requires the app to be running:</b> restart and health enforcement is performed
/// in-process (there is no background daemon), so it pauses when the app is closed and resumes via
/// <see cref="ReconcileAsync"/> on the next launch.</para>
/// </summary>
public sealed class ComposeProjectSupervisor
{
    private readonly IWslcService _wslc;
    private readonly IComposeProjectStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger<ComposeProjectSupervisor> _logger;

    /// <summary>How long to wait for a <c>service_healthy</c> dependency before starting dependents.</summary>
    private static readonly TimeSpan HealthyWaitTimeout = TimeSpan.FromSeconds(90);

    public ComposeProjectSupervisor(
        IWslcService wslc,
        IComposeProjectStore store,
        ISettingsService settings,
        ILogger<ComposeProjectSupervisor> logger)
    {
        _wslc = wslc;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Brings the project up: (re)creates each service container in dependency order, gating any
    /// <c>service_healthy</c> edges on a health probe, and enrolls health/restart policies. The
    /// project is persisted so it can be managed and re-adopted later.
    /// </summary>
    public async Task<ComposeUpResult> UpAsync(ComposeProject project, CancellationToken ct = default)
    {
        _store.Save(project);

        // Provision declared networks/volumes and build any images before starting services.
        await ProvisionResourcesAsync(project, ct).ConfigureAwait(false);
        await BuildImagesAsync(project, ct).ConfigureAwait(false);

        var order = ResolveOrder(project);
        var results = new List<ComposeServiceResult>();
        var started = new HashSet<string>(StringComparer.Ordinal);

        foreach (var service in order)
        {
            ct.ThrowIfCancellationRequested();

            // Honor service_healthy dependencies before creating this service.
            foreach (var dep in service.DependsOn.Where(d => d.Condition == DependencyCondition.ServiceHealthy))
            {
                var depService = project.Services.FirstOrDefault(s =>
                    string.Equals(s.Name, dep.ServiceName, StringComparison.Ordinal));
                if (depService is null || !started.Contains(dep.ServiceName))
                {
                    continue;
                }

                var healthy = await WaitForHealthyAsync(project, depService, ct).ConfigureAwait(false);
                if (!healthy)
                {
                    _logger.LogWarning(
                        "Dependency {Dep} did not become healthy within {Timeout}s; starting {Service} anyway.",
                        dep.ServiceName, (int)HealthyWaitTimeout.TotalSeconds, service.Name);
                }
            }

            var result = await StartServiceAsync(project, service, ct).ConfigureAwait(false);
            results.Add(result);
            if (result.Success)
            {
                started.Add(service.Name);
            }
        }

        SeedHealthChecks(project);
        SeedRestartPolicies(project);
        return new ComposeUpResult { Services = results };
    }

    /// <summary>
    /// Creates the project's declared networks and volumes before its services start. External
    /// resources are assumed to already exist and are skipped; "already exists" errors are ignored so
    /// <c>up</c> is idempotent. Never throws — provisioning failures fall through to the run attempt.
    /// </summary>
    private async Task ProvisionResourcesAsync(ComposeProject project, CancellationToken ct)
    {
        foreach (var network in project.Networks.Where(n => !n.External && !string.IsNullOrWhiteSpace(n.Name)))
        {
            try
            {
                await _wslc.CreateNetworkAsync(network.Name, network.Driver, network.DriverOpts, network.Labels, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Creating network {Network} failed (may already exist).", network.Name);
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
                    build.Context, tag, build.Dockerfile, build.Args, build.Target, build.Labels, noCache: false, ct)
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
    /// </summary>
    public async Task DownAsync(string projectName, CancellationToken ct = default)
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
                continue;
            }

            await _wslc.StopContainerAsync(existing.Id, ct).ConfigureAwait(false);
            await _wslc.RemoveContainerAsync(existing.Id, force: true, ct).ConfigureAwait(false);
        }

        // Remove project-created networks (like `docker compose down`). Volumes are preserved.
        foreach (var network in project.Networks.Where(n => !n.External && !string.IsNullOrWhiteSpace(n.Name)))
        {
            try
            {
                await _wslc.RemoveNetworkAsync(network.Name, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Removing network {Network} failed (may be in use).", network.Name);
            }
        }

        RemoveHealthChecks(project);
        RemoveRestartPolicies(project);
    }

    /// <summary>
    /// Restarts the whole project: brings it down (stops and removes its containers) and then back
    /// up in dependency order. Returns the up result so callers can report per-service outcomes.
    /// </summary>
    public async Task<ComposeUpResult> RestartAsync(string projectName, CancellationToken ct = default)
    {
        var project = _store.Get(projectName);
        if (project is null)
        {
            return new ComposeUpResult();
        }

        await DownAsync(projectName, ct).ConfigureAwait(false);
        return await UpAsync(project, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup reconciliation: re-enrolls health/restart policies for stored projects whose
    /// containers still exist, so supervision resumes after the app is relaunched. Never throws.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
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
                var anyPresent = project.Services.Any(s =>
                    FindByName(containers, ResolveContainerName(project, s)) is not null);
                if (anyPresent)
                {
                    SeedHealthChecks(project);
                    SeedRestartPolicies(project);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Compose reconcile on startup failed.");
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

    private async Task<ComposeServiceResult> StartServiceAsync(ComposeProject project, ComposeService service, CancellationToken ct)
    {
        var name = ResolveContainerName(project, service);

        // Make (re)creation idempotent: drop any prior container with the same name.
        try
        {
            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var existing = FindByName(containers, name);
            if (existing is not null)
            {
                await _wslc.StopContainerAsync(existing.Id, ct).ConfigureAwait(false);
                await _wslc.RemoveContainerAsync(existing.Id, force: true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-run cleanup for {Name} failed; continuing.", name);
        }

        var options = CloneForRun(project, service, name);

        try
        {
            var run = await _wslc.RunContainerAsync(options, ct).ConfigureAwait(false);
            return run.Success
                ? new ComposeServiceResult(service.Name, true, $"Started as {name}")
                : new ComposeServiceResult(service.Name, false, Summarize(run));
        }
        catch (Exception ex)
        {
            return new ComposeServiceResult(service.Name, false, ex.Message);
        }
    }

    /// <summary>Waits until the dependency container passes its health probe (or is running if it has none).</summary>
    private async Task<bool> WaitForHealthyAsync(ComposeProject project, ComposeService dep, CancellationToken ct)
    {
        var name = ResolveContainerName(project, dep);
        var deadline = DateTimeOffset.UtcNow + HealthyWaitTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var container = FindByName(containers, name);
            if (container is not null && container.State == ContainerState.Running)
            {
                if (dep.Health is null || string.IsNullOrWhiteSpace(dep.Health.Command))
                {
                    return true; // No probe defined: "started" is the best signal we have.
                }

                try
                {
                    var probe = await _wslc.ExecAsync(container.Id, dep.Health.Command, ct).ConfigureAwait(false);
                    if (probe.Success)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Health probe for dependency {Name} threw.", name);
                }
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

    private static RunContainerOptions CloneForRun(ComposeProject project, ComposeService service, string name)
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
            Entrypoint = src.Entrypoint,
            User = src.User,
            WorkingDir = src.WorkingDir,
            Hostname = src.Hostname,
            CpuLimit = src.CpuLimit,
            MemoryLimit = src.MemoryLimit,
            Network = src.Network,
            Networks = new List<string>(src.Networks),
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

        // Give the container its service name as a network alias so siblings can resolve it by name
        // (Compose's built-in DNS discovery). Only meaningful when attached to a user network.
        if (!options.Aliases.Contains(service.Name, StringComparer.Ordinal))
        {
            options.Aliases.Add(service.Name);
        }

        // Bind-mount file-backed secrets/configs read-only (wslc has no secret store).
        foreach (var mount in service.Secrets)
        {
            AddFileMount(options, project.Secrets, mount);
        }

        foreach (var mount in service.Configs)
        {
            AddFileMount(options, project.Configs, mount);
        }

        // Tag the container so the project can be re-adopted and torn down as a unit.
        options.Labels[ComposeProject.ProjectLabel] = project.Name;
        options.Labels[ComposeProject.ServiceLabel] = service.Name;
        return options;
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

    /// <summary>Resolves a secret/config reference to its source file and adds a read-only bind mount.</summary>
    private static void AddFileMount(
        RunContainerOptions options,
        IReadOnlyList<ComposeSecret> definitions,
        ComposeFileMount reference)
    {
        var def = definitions.FirstOrDefault(d =>
            string.Equals(d.Name, reference.Source, StringComparison.Ordinal));
        if (def is null || string.IsNullOrWhiteSpace(def.File) || string.IsNullOrWhiteSpace(reference.Target))
        {
            return;
        }

        options.Volumes.Add($"{def.File}:{reference.Target}:ro");
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
            if (service.Health is null || string.IsNullOrWhiteSpace(service.Health.Command))
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
            var hasHealth = service.Health is not null && !string.IsNullOrWhiteSpace(service.Health.Command);
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
        return string.IsNullOrEmpty(text) ? $"wslc exited with code {result.ExitCode}" : text;
    }
}
