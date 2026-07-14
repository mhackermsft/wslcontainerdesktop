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
/// see <see cref="ComposeProjectSupervisor"/>. A small indentation-aware YAML reader is used
/// rather than pulling in a YAML dependency, mirroring the manual parsing already used elsewhere
/// (see <see cref="K8sManifestSanitizer"/>).
///
/// <para>Supported per service: <c>image</c>, <c>container_name</c>, <c>command</c>,
/// <c>entrypoint</c>, <c>ports</c>, <c>environment</c>, <c>volumes</c>, <c>networks</c> /
/// <c>network_mode</c>, <c>user</c>, <c>working_dir</c>, <c>hostname</c>, <c>labels</c>,
/// <c>cpus</c> / <c>mem_limit</c> / <c>deploy.resources.limits</c>, <c>restart</c>,
/// <c>depends_on</c> (list and long/condition form) and <c>healthcheck</c>. Values support
/// <c>${VAR}</c> / <c>${VAR:-default}</c> interpolation. Unknown keys are ignored; malformed
/// content never throws.</para>
/// </summary>
public static class ComposeImporter
{
    private sealed record Line(int Indent, string Text);

    // Minimal YAML value tree produced by the reader.
    private abstract class Node;

    private sealed class ScalarNode(string value) : Node
    {
        public string Value { get; } = value;
    }

    private sealed class SequenceNode(List<Node> items) : Node
    {
        public List<Node> Items { get; } = items;
    }

    private sealed class MappingNode(Dictionary<string, Node> map) : Node
    {
        public Dictionary<string, Node> Map { get; } = map;

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
    /// no services when no <c>services:</c> block is present. Never throws for malformed content.
    /// </summary>
    /// <param name="yaml">The compose document text.</param>
    /// <param name="environment">
    /// Variables used for <c>${VAR}</c> interpolation; process environment variables and inline
    /// <c>:-</c> defaults are consulted as a fallback.
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
        var lines = Tokenize(yaml, effectiveEnv);
        var anchors = new Dictionary<string, Node>(StringComparer.Ordinal);
        var root = ParseMapping(lines, 0, lines.Count, 0, anchors);

        var project = new ComposeProject
        {
            Name = SanitizeProjectName(root.Scalar("name")),
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
            }
        }

        project.Networks = ParseTopLevelNetworks(root.Child("networks"));
        project.Volumes = ParseTopLevelVolumes(root.Child("volumes"));
        project.Secrets = ParseTopLevelSecrets(root.Child("secrets"), baseDirectory);
        project.Configs = ParseTopLevelSecrets(root.Child("configs"), baseDirectory);

