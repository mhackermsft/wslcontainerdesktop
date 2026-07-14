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
    public static IReadOnlyList<RunProfile> Parse(string yaml)
    {
        var project = ParseProject(yaml);
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
    /// Variables used for <c>${VAR}</c> interpolation (e.g. loaded from a <c>.env</c> file); process
    /// environment variables and inline <c>:-</c> defaults are consulted as a fallback.
    /// </param>
    public static ComposeProject ParseProject(string yaml, IReadOnlyDictionary<string, string>? environment = null)
    {
        var lines = Tokenize(yaml, environment);
        var root = ParseMapping(lines, 0, lines.Count, 0);

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

            var service = BuildService(name, svc);
            if (service is not null)
            {
                project.Services.Add(service);
            }
        }

        return project;
    }

    private static ComposeService? BuildService(string serviceName, MappingNode svc)
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
            PortMappings = CollectStrings(svc.Child("ports")),
            Volumes = CollectStrings(svc.Child("volumes")),
            EnvironmentVariables = CollectKeyValues(svc.Child("environment")),
        };

        options.Labels = CollectLabels(svc.Child("labels"));
        ApplyNetwork(options, svc);
        ApplyResourceLimits(options, svc);

        if (string.IsNullOrWhiteSpace(options.Image))
        {
            return null;
        }

        var service = new ComposeService
        {
            Name = serviceName,
            Options = options,
            Restart = ParseRestart(svc.Scalar("restart")),
            DependsOn = ParseDependsOn(svc.Child("depends_on")),
        };

        service.Health = ParseHealthCheck(svc.Child("healthcheck"), service.Restart);
        return service;
    }

    private static void ApplyNetwork(RunContainerOptions options, MappingNode svc)
    {
        var mode = svc.Scalar("network_mode");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            options.Network = NormalizeNetwork(mode);
            return;
        }

        // networks: may be a sequence (["frontend"]) or a mapping (frontend: {...}); take the first.
        switch (svc.Child("networks"))
        {
            case SequenceNode seq when seq.Items.Count > 0 && seq.Items[0] is ScalarNode s:
                options.Network = NormalizeNetwork(s.Value);
                break;
            case MappingNode map when map.Map.Count > 0:
                options.Network = NormalizeNetwork(map.Map.Keys.First());
                break;
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

    private static MappingNode ParseMapping(List<Line> lines, int start, int end, int indent)
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

            Node node;
            if (!string.IsNullOrEmpty(inline))
            {
                node = ParseInlineValue(inline);
            }
            else if (blockEnd > i + 1)
            {
                var first = lines[i + 1];
                node = first.Text.StartsWith('-')
                    ? ParseSequence(lines, i + 1, blockEnd, first.Indent)
                    : ParseMapping(lines, i + 1, blockEnd, first.Indent);
            }
            else
            {
                node = new ScalarNode(string.Empty);
            }

            if (!string.IsNullOrEmpty(key))
            {
                map[key] = node;
            }

            i = blockEnd;
        }

        return new MappingNode(map);
    }

    private static SequenceNode ParseSequence(List<Line> lines, int start, int end, int indent)
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

            if (!string.IsNullOrEmpty(afterDash))
            {
                // For the compose fields we consume, sequence items are scalars ("80:80",
                // "KEY=VALUE", "db") or inline flow lists.
                items.Add(ParseInlineValue(afterDash));
            }
            else if (itemEnd > i + 1)
            {
                var first = lines[i + 1];
                items.Add(first.Text.StartsWith('-')
                    ? ParseSequence(lines, i + 1, itemEnd, first.Indent)
                    : ParseMapping(lines, i + 1, itemEnd, first.Indent));
            }

            i = itemEnd;
        }

        return new SequenceNode(items);
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
