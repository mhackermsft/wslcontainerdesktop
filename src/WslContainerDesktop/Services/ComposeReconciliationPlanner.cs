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

using System.Security.Cryptography;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Pure lifecycle decisions over desired configuration and a single observed inventory.</summary>
public static class ComposeReconciliationPlanner
{
    public static string ContainerName(ComposeProject project, ComposeService service) =>
        string.IsNullOrWhiteSpace(service.Options.Name)
            ? project.ContainerNameFor(service.Name)
            : service.Options.Name.Trim();

    public static ComposeService DeepCloneService(ComposeService service) => CloneService(service);

    public static string BuildFingerprint(ComposeBuildConfig? build) => HashConfiguration(BuildConfiguration(build));

    public static ComposeService CloneService(ComposeService service) => new()
    {
        Name = service.Name,
        Options = service.Options.Clone(),
        DependsOn = service.DependsOn.Select(d => new ComposeDependency
        {
            ServiceName = d.ServiceName, Condition = d.Condition, Required = d.Required, Restart = d.Restart,
        }).ToList(),
        Restart = service.Restart,
        Profiles = new(service.Profiles),
        StopGracePeriodSeconds = service.StopGracePeriodSeconds,
        PullPolicy = service.PullPolicy,
        Build = service.Build is not { } build ? null : new ComposeBuildConfig
        {
            Context = build.Context, Dockerfile = build.Dockerfile, Args = new(build.Args),
            Target = build.Target, Labels = new(build.Labels, StringComparer.Ordinal),
            NoCache = build.NoCache, Pull = build.Pull,
        },
        Secrets = service.Secrets.Select(m => new ComposeFileMount { Source = m.Source, Target = m.Target }).ToList(),
        Configs = service.Configs.Select(m => new ComposeFileMount { Source = m.Source, Target = m.Target }).ToList(),
        Health = service.Health?.Clone(),
        ExtraHosts = new(service.ExtraHosts),
    };

    /// <summary>Includes namespace-sharing dependencies even for older persisted configurations.</summary>
    public static IReadOnlyList<ComposeDependency> Dependencies(ComposeService service)
    {
        var dependencies = service.DependsOn.ToList();
        var mode = service.Options.NetworkMode ?? service.Options.Network;
        if (mode?.StartsWith("service:", StringComparison.Ordinal) == true)
        {
            var name = mode["service:".Length..];
            // Sharing a namespace cannot be optional even if depends_on marks the same edge optional.
            dependencies = dependencies.Where(d => d.ServiceName != name).ToList();
            dependencies.Add(new ComposeDependency
            {
                ServiceName = name,
                Condition = service.DependsOn.FirstOrDefault(d => d.ServiceName == name)?.Condition
                    ?? DependencyCondition.ServiceStarted,
                Restart = service.DependsOn.Any(d => d.ServiceName == name && d.Restart),
            });
        }
        return dependencies;
    }