        return project;
    }

    /// <summary>
    /// Merges an explicit interpolation environment with a sibling <c>.env</c> file (when a base
    /// directory is given). Explicitly-provided variables win over <c>.env</c> entries.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BuildInterpolationEnvironment(
        IReadOnlyDictionary<string, string>? environment,
        string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return environment;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in ReadEnvFile(Path.Combine(baseDirectory, ".env")))
        {
            merged[key] = value;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                merged[key] = value; // explicit values take precedence over the .env file
            }
        }

        return merged.Count == 0 ? environment : merged;
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
            Volumes = CollectVolumes(svc.Child("volumes")),
            EnvironmentVariables = CollectKeyValues(svc.Child("environment")),
        };

        options.Labels = CollectLabels(svc.Child("labels"));
        MergeEnvFiles(options, svc.Child("env_file"), baseDirectory);
        ApplyNetwork(options, svc);
        ApplyResourceLimits(options, svc);
        ApplyRuntimeOptions(options, svc);

        var build = ParseBuild(svc.Child("build"), baseDirectory);

        // A service needs either an image to run or a build section to produce one.
        if (string.IsNullOrWhiteSpace(options.Image) && build is null)
        {
            return null;
        }

        var service = new ComposeService
        {
            Name = serviceName,
            Options = options,
            Restart = ParseRestart(svc.Scalar("restart")),
            DependsOn = ParseDependsOn(svc.Child("depends_on")),
            Build = build,
            Secrets = ParseFileMounts(svc.Child("secrets"), "/run/secrets/"),
            Configs = ParseFileMounts(svc.Child("configs"), "/"),
        };

        service.Health = ParseHealthCheck(svc.Child("healthcheck"), service.Restart);
        return service;
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
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cfg = value as MappingNode;
            result.Add(new ComposeNetwork
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
            options.Network = NormalizeNetwork(mode);
            if (options.Network is not null)
            {
                options.Networks.Add(options.Network);
            }

            return;
        }

        // networks: may be a sequence (["frontend", "backend"]) or a mapping (frontend: {...}).
        // Collect them all in declared order; wslc attaches to the first at run time.
        var names = new List<string>();
        switch (svc.Child("networks"))
        {
            case SequenceNode seq:
                foreach (var item in seq.Items.OfType<ScalarNode>())
                {
                    var n = NormalizeNetwork(item.Value);
                    if (n is not null)
                    {
                        names.Add(n);
                    }
                }

                break;
            case MappingNode map:
                foreach (var key in map.Map.Keys)
                {
                    var n = NormalizeNetwork(key);
                    if (n is not null)
                    {
                        names.Add(n);
                    }
                }

                break;
        }

        options.Networks = names.Distinct(StringComparer.Ordinal).ToList();
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
                    if (!string.IsNullOrWhiteSpace(proto))
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
        var paths = CollectStrings(node);
        if (paths.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(
            options.EnvironmentVariables.Select(e =>
            {
                var eq = e.IndexOf('=');
                return eq < 0 ? e.Trim() : e[..eq].Trim();
            }),
            StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var resolved = ResolvePath(path, baseDirectory);
            foreach (var (key, value) in ReadEnvFile(resolved))
            {
                if (existing.Add(key))
                {
                    options.EnvironmentVariables.Add($"{key}={value}");
                }
            }
        }
    }

    /// <summary>Resolves a possibly-relative path against the compose file's directory when known.</summary>
    private static string ResolvePath(string path, string? baseDirectory)
    {
        var p = path.Trim();
        if (string.IsNullOrEmpty(p) || string.IsNullOrWhiteSpace(baseDirectory) || Path.IsPathRooted(p))
        {
            return p;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, p));
        }
        catch
        {
            return p;
        }
    }

    /// <summary>Reads a <c>.env</c>-style file into KEY/VALUE pairs. Returns empty when unreadable.</summary>
    private static IEnumerable<KeyValuePair<string, string>> ReadEnvFile(string path)
    {
        string[] lines;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                yield break;
            }

            lines = File.ReadAllLines(path);
        }
        catch
        {
            yield break;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
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
            deploy.Child("resources") is MappingNode resources &&
            resources.Child("limits") is MappingNode limits)
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
    }

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

                    var condition = DependencyCondition.ServiceStarted;
                    if (value is MappingNode cfg &&
                        string.Equals(cfg.Scalar("condition"), "service_healthy", StringComparison.OrdinalIgnoreCase))
                    {
                        condition = DependencyCondition.ServiceHealthy;
                    }

                    deps.Add(new ComposeDependency { ServiceName = name.Trim(), Condition = condition });
                }

                break;
        }

        return deps;
    }

    /// <summary>
    /// Maps a compose <c>healthcheck</c> to the app's <see cref="HealthCheckConfig"/> probe. The
    /// restart budget is derived from the service's <c>restart</c> policy, since the desktop
    /// watchdog restarts unhealthy containers within a budget.
    /// </summary>
    private static HealthCheckConfig? ParseHealthCheck(Node? node, RestartPolicyKind restart)
    {
        if (node is not MappingNode map)
        {
            return null;
        }

        if (string.Equals(map.Scalar("disable"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var command = ExtractHealthTest(map.Child("test"));
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var interval = ParseDurationSeconds(map.Scalar("interval")) ?? 30;

        return new HealthCheckConfig
        {
            Kind = HealthProbeKind.Command,
            Command = command,
            IntervalSeconds = interval,
            MaxRestarts = RestartBudget(restart, map.Scalar("retries")),
            Enabled = true,
        };
    }

    /// <summary>
    /// Reads a compose healthcheck <c>test</c>, which is either a shell string or a list whose first
    /// element is <c>CMD</c> (exec form) or <c>CMD-SHELL</c> (shell string). Returns a single shell
    /// command suitable for <c>wslc exec &lt;id&gt; sh -c</c>.
    /// </summary>
    private static string ExtractHealthTest(Node? node)
    {
        switch (node)
        {
            case ScalarNode s:
                return s.Value.Trim();

            case SequenceNode seq when seq.Items.Count > 0:
                var parts = seq.Items.OfType<ScalarNode>().Select(x => x.Value).ToList();
                if (parts.Count == 0)
                {
                    return string.Empty;
                }

                if (string.Equals(parts[0], "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                if (string.Equals(parts[0], "CMD-SHELL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parts[0], "CMD", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join(' ', parts.Skip(1)).Trim();
                }

                return string.Join(' ', parts).Trim();

            default:
                return string.Empty;
        }
    }

    /// <summary>Translates a restart policy (and optional on-failure count) into a watchdog restart budget.</summary>
    private static int RestartBudget(RestartPolicyKind restart, string? retries)
    {
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
                return string.IsNullOrWhiteSpace(s.Value) ? null : s.Value.Trim();

            case SequenceNode seq:
                var tokens = seq.Items.OfType<ScalarNode>().Select(x => x.Value).Where(x => !string.IsNullOrEmpty(x));
                var joined = string.Join(' ', tokens).Trim();
                return string.IsNullOrEmpty(joined) ? null : joined;

            default:
                return null;
        }
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
                        items.Add(s.Value.Trim());
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
                        items.Add(s.Value.Trim());
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

                    var v = (value as ScalarNode)?.Value ?? string.Empty;
                    items.Add(string.IsNullOrEmpty(v) ? key : $"{key}={v}");
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
                labels[raw[..eq].Trim()] = raw[(eq + 1)..].Trim();
            }
        }

        return labels;
    }

    /// <summary>Maps compose network conventions to a `wslc run --network` value (null = default bridge).</summary>
    private static string? NormalizeNetwork(string network)
    {
        var v = Unquote(network).Trim();
        if (string.IsNullOrWhiteSpace(v) ||
            string.Equals(v, "default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return v;
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

    // ----- YAML reader -------------------------------------------------------------------------

    private static MappingNode ParseMapping(List<Line> lines, int start, int end, int indent, Dictionary<string, Node> anchors)
    {
        var map = new Dictionary<string, Node>(StringComparer.Ordinal);
        var i = start;
        while (i < end)
        {
            var line = lines[i];
            if (line.Indent < indent || (line.Indent == indent && line.Text.StartsWith('-')))
            {
                break;
            }

            if (line.Indent > indent)
            {
                i++;
                continue;
            }

            var key = KeyOf(line.Text);
            var inline = ValueOf(line.Text);

            var blockEnd = i + 1;
            while (blockEnd < end)
            {
                var b = lines[blockEnd];
                var deeper = b.Indent > indent;
                var sameSeq = b.Indent == indent && b.Text.StartsWith('-');
                if (!deeper && !sameSeq)
                {
                    break;
                }

                blockEnd++;
            }

            // A leading &anchor on the value names the node for later *alias / << merge references.
            var anchorName = StripAnchor(ref inline);

            // Merge key: "<<: *base" (or a flow list of aliases) folds the referenced mapping(s)
            // into this mapping without overriding keys that are set explicitly.
            if (key == "<<")
            {
                foreach (var merged in ResolveMergeSources(inline, anchors))
                {
                    foreach (var (mk, mv) in merged.Map)
                    {
                        if (!map.ContainsKey(mk))
                        {
                            map[mk] = mv;
                        }
                    }
                }

                i = blockEnd;
                continue;
            }

            Node node;
            if (IsBlockScalar(inline, out var literal))
            {
                node = BuildBlockScalar(lines, i + 1, blockEnd, literal);
            }
            else if (!string.IsNullOrEmpty(inline))
            {
                node = ResolveAlias(inline, anchors) ?? ParseInlineValue(inline);
            }
            else if (blockEnd > i + 1)
            {
                var first = lines[i + 1];
                node = first.Text.StartsWith('-')
                    ? ParseSequence(lines, i + 1, blockEnd, first.Indent, anchors)
                    : ParseMapping(lines, i + 1, blockEnd, first.Indent, anchors);
            }
            else
            {
                node = new ScalarNode(string.Empty);
            }

            if (anchorName is not null)
            {
                anchors[anchorName] = node;
            }

            if (!string.IsNullOrEmpty(key))
            {
                map[key] = node;
            }

            i = blockEnd;
        }

        return new MappingNode(map);
    }

    private static SequenceNode ParseSequence(List<Line> lines, int start, int end, int indent, Dictionary<string, Node> anchors)
    {
        var items = new List<Node>();
        var i = start;
        while (i < end)
        {
            var line = lines[i];
            if (line.Indent != indent || !line.Text.StartsWith('-'))
            {
                i++;
                continue;
            }

            var afterDash = line.Text[1..].Trim();

            var itemEnd = i + 1;
            while (itemEnd < end && lines[itemEnd].Indent > indent)
            {
                itemEnd++;
            }

            var itemAnchor = StripAnchor(ref afterDash);

            Node? item = null;
            if (!string.IsNullOrEmpty(afterDash))
            {
                // "- key: value" starts a mapping item (e.g. long-form ports/volumes). Any deeper
                // continuation lines belong to that same mapping.
                if (!afterDash.StartsWith('[') && !afterDash.StartsWith('{') && FindKeySeparator(afterDash) >= 0)
                {
                    var sub = new List<Line> { new Line(indent + 2, afterDash) };
                    for (var k = i + 1; k < itemEnd; k++)
                    {
                        sub.Add(lines[k]);
                    }

                    item = ParseMapping(sub, 0, sub.Count, indent + 2, anchors);
                }
                else
                {
                    // Scalar / alias / inline flow list item ("80:80", "db", "*ref", "[a, b]").
                    item = ResolveAlias(afterDash, anchors) ?? ParseInlineValue(afterDash);
                }
            }
            else if (itemEnd > i + 1)
            {
                var first = lines[i + 1];
                item = first.Text.StartsWith('-')
                    ? ParseSequence(lines, i + 1, itemEnd, first.Indent, anchors)
                    : ParseMapping(lines, i + 1, itemEnd, first.Indent, anchors);
            }

            if (item is not null)
            {
                if (itemAnchor is not null)
                {
                    anchors[itemAnchor] = item;
                }

                items.Add(item);
            }

            i = itemEnd;
        }

        return new SequenceNode(items);
    }

    /// <summary>
    /// If <paramref name="value"/> begins with a YAML anchor (<c>&amp;name</c>), removes it and returns
    /// the anchor name; otherwise returns null and leaves the value unchanged.
    /// </summary>
    private static string? StripAnchor(ref string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '&')
        {
            return null;
        }

        var end = 1;
        while (end < value.Length && !char.IsWhiteSpace(value[end]))
        {
            end++;
        }

        var name = value[1..end];
        value = value[end..].Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>Resolves a <c>*alias</c> reference to its anchored node, or null if not an alias.</summary>
    private static Node? ResolveAlias(string value, Dictionary<string, Node> anchors)
    {
        var v = value.Trim();
        if (v.Length < 2 || v[0] != '*')
        {
            return null;
        }

        var name = v[1..].Trim();
        return anchors.TryGetValue(name, out var node) ? node : new ScalarNode(string.Empty);
    }

    /// <summary>Resolves the mapping source(s) referenced by a merge key value (<c>*base</c> or <c>[*a, *b]</c>).</summary>
    private static IEnumerable<MappingNode> ResolveMergeSources(string inline, Dictionary<string, Node> anchors)
    {
        var v = inline.Trim();
        if (v.StartsWith('[') && v.EndsWith(']'))
        {
            foreach (var part in SplitFlow(v[1..^1]))
            {
                if (ResolveAlias(part.Trim(), anchors) is MappingNode m)
                {
                    yield return m;
                }
            }
        }
        else if (ResolveAlias(v, anchors) is MappingNode single)
        {
            yield return single;
        }
    }

    /// <summary>True when a mapping value is a block scalar indicator (<c>|</c>, <c>&gt;</c> and chomping variants).</summary>
    private static bool IsBlockScalar(string inline, out bool literal)
    {
        literal = false;
        var v = inline.Trim();
        if (v.Length == 0 || (v[0] != '|' && v[0] != '>'))
        {
            return false;
        }

        // The remainder may only be chomping/indentation indicators (-, +, digits).
        for (var i = 1; i < v.Length; i++)
        {
            if (v[i] is not ('-' or '+') && !char.IsDigit(v[i]))
            {
                return false;
            }
        }

        literal = v[0] == '|';
        return true;
    }

    /// <summary>
    /// Builds a block scalar from the deeper lines that follow. Literal (<c>|</c>) blocks join with
    /// newlines; folded (<c>&gt;</c>) blocks join with spaces. Best-effort: blank lines are not preserved.
    /// </summary>
    private static ScalarNode BuildBlockScalar(List<Line> lines, int start, int end, bool literal)
    {
        var parts = new List<string>();
        for (var i = start; i < end; i++)
        {
            parts.Add(lines[i].Text);
        }

        return new ScalarNode(string.Join(literal ? "\n" : " ", parts).Trim());
    }

    /// <summary>Parses an inline value: a flow sequence (<c>[a, b]</c>) or a plain scalar.</summary>
    private static Node ParseInlineValue(string inline)
    {
        var trimmed = inline.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            var items = new List<Node>();
            foreach (var part in SplitFlow(trimmed[1..^1]))
            {
                var value = Unquote(part.Trim());
                if (!string.IsNullOrEmpty(value))
                {
                    items.Add(new ScalarNode(value));
                }
            }

            return new SequenceNode(items);
        }

        return new ScalarNode(Unquote(trimmed));
    }

    /// <summary>Splits a flow-list body on commas that are not inside quotes.</summary>
    private static IEnumerable<string> SplitFlow(string body)
    {
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        foreach (var c in body)
        {
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }

            if (c == ',' && !inSingle && !inDouble)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static List<Line> Tokenize(string yaml, IReadOnlyDictionary<string, string>? environment)
    {
        var result = new List<Line>();
        if (string.IsNullOrEmpty(yaml))
        {
            return result;
        }

        // Normalize every line-ending style to '\n' before splitting. In particular WinUI's TextBox
        // returns multi-line text with bare '\r' separators, which would otherwise collapse the whole
        // document into a single line and break parsing.
        foreach (var raw in yaml.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Split('\n'))
        {
            var withoutComment = StripComment(raw);
            if (string.IsNullOrWhiteSpace(withoutComment))
            {
                continue;
            }

            var indent = 0;
            while (indent < withoutComment.Length && withoutComment[indent] == ' ')
            {
                indent++;
            }

            var text = Interpolate(withoutComment.Trim(), environment);
            result.Add(new Line(indent, text));
        }

        return result;
    }

    /// <summary>
    /// Applies compose-style variable interpolation: <c>$VAR</c>, <c>${VAR}</c>, and
    /// <c>${VAR:-default}</c> / <c>${VAR-default}</c>. Values resolve from the supplied
    /// environment, then process environment variables, then the inline default, then empty.
    /// </summary>
    private static string Interpolate(string text, IReadOnlyDictionary<string, string>? environment)
    {
        if (text.IndexOf('$') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '$')
            {
                sb.Append(c);
                continue;
            }

            // "$$" is an escaped literal dollar sign.
            if (i + 1 < text.Length && text[i + 1] == '$')
            {
                sb.Append('$');
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                var close = text.IndexOf('}', i + 2);
                if (close > 0)
                {
                    var expr = text[(i + 2)..close];
                    sb.Append(ResolveVariable(expr, environment));
                    i = close;
                    continue;
                }
            }

            // Bare $NAME form.
            var j = i + 1;
            while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
            {
                j++;
            }

            if (j > i + 1)
            {
                sb.Append(ResolveVariable(text[(i + 1)..j], environment));
                i = j - 1;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string ResolveVariable(string expr, IReadOnlyDictionary<string, string>? environment)
    {
        string name = expr;
        string? fallback = null;

        // Support ${VAR:-default} and ${VAR-default}.
        var sep = expr.IndexOf(":-", StringComparison.Ordinal);
        if (sep >= 0)
        {
            name = expr[..sep];
            fallback = expr[(sep + 2)..];
        }
        else if ((sep = expr.IndexOf('-')) > 0)
        {
            name = expr[..sep];
            fallback = expr[(sep + 1)..];
        }

        name = name.Trim();

        if (environment is not null && environment.TryGetValue(name, out var fromEnv) && !string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        var fromProcess = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromProcess))
        {
            return fromProcess;
        }

        return fallback ?? string.Empty;
    }

    /// <summary>Removes a trailing <c>#</c> comment that is not inside quotes.</summary>
    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || line[i - 1] == ' '))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string KeyOf(string text)
    {
        var colon = FindKeySeparator(text);
        var key = colon < 0 ? text : text[..colon];
        return Unquote(key.Trim());
    }

    private static string ValueOf(string text)
    {
        var colon = FindKeySeparator(text);
        return colon < 0 || colon == text.Length - 1 ? string.Empty : text[(colon + 1)..].Trim();
    }

    /// <summary>
    /// Finds the ':' that separates a mapping key from its value, ignoring colons inside quotes and
    /// requiring the ':' to be followed by whitespace or end-of-line (so "80:80" is not split).
    /// </summary>
    private static int FindKeySeparator(string text)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == ':' && !inSingle && !inDouble && (i == text.Length - 1 || text[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
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
