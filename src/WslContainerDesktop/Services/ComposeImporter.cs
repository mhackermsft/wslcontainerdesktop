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

using System.Text;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Parses a <c>docker-compose.yml</c> into a <see cref="ComposeProject"/> (services plus their
/// dependency graph, restart policy and health check) so the app can orchestrate it as a unit —
/// see <see cref="ComposeProjectSupervisor"/>. YAML syntax is read by YamlDotNet; Compose
/// interpolation and merge rules are applied separately, before building the persisted model.
///
/// <para>Supported per service: <c>image</c>, <c>container_name</c>, <c>command</c>,
/// <c>entrypoint</c>, <c>ports</c>, <c>environment</c>, <c>volumes</c>, <c>networks</c> /
/// <c>network_mode</c>, <c>user</c>, <c>working_dir</c>, <c>hostname</c>, <c>labels</c>,
/// <c>cpus</c> / <c>mem_limit</c> / <c>deploy.resources.limits</c>, <c>restart</c>,
/// <c>depends_on</c> (list and long/condition form) and <c>healthcheck</c>. Values support
/// Compose variable interpolation. Unknown keys produce warnings; malformed configuration
/// throws a value-free <see cref="ComposeConfigurationException"/> before persistence or deployment.</para>
/// </summary>
public static partial class ComposeImporter
{
    private abstract class Node
    {
        public string? Tag { get; set; }
        public string Source { get; set; } = "Compose input";
    }

    private sealed class NullNode : Node;

    private sealed class ScalarNode(string value) : Node
    {
        public string Value { get; } = value;
        public string Location { get; init; } = "Compose value";
        public bool IsMergeKey { get; init; }
    }

    private sealed class SequenceNode(List<Node> items) : Node
    {
        public List<Node> Items { get; } = items;
    }

    private sealed class MappingNode(Dictionary<string, Node> map) : Node
    {
        public Dictionary<string, Node> Map { get; } = map;
        // Defer this default so an args-only override cannot erase an inherited build context.
        public string? DefaultBuildContext { get; set; }

        public Node? Child(string key) => Map.TryGetValue(key, out var n) ? n : null;

        public string? Scalar(string key) => Child(key) is ScalarNode s ? s.Value : null;
    }

    /// <summary>
    /// Back-compat entry point: returns one <see cref="RunProfile"/> per service, ignoring
    /// orchestration metadata. Used by the "import as run profiles" flow.
    /// </summary>
    /// <param name="yaml">The compose document text.</param>
    /// <param name="baseDirectory">
    /// Directory the compose file was loaded from, used to resolve <c>.env</c> and relative
    /// <c>env_file</c> / bind-mount paths. Null when the source has no on-disk location.
    /// </param>
    public static IReadOnlyList<RunProfile> Parse(string yaml, string? baseDirectory = null)
    {
        var project = ParseProject(yaml, environment: null, baseDirectory);
        var profiles = new List<RunProfile>();
        foreach (var service in project.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Options.Image))
            {
                continue;
            }

            service.Options.Name ??= service.Name;
            profiles.Add(new RunProfile { Name = service.Name, Options = service.Options });
        }

        return profiles;
    }

    /// <summary>
    /// Parses <paramref name="yaml"/> into a <see cref="ComposeProject"/>. Returns a project with
    /// no services when no <c>services:</c> block is present. Rejects malformed configuration.
    /// </summary>
    /// <param name="yaml">The compose document text.</param>
    /// <param name="environment">
    /// Variables used for interpolation. Explicit entries override the process environment,
    /// which overrides the sibling .env file. Empty and unset values remain distinct.
    /// </param>
    /// <param name="baseDirectory">
    /// Directory the compose file was loaded from. When supplied, a sibling <c>.env</c> file seeds
    /// interpolation defaults and relative <c>env_file</c> paths resolve against it.
    /// </param>
    public static ComposeProject ParseProject(
        string yaml,
        IReadOnlyDictionary<string, string>? environment = null,
        string? baseDirectory = null)
    {
        var effectiveEnv = BuildInterpolationEnvironment(environment, baseDirectory);
        var interpolationWarnings = new List<string>();
        var graph = new FileGraph(interpolationWarnings);
        var root = graph.LoadMain(yaml, baseDirectory, effectiveEnv);
        return ProjectRoot(root, baseDirectory, effectiveEnv, interpolationWarnings);
    }

    /// <summary>
    /// Loads an explicit ordered Compose file set as one project. All override paths use the
    /// first file's directory; no implicit sibling override is discovered.
    /// </summary>
    public static ComposeProject ParseProjectFiles(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (files.Count == 0)
            throw FileError("Compose files", "expected a nonempty ordered file list");
        var paths = files.Select((file, index) =>
            RequiredPath(RequiredScalar(new ScalarNode(file), $"Compose files[{index + 1}]"),
                null, $"Compose files[{index + 1}]")).ToList();
        var directory = Path.GetDirectoryName(paths[0]);
        var effectiveEnv = BuildInterpolationEnvironment(environment, directory);
        var warnings = new List<string>();
        var root = new FileGraph(warnings).LoadFiles(paths, directory, effectiveEnv);
        return ProjectRoot(root, directory, effectiveEnv, warnings);
    }

    private static ComposeProject ProjectRoot(MappingNode root, string? baseDirectory,
        IReadOnlyDictionary<string, string> effectiveEnv, List<string> interpolationWarnings)
    {
        ValidateServices(root);
        ValidateFileResources(root);

        var project = new ComposeProject
        {
            Name = SanitizeProjectName(root.Scalar("name")),
            ActiveProfiles = ParseActiveProfiles(effectiveEnv),
            Warnings = interpolationWarnings.Distinct(StringComparer.Ordinal).ToList(),
        };

        if (root.Child("services") is not MappingNode services)
        {
            return project;
        }

        foreach (var (name, node) in services.Map)
        {
            if (node is not MappingNode svc || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var service = BuildService(name, svc, baseDirectory);
            if (service is not null)
            {
                project.Services.Add(service);
                CollectServiceWarnings(name, svc, service, project.Warnings);
            }
        }

        project.Networks = ParseTopLevelNetworks(root.Child("networks"));
        if (root.Child("networks") is MappingNode declaredNetworks)
        {
            foreach (var (name, node) in declaredNetworks.Map)
            {
                if (node is MappingNode network && network.Child("ipam") is MappingNode ipam &&
                    ipam.Child("config") is SequenceNode configurations && configurations.Items.Count > 1)
                {
                    project.Warnings.Add($"Network '{name}': only the first IPAM subnet is supported by WSLC.");
                }
            }
        }
        project.Volumes = ParseTopLevelVolumes(root.Child("volumes"));
        project.Secrets = ParseTopLevelSecrets(root.Child("secrets"), baseDirectory);
        project.Configs = ParseTopLevelSecrets(root.Child("configs"), baseDirectory);
        CollectTopLevelWarnings(root, project.Warnings);

        return project;
    }

    // Service keys the parser actually honors. Any other key is reported as unsupported at import.
    private static readonly HashSet<string> SupportedServiceKeys = new(StringComparer.Ordinal)
    {
        "image", "container_name", "command", "entrypoint", "user", "working_dir", "hostname",
        "ports", "volumes", "environment", "labels", "env_file", "build", "pull_policy", "restart",
        "depends_on", "profiles", "stop_grace_period", "secrets", "configs", "healthcheck",
        "extra_hosts", "networks", "network_mode", "tmpfs", "dns", "dns_search", "dns_opt", "ulimits",
        "shm_size", "stop_signal", "domainname", "cpus", "mem_limit", "deploy", "extends", "scale",
    };

    // Top-level keys the parser understands. Anything else (except `x-` extensions) is reported.
    private static readonly HashSet<string> SupportedTopLevelKeys = new(StringComparer.Ordinal)
    {
        "version", "name", "services", "networks", "volumes", "secrets", "configs", "include",
    };

    /// <summary>
    /// Adds a warning for every service key the parser does not honor, plus targeted notes for
    /// partially-supported features (multi-network attach and Swarm deploy settings), so the
    /// user knows what was dropped before bringing the project up.
    /// </summary>
    private static void CollectServiceWarnings(
        string name, MappingNode svc, ComposeService service, List<string> warnings)
    {
        foreach (var key in svc.Map.Keys)
        {
            if (!SupportedServiceKeys.Contains(key) && !key.StartsWith("x-", StringComparison.Ordinal))
            {
                warnings.Add($"Service '{name}': '{key}' is not supported and was ignored.");
            }
        }

        if (service.Options.Networks.Count > 1)
        {
            warnings.Add(
                $"Service '{name}': multiple networks require WSLC network connect support. " +
                "Older engines attach only the first network; all desired endpoint settings are retained.");
        }

        if (!string.IsNullOrWhiteSpace(svc.Scalar("network_mode")) && svc.Child("networks") is not null)
        {
            warnings.Add($"Service '{name}': 'networks' is ignored when 'network_mode' is set.");
        }

        if (svc.Child("networks") is MappingNode networks)
        {
            foreach (var (network, node) in networks.Map)
            {
                if (node is MappingNode endpoint)
                {
                    foreach (var key in endpoint.Map.Keys.Where(k => k is not "aliases" and not "ipv4_address"))
                    {
                        warnings.Add($"Service '{name}', network '{network}': endpoint setting '{key}' is not supported and was ignored.");
                    }
                }
            }
        }

        if (svc.Child("deploy") is MappingNode deploy)
        {
            foreach (var key in deploy.Map.Keys.Where(key =>
                key is not "replicas" and not "mode" and not "resources" &&
                !key.StartsWith("x-", StringComparison.Ordinal)))
            {
                warnings.Add($"Service '{name}': 'deploy.{key}' is not supported and was ignored; local scaling does not implement Swarm.");
            }

            // A GPU reservation is honoured, but only as all-or-nothing passthrough.
            if (deploy.Child("resources") is MappingNode deployResources &&
                deployResources.Child("reservations") is MappingNode reservations &&
                reservations.Child("devices") is SequenceNode devices)
            {
                foreach (var device in devices.Items.OfType<MappingNode>().Where(RequestsGpu))
                {
                    var count = Unquote(device.Scalar("count") ?? string.Empty).Trim();
                    if (device.Child("device_ids") is not null ||
                        (count.Length > 0 && !count.Equals("all", StringComparison.OrdinalIgnoreCase)))
                    {
                        warnings.Add($"Service '{name}': GPU reservation imported as all GPUs (per-device selection isn't supported).");
                    }
                }
            }
        }

        foreach (var field in new[] { "ports", "volumes", "secrets", "configs" })
        {
            if (svc.Child(field) is not SequenceNode resources) continue;
            var supported = field switch
            {
                "ports" => new[] { "target", "published", "host_ip", "protocol" },
                "volumes" => ["type", "source", "target", "read_only", "consistency"],
                _ => ["source", "target"],
            };
            foreach (var resource in resources.Items.OfType<MappingNode>())
            foreach (var key in resource.Map.Keys)
                if (!supported.Contains(key, StringComparer.Ordinal) && !key.StartsWith("x-", StringComparison.Ordinal))
                    warnings.Add($"Service '{name}': '{field}.{key}' is not supported and was ignored.");
        }
    }

    /// <summary>Adds a warning for every unrecognized top-level key (ignoring <c>x-</c> extensions).</summary>
    private static void CollectTopLevelWarnings(MappingNode root, List<string> warnings)
    {
        foreach (var key in root.Map.Keys)
        {
            if (!SupportedTopLevelKeys.Contains(key) && !key.StartsWith("x-", StringComparison.Ordinal))
            {
                warnings.Add($"Top-level '{key}:' is not supported and was ignored.");
            }
        }
    }

    // Compose's default override file names, in precedence order (later ones do not override earlier).
    private static readonly string[] OverrideFileNames =
    {
        "docker-compose.override.yml",
        "docker-compose.override.yaml",
        "compose.override.yml",
        "compose.override.yaml",
    };

    /// <summary>
    /// Reads the active compose profiles from the <c>COMPOSE_PROFILES</c> environment variable (the
    /// same mechanism <c>docker compose</c> uses), split on commas.
    /// </summary>
    private static List<string> ParseActiveProfiles(IReadOnlyDictionary<string, string>? env)
    {
        if (env is null || !env.TryGetValue("COMPOSE_PROFILES", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>Returns a copy of <paramref name="map"/> with <paramref name="key"/> removed.</summary>
    private static MappingNode StripKey(MappingNode map, string key)
    {
        var copy = new Dictionary<string, Node>(map.Map, StringComparer.Ordinal);
        copy.Remove(key);
        return new MappingNode(copy) { Tag = map.Tag, Source = map.Source, DefaultBuildContext = map.DefaultBuildContext };
    }

    /// <summary>
    /// Merges an explicit interpolation environment with a sibling <c>.env</c> file (when a base
    /// directory is given). Explicitly-provided variables win over <c>.env</c> entries.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildInterpolationEnvironment(
        IReadOnlyDictionary<string, string>? environment,
        string? baseDirectory)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            foreach (var (key, value) in ReadEnvFile(Path.Combine(baseDirectory, ".env"), false, "Compose input .env"))
            {
                merged[key] = value;
            }
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            merged[(string)entry.Key] = (string)entry.Value!;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                merged[key] = value; // explicit values take precedence over the .env file
            }
        }

        return merged;
    }

    private static ComposeService? BuildService(string serviceName, MappingNode svc, string? baseDirectory)
    {
        var options = new RunContainerOptions
        {
            Image = svc.Scalar("image")?.Trim() ?? string.Empty,
            Name = svc.Scalar("container_name")?.Trim(),
            Command = JoinCommand(svc.Child("command")),
            Entrypoint = JoinCommand(svc.Child("entrypoint")),
            User = svc.Scalar("user")?.Trim(),
            WorkingDir = svc.Scalar("working_dir")?.Trim(),
            Hostname = svc.Scalar("hostname")?.Trim(),
            PortMappings = CollectPorts(svc.Child("ports")),
            Volumes = CollectVolumes(svc.Child("volumes")).Select(v => NormalizeVolumeSpec(v, baseDirectory)).ToList(),
            EnvironmentVariables = CollectKeyValues(svc.Child("environment")),
        };

        options.Labels = CollectLabels(svc.Child("labels"));
        MergeEnvFiles(options, svc.Child("env_file"), baseDirectory);
        ApplyNetwork(options, svc);
        ApplyResourceLimits(options, svc);
        ApplyRuntimeOptions(options, svc);

        var build = ParseBuild(svc.Child("build"), baseDirectory);
        var pullPolicy = ParsePullPolicy(svc.Scalar("pull_policy"));
        if (pullPolicy == ComposeImagePolicy.Build && build is null)
            throw new ComposeConfigurationException("Compose pull_policy build requires a build definition.");

        // A service needs either an image to run or a build section to produce one.
        if (string.IsNullOrWhiteSpace(options.Image) && build is null)
        {
            throw new ComposeConfigurationException($"{svc.Source}: a Compose service has neither image nor build. Supply one before importing.");
        }

        var service = new ComposeService
        {
            Name = serviceName,
            Options = options,
            Replicas = ParseReplicas(svc),
            Restart = ParseRestart(svc.Scalar("restart")),
            DependsOn = ParseDependsOn(svc.Child("depends_on")),
            Build = build,
            PullPolicy = pullPolicy,
            Profiles = CollectStrings(svc.Child("profiles")),
            StopGracePeriodSeconds = ParseDurationSeconds(svc.Scalar("stop_grace_period")),
            Secrets = ParseFileMounts(svc.Child("secrets"), "/run/secrets/"),
            Configs = ParseFileMounts(svc.Child("configs"), "/"),
        };

        service.Health = ParseHealthCheck(svc.Child("healthcheck"), service.Restart, svc.Scalar("restart"));
        options.Health = service.Health?.DesiredHealth?.Clone();
        service.ExtraHosts = ParseExtraHosts(svc.Child("extra_hosts"));
        if (options.NetworkMode?.StartsWith("service:", StringComparison.Ordinal) == true)
        {
            var dependency = options.NetworkMode["service:".Length..];
            var declaredDependency = service.DependsOn.FirstOrDefault(d => d.ServiceName == dependency);
            if (declaredDependency is null)
            {
                service.DependsOn.Add(new ComposeDependency { ServiceName = dependency });
            }
            else
            {
                declaredDependency.Required = true;
            }
        }

        return service;
    }

    private static int ParseReplicas(MappingNode svc)
    {
        var deploy = svc.Child("deploy") as MappingNode;
        if (deploy?.Child("mode") is { } mode &&
            (mode is not ScalarNode modeScalar || modeScalar.Value != "replicated"))
            throw ShapeError("deploy.mode", "replicated (Swarm global and job modes are not supported)");

        static int? Count(Node? node, string field)
        {
            if (node is null) return null;
            if (node is not ScalarNode scalar ||
                !int.TryParse(scalar.Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var count))
                throw ShapeError(field, "a nonnegative 32-bit integer");
            return count;
        }

        var scale = Count(svc.Child("scale"), "scale");
        var replicas = Count(deploy?.Child("replicas"), "deploy.replicas");
        if (scale.HasValue && replicas.HasValue && scale != replicas)
            throw ShapeError("scale", "the same value as deploy.replicas when both are specified");
        return scale ?? replicas ?? 1;
    }

    /// <summary>
    /// Parses compose <c>extra_hosts:</c> into <c>host:ip</c> strings, accepting the list form
    /// (<c>- "host:ip"</c> or <c>- host=ip</c>) and the mapping form (<c>host: ip</c>).
    /// </summary>
    private static List<string> ParseExtraHosts(Node? node)
    {
        var hosts = new List<string>();
        switch (node)
        {
            case SequenceNode seq:
                foreach (var item in seq.Items.OfType<ScalarNode>())
                {
                    var entry = NormalizeHostEntry(item.Value);
                    if (entry is not null)
                    {
                        hosts.Add(entry);
                    }
                }

                break;

            case MappingNode map:
                foreach (var (host, value) in map.Map)
                {
                    if (!string.IsNullOrWhiteSpace(host) && value is ScalarNode ip)
                    {
                        var entry = NormalizeHostEntry($"{host}:{ip.Value}");
                        if (entry is not null)
                        {
                            hosts.Add(entry);
                        }
                    }
                }

                break;
        }

        return hosts;
    }

    /// <summary>Normalizes a host entry to <c>host:ip</c>, accepting <c>host:ip</c> or <c>host=ip</c>.</summary>
    private static string? NormalizeHostEntry(string raw)
    {
        var value = Unquote(raw).Trim();
        var sep = value.IndexOfAny(new[] { '=', ':' });
        if (sep <= 0 || sep >= value.Length - 1)
        {
            return null;
        }

        var host = value[..sep].Trim();
        var ip = value[(sep + 1)..].Trim();
        return string.IsNullOrEmpty(host) || string.IsNullOrEmpty(ip) ? null : $"{host}:{ip}";
    }

    /// <summary>
    /// Reads compose runtime options that map directly to <c>wslc run</c> flags but have no bearing
    /// on orchestration: <c>tmpfs</c>, <c>ulimits</c>, <c>shm_size</c>, <c>stop_signal</c>,
    /// <c>domainname</c>, and the <c>dns</c> family.
    /// </summary>
    private static void ApplyRuntimeOptions(RunContainerOptions options, MappingNode svc)
    {
        options.Tmpfs = CollectStrings(svc.Child("tmpfs"));
        options.Dns = CollectStrings(svc.Child("dns"));
        options.DnsSearch = CollectStrings(svc.Child("dns_search"));
        options.DnsOptions = CollectStrings(svc.Child("dns_opt"));
        options.Ulimits = CollectUlimits(svc.Child("ulimits"));

        var shm = svc.Scalar("shm_size");
        if (!string.IsNullOrWhiteSpace(shm))
        {
            options.ShmSize = shm.Trim();
        }

        var stopSignal = svc.Scalar("stop_signal");
        if (!string.IsNullOrWhiteSpace(stopSignal))
        {
            options.StopSignal = stopSignal.Trim();
        }

        var domain = svc.Scalar("domainname");
        if (!string.IsNullOrWhiteSpace(domain))
        {
            options.Domainname = domain.Trim();
        }
    }

    /// <summary>
    /// Reads compose <c>ulimits:</c> into <c>name=soft[:hard]</c> strings, accepting the short scalar
    /// form (<c>nofile: 65535</c>) and the long mapping form (<c>nofile: { soft: 1, hard: 2 }</c>).
    /// </summary>
    private static List<string> CollectUlimits(Node? node)
    {
        var items = new List<string>();
        if (node is not MappingNode map)
        {
            return items;
        }

        foreach (var (name, value) in map.Map)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            switch (value)
            {
                case ScalarNode s when !string.IsNullOrWhiteSpace(s.Value):
                    items.Add($"{name.Trim()}={s.Value.Trim()}");
                    break;

                case MappingNode limits:
                    var soft = limits.Scalar("soft")?.Trim();
                    var hard = limits.Scalar("hard")?.Trim();
                    if (!string.IsNullOrWhiteSpace(soft) && !string.IsNullOrWhiteSpace(hard))
                    {
                        items.Add($"{name.Trim()}={soft}:{hard}");
                    }
                    else if (!string.IsNullOrWhiteSpace(soft))
                    {
                        items.Add($"{name.Trim()}={soft}");
                    }

                    break;
            }
        }

        return items;
    }

    /// <summary>
    /// Parses a service <c>build:</c> section. The short form is a context path string; the long form
    /// is a mapping with <c>context</c>, <c>dockerfile</c>, <c>args</c>, <c>target</c>, and <c>labels</c>.
    /// The context (and a relative dockerfile) resolve against <paramref name="baseDirectory"/>.
    /// </summary>
    private static ComposeBuildConfig? ParseBuild(Node? node, string? baseDirectory)
    {
        string? context;
        string? dockerfile = null;
        var args = new List<string>();
        string? target = null;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var noCache = false;
        var pull = false;

        switch (node)
        {
            case ScalarNode s when !string.IsNullOrWhiteSpace(s.Value):
                context = s.Value.Trim();
                break;

            case MappingNode map:
                context = map.Scalar("context")?.Trim();
                dockerfile = map.Scalar("dockerfile")?.Trim();
                target = map.Scalar("target")?.Trim();
                args = CollectKeyValues(map.Child("args"));
                labels = CollectLabels(map.Child("labels"));
                noCache = string.Equals(map.Scalar("no_cache")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                pull = string.Equals(map.Scalar("pull")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                break;

            default:
                return null;
        }

        if (string.IsNullOrWhiteSpace(context))
        {
            return null;
        }

        return new ComposeBuildConfig
        {
            Context = ResolvePath(context, baseDirectory),
            Dockerfile = string.IsNullOrWhiteSpace(dockerfile) ? null : dockerfile,
            Args = args,
            Target = string.IsNullOrWhiteSpace(target) ? null : target,
            Labels = labels,
            NoCache = noCache,
            Pull = pull,
        };
    }

    /// <summary>
    /// Parses a service <c>secrets:</c> / <c>configs:</c> reference list into <see cref="ComposeFileMount"/>s.
    /// Short form is a name (mounted at <paramref name="defaultTargetDir"/> + name); long form supplies
    /// <c>source</c> and optional <c>target</c>.
    /// </summary>
    private static List<ComposeFileMount> ParseFileMounts(Node? node, string defaultTargetDir)
    {
        var mounts = new List<ComposeFileMount>();
        if (node is not SequenceNode seq)
        {
            return mounts;
        }

        foreach (var item in seq.Items)
        {
            switch (item)
            {
                case ScalarNode s when !string.IsNullOrWhiteSpace(s.Value):
                    var name = s.Value.Trim();
                    mounts.Add(new ComposeFileMount { Source = name, Target = defaultTargetDir + name });
                    break;

                case MappingNode map:
                    var source = map.Scalar("source")?.Trim();
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        break;
                    }

                    var target = map.Scalar("target")?.Trim();
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        target = defaultTargetDir + source;
                    }
                    else if (!target.StartsWith('/'))
                    {
                        // configs allow a bare filename target; anchor it under the default dir.
                        target = defaultTargetDir + target;
                    }

                    mounts.Add(new ComposeFileMount { Source = source, Target = target });
                    break;
            }
        }

        return mounts;
    }

    /// <summary>Parses the top-level <c>networks:</c> mapping into <see cref="ComposeNetwork"/> definitions.</summary>
    private static List<ComposeNetwork> ParseTopLevelNetworks(Node? node)
    {
        var result = new List<ComposeNetwork>();
        if (node is not MappingNode map)
        {
            return result;
        }

        foreach (var (name, value) in map.Map)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var cfg = value as MappingNode;
            var ipam = (cfg?.Child("ipam") as MappingNode)?.Child("config") as SequenceNode;
            var subnet = ipam?.Items.OfType<MappingNode>().FirstOrDefault();
            result.Add(new ComposeNetwork
            {
                Name = name.Trim(),
                ExplicitName = cfg?.Scalar("name")?.Trim(),
                Subnet = subnet?.Scalar("subnet")?.Trim(),
                Gateway = subnet?.Scalar("gateway")?.Trim(),
                IpRange = subnet?.Scalar("ip_range")?.Trim(),
                Driver = cfg?.Scalar("driver")?.Trim(),
                DriverOpts = CollectKeyValues(cfg?.Child("driver_opts")),
                Labels = CollectLabels(cfg?.Child("labels")),
                External = IsExternal(cfg?.Child("external")),
            });
        }

        return result;
    }

    /// <summary>Parses the top-level <c>volumes:</c> mapping into <see cref="ComposeVolume"/> definitions.</summary>
    private static List<ComposeVolume> ParseTopLevelVolumes(Node? node)
    {
        var result = new List<ComposeVolume>();
        if (node is not MappingNode map)
        {
            return result;
        }

        foreach (var (name, value) in map.Map)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var cfg = value as MappingNode;
            result.Add(new ComposeVolume
            {
                Name = name.Trim(),
                Driver = cfg?.Scalar("driver")?.Trim(),
                DriverOpts = CollectKeyValues(cfg?.Child("driver_opts")),
                Labels = CollectLabels(cfg?.Child("labels")),
                External = IsExternal(cfg?.Child("external")),
            });
        }

        return result;
    }

    /// <summary>
    /// Parses a top-level <c>secrets:</c> / <c>configs:</c> mapping. Only file-backed sources are
    /// materializable; the source path resolves against <paramref name="baseDirectory"/>.
    /// </summary>
    private static List<ComposeSecret> ParseTopLevelSecrets(Node? node, string? baseDirectory)
    {
        var result = new List<ComposeSecret>();
        if (node is not MappingNode map)
        {
            return result;
        }

        foreach (var (name, value) in map.Map)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var cfg = value as MappingNode;
            var file = cfg?.Scalar("file")?.Trim();
            result.Add(new ComposeSecret
            {
                Name = name.Trim(),
                File = string.IsNullOrWhiteSpace(file) ? null : ResolvePath(file, baseDirectory),
                External = IsExternal(cfg?.Child("external")),
            });
        }

        return result;
    }

    /// <summary>True when an <c>external:</c> node is <c>true</c> or a mapping (compose's external form).</summary>
    private static bool IsExternal(Node? node) => node switch
    {
        ScalarNode s => string.Equals(s.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
        MappingNode => true,
        _ => false,
    };

    private static void ApplyNetwork(RunContainerOptions options, MappingNode svc)
    {
        var mode = svc.Scalar("network_mode");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            options.NetworkMode = mode.Trim();
            options.Network = mode.Trim();
            if (options.Network is not null)
            {
                options.Networks.Add(options.Network);
            }

            return;
        }

        // networks: may be a sequence (["frontend", "backend"]) or a mapping (frontend: {...}).
        // Endpoint aliases and addresses must not leak to a sibling network.
        var endpoints = new List<NetworkAttachment>();
        switch (svc.Child("networks"))
        {
            case SequenceNode seq:
                foreach (var item in seq.Items.OfType<ScalarNode>())
                {
                    var n = Unquote(item.Value).Trim();
                    if (!string.IsNullOrWhiteSpace(n))
                    {
                        endpoints.Add(new NetworkAttachment { Network = n });
                    }
                }

                break;
            case MappingNode map:
                foreach (var (key, value) in map.Map)
                {
                    var n = Unquote(key).Trim();
                    if (!string.IsNullOrWhiteSpace(n))
                    {
                        var config = value as MappingNode;
                        endpoints.Add(new NetworkAttachment
                        {
                            Network = n,
                            Aliases = CollectStrings(config?.Child("aliases")).Distinct(StringComparer.Ordinal).ToList(),
                            Ipv4Address = config?.Scalar("ipv4_address")?.Trim(),
                        });
                    }
                }

                break;
        }

        options.NetworkAttachments = endpoints.GroupBy(n => n.Network, StringComparer.Ordinal)
            .Select(g => g.First()).ToList();
        options.Networks = options.NetworkAttachments.Select(n => n.Network).ToList();
        options.Network = options.Networks.FirstOrDefault();
    }

    /// <summary>
    /// Reads a compose <c>ports:</c> node into raw <c>host:container[/proto]</c> strings, accepting
    /// both the short string form (<c>"8080:80"</c>) and the long mapping form
    /// (<c>{ target: 80, published: 8080, protocol: tcp }</c>).
    /// </summary>
    private static List<string> CollectPorts(Node? node)
    {
        var items = new List<string>();
        if (node is not SequenceNode seq)
        {
            return CollectStrings(node);
        }

        foreach (var item in seq.Items)
        {
            switch (item)
            {
                case ScalarNode s when !string.IsNullOrWhiteSpace(s.Value):
                    items.Add(s.Value.Trim());
                    break;

                case MappingNode map:
                    var target = map.Scalar("target")?.Trim();
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        break;
                    }

                    var published = map.Scalar("published")?.Trim();
                    var proto = map.Scalar("protocol")?.Trim();
                    var mapping = string.IsNullOrWhiteSpace(published) ? target : $"{published}:{target}";
                    if (map.Scalar("host_ip") is { Length: > 0 } hostIp)
                        mapping = $"{(hostIp.Contains(':') ? $"[{hostIp}]" : hostIp)}:{mapping}";
                    if (!string.IsNullOrWhiteSpace(proto) && !proto.Equals("tcp", StringComparison.OrdinalIgnoreCase))
                    {
                        mapping += $"/{proto}";
                    }

                    items.Add(mapping);
                    break;
            }
        }

        return items;
    }

    /// <summary>
    /// Reads a compose <c>volumes:</c> node into raw <c>source:target[:ro]</c> strings, accepting
    /// both the short string form (<c>"./data:/data"</c>) and the long mapping form
    /// (<c>{ type: bind, source: ./data, target: /data, read_only: true }</c>).
    /// </summary>
    private static List<string> CollectVolumes(Node? node)
    {
        var items = new List<string>();
        if (node is not SequenceNode seq)
        {
            return CollectStrings(node);
        }

        foreach (var item in seq.Items)
        {
            switch (item)
            {
                case ScalarNode s when !string.IsNullOrWhiteSpace(s.Value):
                    items.Add(s.Value.Trim());
                    break;

                case MappingNode map:
                    var target = map.Scalar("target")?.Trim();
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        break;
                    }

                    var source = map.Scalar("source")?.Trim();
                    var readOnly = string.Equals(map.Scalar("read_only")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    string spec;
                    if (string.IsNullOrWhiteSpace(source))
                    {
                        spec = target; // anonymous volume
                    }
                    else
                    {
                        spec = $"{source}:{target}";
                    }

                    if (readOnly)
                    {
                        spec += ":ro";
                    }

                    items.Add(spec);
                    break;
            }
        }

        return items;
    }

    /// <summary>
    /// Merges variables from service-level <c>env_file:</c> entries into the container environment.
    /// Relative paths resolve against <paramref name="baseDirectory"/> (the compose file's folder);
    /// entries already provided by <c>environment:</c> win and are never overwritten.
    /// </summary>
    private static void MergeEnvFiles(RunContainerOptions options, Node? node, string? baseDirectory)
    {
        if (node is null or NullNode) return;
        if (node is not SequenceNode files) throw ShapeError("env_file", "a path or list of paths");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < files.Items.Count; i++)
        {
            var item = files.Items[i];
            var context = $"{item.Source} env_file[{i + 1}]";
            var required = item is not MappingNode map || ValidateEnvFile(map, context);
            var path = RequiredScalar(item is MappingNode fileOptions ? fileOptions.Child("path") : item, context);
            var resolved = ResolvePath(path, baseDirectory);
            foreach (var (key, value) in ReadEnvFile(resolved, required, context))
            {
                values[key] = $"{key}={value}";
            }
        }
        foreach (var value in options.EnvironmentVariables)
            values[value.Split('=', 2)[0]] = value;
        options.EnvironmentVariables = values.Values.ToList();
    }

    /// <summary>Resolves a possibly-relative path against the compose file's directory when known.</summary>
    private static string ResolvePath(string path, string? baseDirectory)
    {
        var p = path.Trim();
        if (p.Contains('\0') || (p.Length >= 2 && p[1] == ':' && !IsAbsolutePath(p)) ||
            (p.StartsWith('\\') && !p.StartsWith(@"\\", StringComparison.Ordinal)))
            throw FileError("Compose path", "invalid or drive-relative path");
        // Preserve engine/Linux paths and remote build contexts. Required local inputs reject URLs.
        if (p.StartsWith('/') || p.Contains("://", StringComparison.Ordinal) || p.StartsWith("git@", StringComparison.Ordinal))
            return p;
        if (string.IsNullOrEmpty(p))
        {
            return p;
        }

        try
        {
            if (!OperatingSystem.IsWindows() && IsAbsolutePath(p)) return p;
            if (IsAbsolutePath(p)) return Path.GetFullPath(p);
            if (string.IsNullOrWhiteSpace(baseDirectory)) return p;
            return Path.GetFullPath(p, baseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw FileError("Compose path", "invalid path");
        }
    }

    /// <summary>
    /// Normalizes a short-form volume spec (<c>source:target[:mode]</c>) so it is usable by the WSL
    /// container engine: relative bind-mount host sources (starting with <c>.</c>) are resolved to an
    /// absolute path against the compose file's directory (matching Docker Compose semantics), and
    /// macOS/Docker consistency hints (<c>cached</c>, <c>delegated</c>, <c>consistent</c>) — which are
    /// no-ops the WSL engine rejects — are stripped. Named volumes and absolute paths are left intact.
    /// </summary>
    private static string NormalizeVolumeSpec(string spec, string? baseDirectory)
    {
        var trimmed = spec.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var (source, target, mode) = SplitVolumeSpec(trimmed);
        if (target is null)
        {
            return trimmed;
        }

        if (source is not null
            && !string.IsNullOrWhiteSpace(baseDirectory)
            && (source.StartsWith("./", StringComparison.Ordinal)
                || source.StartsWith("../", StringComparison.Ordinal)
                || source.StartsWith(".\\", StringComparison.Ordinal)
                || source.StartsWith("..\\", StringComparison.Ordinal)
                || source == "."
                || source == ".."))
        {
            source = ResolvePath(source, baseDirectory);
        }

        if (mode is not null)
        {
            var kept = mode
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(m => !m.Equals("cached", StringComparison.OrdinalIgnoreCase)
                    && !m.Equals("delegated", StringComparison.OrdinalIgnoreCase)
                    && !m.Equals("consistent", StringComparison.OrdinalIgnoreCase));
            mode = string.Join(',', kept);
        }

        var result = source is null ? target : $"{source}:{target}";
        if (!string.IsNullOrWhiteSpace(mode))
        {
            result += $":{mode}";
        }

        return result;
    }

    /// <summary>
    /// Splits a short-form volume spec into source / target / mode, tolerating a Windows drive-letter
    /// colon in the source (e.g. <c>C:\host:/container</c>). A spec with no separator is treated as an
    /// anonymous volume (source is null, the whole value is the container path).
    /// </summary>
    private static (string? Source, string? Target, string? Mode) SplitVolumeSpec(string spec)
    {
        var searchStart = 0;
        if (spec.Length >= 2 && char.IsLetter(spec[0]) && spec[1] == ':')
        {
            searchStart = 2;
        }

        var firstColon = spec.IndexOf(':', searchStart);
        if (firstColon < 0)
        {
            return (null, spec, null);
        }

        var source = spec[..firstColon];
        var rest = spec[(firstColon + 1)..];
        if (source.StartsWith('/') && rest.Split(',').All(m => m is "ro" or "rw" or "cached" or "delegated" or "consistent"))
            return (null, source, rest);

        var modeColon = rest.IndexOf(':');
        if (modeColon < 0)
        {
            return (source, rest, null);
        }

        return (source, rest[..modeColon], rest[(modeColon + 1)..]);
    }

    /// <summary>Reads the supported dotenv subset; only an explicitly optional absent file is skipped.</summary>
    private static IEnumerable<KeyValuePair<string, string>> ReadEnvFile(string path, bool required, string context)
    {
        var text = ReadInput(path, required, context);
        if (text is null) yield break;
        var lineNumber = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                throw FileError($"{context} line {lineNumber}", "malformed or unsupported dotenv entry; expected KEY=VALUE");
            }

            var key = line[..eq].Trim();
            if (key.Any(char.IsWhiteSpace) || key.Contains('\0'))
                throw FileError($"{context} line {lineNumber}", "malformed dotenv key");
            var rawValue = line[(eq + 1)..].Trim();
            if (rawValue.Length > 0 && rawValue[0] is '\'' or '"' &&
                (rawValue.Length < 2 || rawValue[^1] != rawValue[0]))
                throw FileError($"{context} line {lineNumber}", "malformed or unsupported multiline dotenv value");
            var value = Unquote(line[(eq + 1)..].Trim());
            if (!string.IsNullOrEmpty(key))
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }

    private static void ApplyResourceLimits(RunContainerOptions options, MappingNode svc)
    {
        // Short form: cpus / mem_limit at the service level.
        var cpus = svc.Scalar("cpus");
        if (!string.IsNullOrWhiteSpace(cpus))
        {
            options.CpuLimit = cpus.Trim();
        }

        var mem = svc.Scalar("mem_limit");
        if (!string.IsNullOrWhiteSpace(mem))
        {
            options.MemoryLimit = mem.Trim();
        }

        // Long form: deploy.resources.limits.{cpus,memory}. Only fills gaps left by the short form.
        if (svc.Child("deploy") is MappingNode deploy &&
            deploy.Child("resources") is MappingNode resources)
        {
            if (resources.Child("limits") is MappingNode limits)
            {
                if (string.IsNullOrWhiteSpace(options.CpuLimit) && limits.Scalar("cpus") is { } c && !string.IsNullOrWhiteSpace(c))
                {
                    options.CpuLimit = c.Trim();
                }

                if (string.IsNullOrWhiteSpace(options.MemoryLimit) && limits.Scalar("memory") is { } m && !string.IsNullOrWhiteSpace(m))
                {
                    options.MemoryLimit = m.Trim();
                }
            }

            // deploy.resources.reservations.devices is Compose's standard way to request GPU
            // access. The engine only offers all-or-nothing passthrough, so any GPU request maps
            // to --gpus all; narrower selections are reported as warnings rather than silently
            // honoured as written.
            if (resources.Child("reservations") is MappingNode reservations &&
                reservations.Child("devices") is SequenceNode devices &&
                devices.Items.OfType<MappingNode>().Any(RequestsGpu))
            {
                options.AllGpus = true;
            }
        }
    }

    /// <summary>True when a Compose device reservation asks for the <c>gpu</c> capability.</summary>
    private static bool RequestsGpu(MappingNode device) =>
        device.Child("capabilities") is SequenceNode capabilities &&
        capabilities.Items.OfType<ScalarNode>()
            .Any(c => Unquote(c.Value ?? string.Empty).Trim().Equals("gpu", StringComparison.OrdinalIgnoreCase));

    private static RestartPolicyKind ParseRestart(string? value)
    {
        var v = Unquote(value ?? string.Empty).Trim().ToLowerInvariant();

        // on-failure may carry a retry count (on-failure:5); the count is honored via the health budget.
        if (v.StartsWith("on-failure", StringComparison.Ordinal))
        {
            return RestartPolicyKind.OnFailure;
        }

        return v switch
        {
            "always" => RestartPolicyKind.Always,
            "unless-stopped" => RestartPolicyKind.UnlessStopped,
            _ => RestartPolicyKind.No,
        };
    }

    private static List<ComposeDependency> ParseDependsOn(Node? node)
    {
        var deps = new List<ComposeDependency>();
        switch (node)
        {
            // Short form: depends_on: [db, redis]
            case SequenceNode seq:
                foreach (var item in seq.Items)
                {
                    if (item is ScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                    {
                        deps.Add(new ComposeDependency { ServiceName = s.Value.Trim() });
                    }
                }

                break;

            // Long form: depends_on: { db: { condition: service_healthy } }
            case MappingNode map:
                foreach (var (name, value) in map.Map)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (value is not MappingNode cfg)
                        throw new ComposeConfigurationException("Compose depends_on entries require a mapping.");
                    if (cfg.Map.Keys.Any(key => key is not ("condition" or "required" or "restart")))
                        throw new ComposeConfigurationException("Compose depends_on contains an unsupported option.");
                    var condition = cfg.Child("condition") switch
                    {
                        null => DependencyCondition.ServiceStarted,
                        ScalarNode { Value: "service_started" } => DependencyCondition.ServiceStarted,
                        ScalarNode { Value: "service_healthy" } => DependencyCondition.ServiceHealthy,
                        ScalarNode { Value: "service_completed_successfully" } => DependencyCondition.ServiceCompletedSuccessfully,
                        _ => throw new ComposeConfigurationException("Compose depends_on condition is unsupported."),
                    };
                    deps.Add(new ComposeDependency
                    {
                        ServiceName = name.Trim(), Condition = condition,
                        Required = ParseDependencyBoolean(cfg.Child("required"), true),
                        Restart = ParseDependencyBoolean(cfg.Child("restart"), false),
                    });
                }

                break;
        }

        return deps;
    }

    private static bool ParseDependencyBoolean(Node? value, bool fallback) => value switch
    {
        null => fallback,
        ScalarNode scalar when bool.TryParse(scalar.Value, out _) => bool.Parse(scalar.Value),
        _ => throw new ComposeConfigurationException("Compose depends_on required and restart must be boolean values."),
    };

    private static ComposeImagePolicy ParsePullPolicy(string? value) => value?.Trim() switch
    {
        null or "missing" or "if_not_present" => ComposeImagePolicy.Missing,
        "always" => ComposeImagePolicy.Always,
        "never" => ComposeImagePolicy.Never,
        "build" => ComposeImagePolicy.Build,
        _ => throw new ComposeConfigurationException("Compose pull_policy is unsupported. Use missing, always, never or build."),
    };

    /// <summary>
    /// Maps a compose <c>healthcheck</c> to the app's <see cref="HealthCheckConfig"/> probe. The
    /// restart budget is derived from the service's <c>restart</c> policy, since the desktop
    /// watchdog restarts unhealthy containers within a budget.
    /// </summary>
    private static HealthCheckConfig? ParseHealthCheck(Node? node, RestartPolicyKind restart, string? restartText)
    {
        if (node is not MappingNode map)
        {
            return null;
        }

        var test = map.Child("test") switch
        {
            ScalarNode s => new List<string> { "CMD-SHELL", s.Value },
            SequenceNode seq => seq.Items.OfType<ScalarNode>().Select(s => s.Value).ToList(),
            _ => new List<string>(),
        };
        var desired = new NativeHealthOptions
        {
            Test = test,
            Disabled = string.Equals(map.Scalar("disable"), "true", StringComparison.OrdinalIgnoreCase),
            Interval = map.Scalar("interval"),
            Timeout = map.Scalar("timeout"),
            StartPeriod = map.Scalar("start_period"),
            StartInterval = map.Scalar("start_interval"),
            Retries = map.Scalar("retries") is { } retries
                ? int.TryParse(retries, out var n) ? n : 0 : null,
        };

        return new HealthCheckConfig
        {
            Kind = HealthProbeKind.Command,
            Command = test.Count == 2 && test[0] == "CMD-SHELL" ? test[1] : string.Empty,
            DesiredHealth = desired,
            IntervalSeconds = ParseDurationSeconds(desired.Interval) ?? 30,
            MaxRestarts = RestartBudget(restart, restartText),
            Enabled = true,
        };
    }

    /// <summary>Translates a restart policy (and optional on-failure count) into a watchdog restart budget.</summary>
    private static int RestartBudget(RestartPolicyKind restart, string? restartText)
    {
        var retries = restartText?.Split(':', 2).ElementAtOrDefault(1);
        return restart switch
        {
            RestartPolicyKind.Always or RestartPolicyKind.UnlessStopped => HealthCheckConfig.MaxRestartLimit,
            RestartPolicyKind.OnFailure => int.TryParse((retries ?? string.Empty).Trim(), out var n) && n > 0
                ? Math.Min(n, HealthCheckConfig.MaxRestartLimit)
                : 3,
            _ => 0, // "no" restart policy => alert-only health check.
        };
    }

    private static string? JoinCommand(Node? node)
    {
        switch (node)
        {
            case ScalarNode s:
                return s.Value;

            case SequenceNode seq:
                // Exec (list) form: each element is a distinct argv token and must survive the
                // consumer's whitespace tokenizer intact. Quote any element containing whitespace so
                // e.g. ["sh","-c","while true; do ...; done"] is NOT collapsed into separate words
                // (which would leave sh with just "while" as its -c script and exit immediately).
                var tokens = seq.Items.OfType<ScalarNode>()
                    .Select(x => x.Value)
                    .Select(QuoteToken);
                return string.Join(' ', tokens);

            default:
                return null;
        }
    }

    /// <summary>
    /// Wraps a command argv token in quotes when it contains whitespace, so the whitespace-splitting
    /// tokenizer in <see cref="Models.RunContainerOptions"/> reconstructs it as a single argument.
    /// Picks a quote character not already present in the token when possible.
    /// </summary>
    private static string QuoteToken(string token)
    {
        if (token.Length == 0)
            return "\"\"";
        if (!token.Any(char.IsWhiteSpace) && !token.Contains('"') && !token.Contains('\''))
        {
            return token;
        }

        if (!token.Contains('"'))
        {
            return $"\"{token}\"";
        }

        if (!token.Contains('\''))
        {
            return $"'{token}'";
        }

        // Adjacent quoted segments are understood by RunContainerOptions.SplitCommand; unlike
        // backslash escaping they also preserve Windows paths verbatim.
        return "'" + token.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static List<string> CollectStrings(Node? node)
    {
        var items = new List<string>();
        switch (node)
        {
            case SequenceNode seq:
                foreach (var item in seq.Items)
                {
                    if (item is ScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                    {
                        items.Add(s.Value);
                    }
                }

                break;

            case ScalarNode single when !string.IsNullOrWhiteSpace(single.Value):
                items.Add(single.Value.Trim());
                break;
        }

        return items;
    }

    /// <summary>Reads a KEY=VALUE list or a KEY: VALUE mapping into raw <c>KEY=VALUE</c> strings.</summary>
    private static List<string> CollectKeyValues(Node? node)
    {
        var items = new List<string>();
        switch (node)
        {
            case SequenceNode seq:
                foreach (var item in seq.Items)
                {
                    if (item is ScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
                    {
                        items.Add(s.Value);
                    }
                }

                break;

            case MappingNode map:
                foreach (var (key, value) in map.Map)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    items.Add(value is NullNode ? key : $"{key}={(value as ScalarNode)?.Value ?? string.Empty}");
                }

                break;
        }

        return items;
    }

    private static Dictionary<string, string> CollectLabels(Node? node)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in CollectKeyValues(node))
        {
            var eq = raw.IndexOf('=');
            if (eq < 0)
            {
                labels[raw] = string.Empty;
            }
            else
            {
                labels[raw[..eq].Trim()] = raw[(eq + 1)..];
            }
        }

        return labels;
    }

    /// <summary>Parses a compose duration (e.g. <c>30s</c>, <c>1m30s</c>, <c>90</c>) into whole seconds.</summary>
    private static int? ParseDurationSeconds(string? value)
    {
        var v = Unquote(value ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (int.TryParse(v, out var bare))
        {
            return bare;
        }

        double totalSeconds = 0;
        var num = new StringBuilder();
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (char.IsDigit(c) || c == '.')
            {
                num.Append(c);
                continue;
            }

            if (num.Length == 0)
            {
                continue;
            }

            // Unit letters: h, m, s, and ms (handled by peeking).
            var unit = c;
            var isMillis = unit == 'm' && i + 1 < v.Length && v[i + 1] == 's';
            if (double.TryParse(num.ToString(), out var magnitude))
            {
                totalSeconds += unit switch
                {
                    'h' => magnitude * 3600,
                    'm' when !isMillis => magnitude * 60,
                    'm' when isMillis => magnitude / 1000.0,
                    's' => magnitude,
                    _ => 0,
                };
            }

            if (isMillis)
            {
                i++; // consume the trailing 's' of "ms".
            }

            num.Clear();
        }

        var seconds = (int)Math.Round(totalSeconds);
        return seconds <= 0 ? 1 : seconds;
    }

    private static string SanitizeProjectName(string? name)
    {
        var v = Unquote(name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(v))
        {
            return "compose";
        }

        // Keep names safe for use as a container-name prefix.
        var sb = new StringBuilder(v.Length);
        foreach (var c in v.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        return sb.ToString();
    }

    private static string Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
        {
            return v[1..^1];
        }

        return v;
    }
}