    public static IReadOnlyList<ComposeService> SelectServices(ComposeProject project, ComposeOperationRequest request)
    {
        if (!Enum.IsDefined(request.Operation))
            throw new InvalidOperationException("Unsupported Compose lifecycle operation.");
        var byName = new Dictionary<string, ComposeService>(StringComparer.Ordinal);
        foreach (var service in project.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Name) || !byName.TryAdd(service.Name, service))
                throw new InvalidOperationException("Compose service names must be nonempty and unique.");
        }

        var targets = request.Services.Distinct(StringComparer.Ordinal).ToList();
        if (targets.Any(name => !byName.ContainsKey(name)))
            throw new InvalidOperationException("A selected Compose service is not defined.");
        var profiles = project.ActiveProfiles.ToHashSet(StringComparer.Ordinal);
        if (request.Operation == ComposeLifecycleOperation.Up)
            foreach (var name in targets)
                profiles.UnionWith(byName[name].Profiles);
        bool IsActive(ComposeService s) => s.Profiles.Count == 0 || profiles.Contains("*") || s.Profiles.Any(profiles.Contains);

        var selected = (targets.Count > 0
            ? targets
            : project.Services.Where(s => request.Operation != ComposeLifecycleOperation.Up || IsActive(s))
                .Select(s => s.Name)).ToHashSet(StringComparer.Ordinal);
        if (request.Operation == ComposeLifecycleOperation.Up)
        {
            var queue = new Queue<string>(selected);
            while (queue.TryDequeue(out var name))
            {
                foreach (var dependency in Dependencies(byName[name]))
                {
                    if (!byName.TryGetValue(dependency.ServiceName, out var service) || !IsActive(service))
                    {
                        if (dependency.Required)
                            throw new InvalidOperationException("A required Compose dependency is missing or inactive.");
                        continue;
                    }
                    if (selected.Add(service.Name)) queue.Enqueue(service.Name);
                }
            }
        }
        else if (request.Operation == ComposeLifecycleOperation.Restart && targets.Count > 0)
        {
            bool added;
            do
            {
                added = false;
                foreach (var service in project.Services)
                    if (Dependencies(service).Any(d => d.Restart && selected.Contains(d.ServiceName)))
                        added |= selected.Add(service.Name);
            } while (added);
        }

        // Validate the defined graph, rather than silently appending cyclic services.
        var ordered = new List<ComposeService>();
        var indegree = byName.Keys.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
        var dependents = byName.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var service in project.Services)
        {
            foreach (var dependency in Dependencies(service).Select(d => d.ServiceName)
                .Where(byName.ContainsKey).Distinct(StringComparer.Ordinal))
            {
                indegree[service.Name]++;
                dependents[dependency].Add(service.Name);
            }
        }
        var ready = new Queue<ComposeService>(project.Services.Where(s => indegree[s.Name] == 0));
        var visited = 0;
        while (ready.TryDequeue(out var service))
        {
            visited++;
            if (selected.Contains(service.Name)) ordered.Add(service);
            foreach (var dependent in dependents[service.Name])
                if (--indegree[dependent] == 0) ready.Enqueue(byName[dependent]);
        }
        if (visited != project.Services.Count)
            throw new InvalidOperationException("The Compose dependency graph contains a cycle.");
        if (request.Operation is ComposeLifecycleOperation.Stop or ComposeLifecycleOperation.Down)
            ordered.Reverse();
        if (ordered.Select(s => ContainerName(project, s)).Distinct(StringComparer.Ordinal).Count() != ordered.Count)
            throw new InvalidOperationException("Selected Compose services resolve to the same container name.");
        return ordered.Select(CloneService).ToArray();
    }

    public static ComposeReconciliationPlan Plan(
        ComposeProject project, ComposeOperationRequest request,
        IReadOnlyList<ContainerInfo> inventory,
        IReadOnlyDictionary<string, ContainerNetworkState> inspections)
    {
        var result = new List<ComposeServicePlan>();
        foreach (var service in SelectServices(project, request))
        {
            var name = ContainerName(project, service);
            if (request.Operation == ComposeLifecycleOperation.Up &&
                project.AppliedServices.TryGetValue(service.Name, out var applied) &&
                ContainerName(project, applied.Service) != name)
            {
                result.Add(new(service, name, string.Empty, ComposeServiceChange.Incompatible,
                    ComposeServiceAction.Blocked,
                    "The container name differs from the applied configuration. Remove the old instance or bring the project down before applying the new name.",
                    applied.ContainerId));
                continue;
            }
            var matches = inventory.Where(c => c.Name.TrimStart('/') == name).ToList();
            var container = matches.FirstOrDefault();
            var fingerprint = string.Empty;
            if (request.Operation == ComposeLifecycleOperation.Up)
            {
                try { fingerprint = Fingerprint(project, service); }
                catch (InvalidOperationException)
                {
                    result.Add(new(service, name, fingerprint, ComposeServiceChange.Incompatible,
                        ComposeServiceAction.Blocked, "Desired configuration could not be validated.", container?.Id));
                    continue;
                }
            }
            if (matches.Count > 1)
            {
                result.Add(new(service, name, fingerprint, ComposeServiceChange.Incompatible,
                    ComposeServiceAction.Blocked, "Container identity is ambiguous.", null));
                continue;
            }
            if (container is null)
            {
                result.Add(new(service, name, fingerprint, ComposeServiceChange.Missing,
                    request.Operation == ComposeLifecycleOperation.Up ? ComposeServiceAction.Create : ComposeServiceAction.Keep,
                    "No container exists for this service.", null));
                continue;
            }
            if ((!inspections.TryGetValue(container.Id, out var inspected) &&
                 !inspections.TryGetValue(name, out inspected)) ||
                ContainerIdentity.ResolveId(inventory.Select(c => c.Id), inspected.Id) != container.Id ||
                inventory.Count(c => c.Id == container.Id) != 1 ||
                !inspected.IsOwnedBy(project, service))
            {
                result.Add(new(service, name, fingerprint, ComposeServiceChange.Incompatible,
                    ComposeServiceAction.Blocked, "Container ownership or inspected identity is unverified.", container.Id));
                continue;
            }
            if (container.State is not (ContainerState.Running or ContainerState.Stopped or ContainerState.Created))
            {
                result.Add(new(service, name, fingerprint, ComposeServiceChange.Incompatible,
                    ComposeServiceAction.Blocked, "The container lifecycle state is unsupported.", inspected.Id));
                continue;
            }

            inspected.Labels.TryGetValue(ComposeProject.ConfigHashLabel, out var appliedHash);
            if (request.Operation != ComposeLifecycleOperation.Up)
            {
                var action = request.Operation switch
                {
                    ComposeLifecycleOperation.Restart => ComposeServiceAction.Restart,
                    ComposeLifecycleOperation.Stop => ComposeServiceAction.Stop,
                    ComposeLifecycleOperation.Down => ComposeServiceAction.Remove,
                    _ => throw new InvalidOperationException("Unsupported Compose lifecycle operation."),
                };
                result.Add(new(service, name, appliedHash ?? string.Empty, ComposeServiceChange.Unchanged,
                    action, "Explicit lifecycle operation; desired configuration is not applied.", inspected.Id));
                continue;
            }

            var changed = appliedHash != fingerprint;
            var recreate = changed || request.ForceRecreate || (request.Build && service.Build is not null);
            var reason = string.IsNullOrEmpty(appliedHash)
                ? "Owned legacy container has no configuration fingerprint; one-time migration is required."
                : changed ? "Effective container configuration changed."
                : request.ForceRecreate ? "Container recreation was explicitly requested."
                : request.Build && service.Build is not null ? "An explicit image build was requested."
                : "Effective container configuration is unchanged.";
            result.Add(new(service, name, fingerprint,
                changed ? ComposeServiceChange.Changed : ComposeServiceChange.Unchanged,
                recreate ? ComposeServiceAction.Recreate :
                    container.State == ContainerState.Running ? ComposeServiceAction.Keep : ComposeServiceAction.Start,
                reason, inspected.Id));
        }
        return new(result.ToArray());
    }

    /// <summary>
    /// Versioned effective configuration identity. Build contexts and bind sources are identities,
    /// not recursive content snapshots: source changes require an explicit build request.
    /// File-backed secrets/configs are re-read on every preflight and never put in diagnostics.
    /// </summary>
    public static string Fingerprint(ComposeProject project, ComposeService service)
    {
        var o = service.Options;
        var attachments = o.GetNetworkAttachments();
        var networkNames = attachments.Select(a => a.Network).ToHashSet(StringComparer.Ordinal);
        var volumeNames = o.Volumes.Select(v => v.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
        var projection = new
        {
            Name = ContainerName(project, service), Image = EffectiveImage(project, service),
            Detached = true, RemoveOnExit = false, Interactive = false, o.AllGpus,
            Command = Tokens(o.Command), Entrypoint = Tokens(o.Entrypoint),
            User = Trim(o.User), WorkingDir = Trim(o.WorkingDir), Hostname = Trim(o.Hostname),
            CpuLimit = Trim(o.CpuLimit), MemoryLimit = Trim(o.MemoryLimit),
            NetworkMode = EffectiveNetworkMode(project, service),
            Networks = attachments.Select(a => new
            {
                a.Network, Aliases = Set(a.Aliases.Append(service.Name)), Ipv4Address = Trim(a.Ipv4Address),
            }).ToArray(),
            Ports = Set(o.PortMappings), Environment = Pairs(o.EnvironmentVariables), Mounts = Set(o.Volumes),
            Labels = o.Labels.Where(p => p.Key != ComposeProject.ProjectLabel &&
                p.Key != ComposeProject.ServiceLabel && p.Key != ComposeProject.ConfigHashLabel &&
                p.Key != ComposeProject.ImageIdLabel &&
                p.Key is not ("com.wsldesktop.apply-operation" or "com.wsldesktop.network-operation"))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Dns = o.Dns.Select(Trim).ToArray(), DnsSearch = o.DnsSearch.Select(Trim).ToArray(),
            DnsOptions = o.DnsOptions.Select(Trim).ToArray(), Tmpfs = Set(o.Tmpfs), Ulimits = Pairs(o.Ulimits),
            ShmSize = Trim(o.ShmSize), StopSignal = Trim(o.StopSignal), Domainname = Trim(o.Domainname),
            Health = o.Health ?? service.Health?.DesiredHealth,
            ExtraHosts = Set(service.ExtraHosts),
            Build = BuildConfiguration(service.Build),
            Secrets = FileMounts(service.Secrets, project.Secrets),
            Configs = FileMounts(service.Configs, project.Configs),
            NetworkDeclarations = project.Networks.Where(n => networkNames.Contains(n.Name))
                .OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => new
                {
                    n.Name, n.ExplicitName, n.Subnet, n.Gateway, n.IpRange, n.Driver, n.External,
                    DriverOpts = Pairs(n.DriverOpts), n.Labels,
                }).ToArray(),
            VolumeDeclarations = project.Volumes.Where(v => volumeNames.Contains(v.Name))
                .OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => new
                {
                    v.Name, v.Driver, v.External, DriverOpts = Pairs(v.DriverOpts), v.Labels,
                }).ToArray(),
        };
        return HashConfiguration(projection);
    }

    private static object? BuildConfiguration(ComposeBuildConfig? build) => build is null ? null : new
    {
        Context = FileIdentity(build.Context),
        Dockerfile = string.IsNullOrWhiteSpace(build.Dockerfile) ? "Dockerfile" : build.Dockerfile,
        Args = Pairs(build.Args), Target = Trim(build.Target), build.Labels, build.NoCache, build.Pull,
    };

    private static string HashConfiguration(object? configuration)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, JsonSerializer.SerializeToElement(configuration));
        return "v1:" + Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? EffectiveNetworkMode(ComposeProject project, ComposeService service)
    {
        if (!service.Options.HasSpecialNetworkMode) return null;
        var mode = Trim(service.Options.NetworkMode ?? service.Options.Network);
        if (mode?.StartsWith("service:", StringComparison.Ordinal) != true) return mode;
        var dependency = project.Services.SingleOrDefault(s => s.Name == mode["service:".Length..]) ??
            throw new InvalidOperationException("A namespace-sharing service is undefined.");
        return "container:" + ContainerName(project, dependency);
    }

    private static string EffectiveImage(ComposeProject project, ComposeService service)
    {
        var reference = service.Options.Image.Trim();
        if (reference.Length == 0)
            return service.Build is null ? string.Empty : $"{project.Name}_{service.Name}:latest";
        return reference.Contains('@') || reference.LastIndexOf(':') > reference.LastIndexOf('/')
            ? reference : reference + ":latest";
    }

    private static string[] Set(IEnumerable<string> values) => values.Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static SortedDictionary<string, string?> Pairs(IEnumerable<string> values)
    {
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var text = value.Trim();
            var index = text.IndexOf('=');
            result[index < 0 ? text : text[..index]] = index < 0 ? null : text[(index + 1)..];
        }
        return result;
    }

    private static string[]? Tokens(string? value) => value is null ? null :
        new RunContainerOptions { Detached = false, Command = value }.ToArguments().Skip(2).ToArray();

    private static string FileIdentity(string path)
    {
        try
        {
            // Engine-owned absolute Linux paths are case-sensitive and not host drive paths.
            if (OperatingSystem.IsWindows() && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
                return path;
            var fullPath = Path.GetFullPath(path);
            return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("A Compose file identity is invalid.");
        }
    }

    private static object[] FileMounts(IReadOnlyList<ComposeFileMount> mounts, IReadOnlyList<ComposeSecret> sources)
    {
        return mounts.OrderBy(m => m.Target, StringComparer.Ordinal).ThenBy(m => m.Source, StringComparer.Ordinal)
            .Select(m =>
            {
                var definitions = sources.Where(s => s.Name == m.Source).ToList();
                if (definitions.Count != 1 || definitions[0].External || string.IsNullOrWhiteSpace(definitions[0].File))
                    throw new InvalidOperationException("A Compose file-backed resource is unavailable.");
                var path = definitions[0].File!;
                var identity = FileIdentity(path);
                string hash;
                try
                {
                    using var file = File.OpenRead(path);
                    hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    throw new InvalidOperationException("A Compose file-backed resource is unreadable.");
                }
                return (object)new { m.Source, m.Target, Identity = identity, ContentHash = hash };
            }).ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
