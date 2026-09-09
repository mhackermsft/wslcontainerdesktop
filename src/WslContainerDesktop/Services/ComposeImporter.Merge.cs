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

namespace WslContainerDesktop.Services;

public static partial class ComposeImporter
{
    private static MappingNode MergeMappings(MappingNode basis, MappingNode overlay, string context = "")
    {
        var result = new Dictionary<string, Node>(basis.Map, StringComparer.Ordinal);
        foreach (var (key, value) in overlay.Map)
        {
            var childContext = context switch
            {
                "" when key == "services" => "services",
                "services" => "service",
                "service" => $"service.{key}",
                _ => $"{context}.{key}",
            };
            result[key] = result.TryGetValue(key, out var previous)
                ? MergeValue(previous, value, childContext) : value;
        }
        return new MappingNode(result)
        {
            Source = overlay.Source, Tag = basis.Tag,
            DefaultBuildContext = overlay.Child("context")?.Tag == "!reset"
                ? overlay.DefaultBuildContext : basis.DefaultBuildContext ?? overlay.DefaultBuildContext,
        };
    }

    private static Node MergeValue(Node basis, Node overlay, string context)
    {
        if (overlay.Tag is "!reset" or "!override") return overlay;
        if (context is "service.command" or "service.entrypoint" or "service.healthcheck.test" ||
            context.StartsWith("service.environment.", StringComparison.Ordinal) ||
            context.StartsWith("service.labels.", StringComparison.Ordinal) ||
            context.StartsWith("service.build.args.", StringComparison.Ordinal) ||
            context.StartsWith("service.build.labels.", StringComparison.Ordinal))
            return overlay;
        // Null is an absent override except for shell command fields (above).
        if (overlay is NullNode) return basis;
        if (basis.Tag == "!reset") return overlay;
        if (basis is MappingNode bm && overlay is MappingNode om)
            return MergeMappings(bm, om, context);
        if (basis is SequenceNode bs && overlay is SequenceNode os)
        {
            if (context == "service.extra_hosts")
                return new SequenceNode(bs.Items.Concat(os.Items)
                    .DistinctBy(n => ((ScalarNode)n).Value, StringComparer.Ordinal).ToList())
                    { Tag = basis.Tag, Source = overlay.Source };
            if (context is "service.ports" or "service.volumes" or "service.secrets" or "service.configs")
            {
                var items = new List<Node>(bs.Items);
                var indices = items.Select((node, index) => (Key: ResourceKey(node, context), Index: index))
                    .ToDictionary(p => p.Key, p => p.Index, StringComparer.Ordinal);
                foreach (var value in os.Items)
                {
                    var key = ResourceKey(value, context);
                    if (!indices.TryGetValue(key, out var index))
                    {
                        indices[key] = items.Count;
                        items.Add(value);
                    }
                    else items[index] = MergeValue(items[index], value, context + ".item");
                }
                return new SequenceNode(items) { Tag = basis.Tag, Source = overlay.Source };
            }
            return new SequenceNode([.. bs.Items, .. os.Items]) { Tag = basis.Tag, Source = overlay.Source };
        }
        return overlay;
    }

    private static Node ApplyTags(Node node)
    {
        if (node.Tag == "!reset") return new NullNode();
        Node result = node switch
        {
            MappingNode map => new MappingNode(map.Map.Where(p => p.Value.Tag != "!reset")
                .ToDictionary(p => p.Key, p => ApplyTags(p.Value), StringComparer.Ordinal))
                { DefaultBuildContext = map.DefaultBuildContext },
            SequenceNode seq => new SequenceNode(seq.Items.Select(ApplyTags).ToList()),
            ScalarNode scalar => new ScalarNode(scalar.Value),
            _ => new NullNode(),
        };
        result.Source = node.Source;
        return result;
    }

    private static MappingNode ExtendService(MappingNode basis, MappingNode child)
    {
        if (child.Child("healthcheck") is MappingNode health && health.Tag != "!reset" &&
            IsTrue(((MappingNode)ApplyTags(health)).Child("disable")) &&
            (basis.Child("healthcheck") is not MappingNode inherited || inherited.Tag == "!reset" ||
                !IsTrue(((MappingNode)ApplyTags(inherited)).Child("disable"))))
            throw FileError(child.Source + " extends.healthcheck.disable",
                "cannot disable an inherited healthcheck unless the base also disables it");
        return (MappingNode)ExtendValue(basis, child, "");
    }

    private static bool IsTrue(Node? node) =>
        node is ScalarNode scalar && bool.TryParse(scalar.Value, out var value) && value;

    private static Node ExtendValue(Node basis, Node child, string field)
    {
        if (child.Tag is "!reset" or "!override") return child;
        if (basis.Tag == "!reset") return child;
        if (basis is MappingNode bm && child is MappingNode cm &&
            field is "" or "build" or "deploy" or "deploy.resources" or "deploy.placement" or
                "deploy.reservations" or "logging" or "blkio_config")
        {
            var map = new Dictionary<string, Node>(bm.Map, StringComparer.Ordinal);
            foreach (var (key, value) in cm.Map)
                map[key] = map.TryGetValue(key, out var previous)
                    ? ExtendValue(previous, value, field.Length == 0 ? key : field + "." + key) : value;
            return new MappingNode(map)
            {
                Source = child.Source,
                DefaultBuildContext = cm.Child("context")?.Tag == "!reset"
                    ? cm.DefaultBuildContext : bm.DefaultBuildContext ?? cm.DefaultBuildContext,
            };
        }
        if (basis is MappingNode baseMap && child is MappingNode childMap &&
            field is "annotations" or "build.args" or "build.labels" or "build.extra_hosts" or
                "deploy.labels" or "deploy.update_config" or "deploy.rollback_config" or
                "deploy.restart_policy" or "deploy.resources.limits" or "environment" or "healthcheck" or
                "labels" or "logging.options" or "sysctls" or "storage_opt" or "ulimits")
        {
            var map = new Dictionary<string, Node>(baseMap.Map, StringComparer.Ordinal);
            foreach (var pair in childMap.Map) map[pair.Key] = pair.Value;
            return new MappingNode(map) { Source = child.Source };
        }
        if (basis is SequenceNode bs && child is SequenceNode cs)
        {
            if (field is "volumes" or "devices" or "blkio_config.device_read_bps" or
                "blkio_config.device_read_iops" or "blkio_config.device_write_bps" or "blkio_config.device_write_iops")
            {
                var items = new List<Node>(bs.Items);
                foreach (var item in cs.Items)
                {
                    var index = items.FindIndex(n => ExtendsTarget(n, field) == ExtendsTarget(item, field));
                    if (index < 0) items.Add(item);
                    else items[index] = item;
                }
                return new SequenceNode(items) { Source = child.Source };
            }
            if (field is "dns" or "dns_search" or "env_file" or "tmpfs")
                return new SequenceNode([.. bs.Items, .. cs.Items]) { Source = child.Source };
            if (field is "cap_add" or "cap_drop" or "configs" or "deploy.placement.constraints" or
                "deploy.placement.preferences" or "deploy.reservations.generic_resources" or "device_cgroup_rules" or
                "expose" or "external_links" or "ports" or "secrets" or "security_opt")
            {
                return new SequenceNode(bs.Items.Concat(cs.Items)
                    .DistinctBy(n => ExtendsSequenceIdentity(n, field), NodeEqualityComparer.Instance).ToList())
                    { Source = child.Source };
            }
            if (field == "extra_hosts")
            {
                var items = new List<Node>(bs.Items);
                var hosts = cs.Items.Cast<ScalarNode>().Select(n => n.Value.Split(':', 2)[0])
                    .ToHashSet(StringComparer.Ordinal);
                items.RemoveAll(n => hosts.Contains(((ScalarNode)n).Value.Split(':', 2)[0]));
                items.AddRange(cs.Items);
                return new SequenceNode(items) { Source = child.Source };
            }
        }
        return child;
    }

    private static Node ExtendsSequenceIdentity(Node node, string field)
    {
        if (node is not MappingNode map || field is not ("ports" or "secrets" or "configs")) return node;
        var canonical = new Dictionary<string, Node>(map.Map, StringComparer.Ordinal);
        if (field == "ports")
            canonical["host_ip"] = new ScalarNode(map.Scalar("host_ip") ?? "0.0.0.0");
        else
            canonical["target"] = new ScalarNode(ResourceKey(node, "service." + field));
        return new MappingNode(canonical);
    }

    private static string ExtendsTarget(Node node, string field) => field switch
    {
        "volumes" => ResourceKey(node, "service.volumes"),
        "devices" => node is ScalarNode scalar
            ? scalar.Value.Split(':').ElementAtOrDefault(1) ?? scalar.Value
            : RequiredScalar((node as MappingNode)?.Child("target"), "extends devices.target"),
        _ => RequiredScalar((node as MappingNode)?.Child("path"), "extends " + field + ".path"),
    };

    private static bool EqualNodes(Node left, Node right) => (left, right) switch
    {
        (NullNode, NullNode) => true,
        (ScalarNode a, ScalarNode b) => a.Value == b.Value,
        (SequenceNode a, SequenceNode b) => a.Items.Count == b.Items.Count &&
            a.Items.Zip(b.Items).All(p => EqualNodes(p.First, p.Second)),
        (MappingNode a, MappingNode b) => a.Map.Count == b.Map.Count &&
            a.Map.All(p => b.Map.TryGetValue(p.Key, out var value) && EqualNodes(p.Value, value)),
        _ => false,
    };

    private sealed class NodeEqualityComparer : IEqualityComparer<Node>
    {
        public static NodeEqualityComparer Instance { get; } = new();

        public bool Equals(Node? left, Node? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && EqualNodes(left, right);

        public int GetHashCode(Node node)
        {
            var hash = new HashCode();
            hash.Add(node.GetType());
            switch (node)
            {
                case ScalarNode scalar:
                    hash.Add(scalar.Value, StringComparer.Ordinal);
                    break;
                case SequenceNode sequence:
                    foreach (var child in sequence.Items) hash.Add(GetHashCode(child));
                    break;
                case MappingNode mapping:
                    foreach (var (key, value) in mapping.Map.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        hash.Add(key, StringComparer.Ordinal);
                        hash.Add(GetHashCode(value));
                    }
                    break;
            }
            return hash.ToHashCode();
        }
    }

    private static MappingNode NormalizeRoot(MappingNode root)
    {
        var resourceBudget = 100_000;
        if (root.Child("services") is MappingNode services)
        {
            foreach (var key in services.Map.Keys.ToList())
                if (services.Map[key] is MappingNode service)
                    services.Map[key] = NormalizeService(service, ref resourceBudget);
        }
        foreach (var kind in new[] { "networks", "volumes" })
        {
            if (root.Child(kind) is not MappingNode resources) continue;
            foreach (var resource in resources.Map.Values.OfType<MappingNode>())
                if (resource.Child("labels") is SequenceNode labels)
                    resource.Map["labels"] = NormalizePairs(labels, "labels");
        }
        return root;
    }

    private static MappingNode NormalizeService(MappingNode service, ref int resourceBudget)
    {
        foreach (var key in service.Map.Keys.ToList())
        {
            var value = service.Map[key];
            Node normalized = value;
            if (key is "environment" or "labels" && value is SequenceNode pairs)
                normalized = NormalizePairs(pairs, key);
            else if (key == "extra_hosts" && value is not NullNode)
                normalized = NormalizeExtraHosts(value);
            else if (key is "networks" or "depends_on" && value is SequenceNode names)
            {
                var map = new Dictionary<string, Node>(StringComparer.Ordinal);
                foreach (var item in names.Items)
                {
                    if (item is not ScalarNode scalar) throw ShapeError(key, "a list of names or a mapping");
                    map[scalar.Value] = key == "depends_on"
                        ? Fields(("condition", "service_started")) : new MappingNode(new(StringComparer.Ordinal));
                }
                normalized = new MappingNode(map);
            }
            else if (key == "build" && value is ScalarNode build)
                normalized = Fields(("context", build.Value));
            else if (key is "dns" or "dns_search" or "dns_opt" or "tmpfs" or "env_file" && value is ScalarNode)
                normalized = new SequenceNode([value]);
            else if (key is "ports" or "volumes" or "secrets" or "configs" && value is SequenceNode resources)
                normalized = NormalizeResources(resources, key, ref resourceBudget);
            if (key == "build" && normalized is MappingNode buildOptions)
            {
                foreach (var field in new[] { "args", "labels" })
                {
                    if (buildOptions.Child(field) is not SequenceNode args) continue;
                    buildOptions.Map[field] = NormalizePairs(args, field);
                }
            }
            normalized.Tag = value.Tag;
            service.Map[key] = normalized;
        }
        return service;
    }

    private static MappingNode NormalizePairs(SequenceNode pairs, string key)
    {
        var map = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var pair in pairs.Items)
        {
            if (pair is not ScalarNode scalar) throw ShapeError(key, "a list of strings or a mapping");
            var separator = scalar.Value.IndexOf('=');
            var name = separator < 0 ? scalar.Value : scalar.Value[..separator];
            if (string.IsNullOrWhiteSpace(name)) throw ShapeError(key, "nonempty keys");
            map[name] = separator < 0 ? new NullNode() : new ScalarNode(scalar.Value[(separator + 1)..]);
        }
        return new MappingNode(map) { Tag = pairs.Tag };
    }

    private static SequenceNode NormalizeExtraHosts(Node value)
    {
        var entries = new List<string>();
        if (value is SequenceNode list)
        {
            foreach (var item in list.Items)
            {
                if (item is not ScalarNode scalar)
                    throw ShapeError("extra_hosts", "hostname/address strings");
                entries.Add(scalar.Value);
            }
        }
        else if (value is MappingNode map)
        {
            foreach (var (host, addresses) in map.Map)
            {
                var items = addresses is SequenceNode sequence ? sequence.Items : [addresses];
                foreach (var address in items)
                {
                    if (address is not ScalarNode scalar)
                        throw ShapeError("extra_hosts", "an address or list of addresses for each hostname");
                    entries.Add($"{host}={scalar.Value}");
                }
            }
        }
        else throw ShapeError("extra_hosts", "a list or mapping of hostname/address pairs");

        var normalized = new List<Node>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var pair = NormalizeHostEntry(entry)
                ?? throw ShapeError("extra_hosts", "nonempty hostname/address pairs separated by = or :");
            if (seen.Add(pair)) normalized.Add(new ScalarNode(pair));
        }
        return new SequenceNode(normalized) { Tag = value.Tag };
    }

    private static SequenceNode NormalizeResources(SequenceNode resources, string key, ref int budget)
    {
        var items = new List<Node>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in resources.Items)
        {
            var normalized = NormalizeResource(raw, key);
            var expanded = key == "ports" ? ExpandPort((MappingNode)normalized) : [normalized];
            foreach (var item in expanded)
            {
                if (--budget < 0)
                    throw ShapeError(key, "at most 100,000 expanded resources per document");
                var identity = ResourceKey(item, "service." + key);
                if (!indices.TryGetValue(identity, out var index))
                {
                    if (items.Count >= 100_000)
                        throw ShapeError(key, "at most 100,000 normalized resources");
                    indices[identity] = items.Count;
                    items.Add(item);
                }
                else items[index] = MergeValue(items[index], item, "service." + key + ".item");
            }
        }
        return new SequenceNode(items);
    }

    private static List<Node> ExpandPort(MappingNode port)
    {
        var target = port.Scalar("target") ?? throw ShapeError("ports", "a target port");
        var protocol = port.Scalar("protocol") ?? "tcp";
        if (protocol is not ("tcp" or "udp" or "sctp"))
            throw ShapeError("ports.protocol", "tcp, udp or sctp");
        if (port.Scalar("host_ip") is { } ip)
            port.Map["host_ip"] = new ScalarNode(ip.Trim('[', ']'));
        var targets = PortRange(target);
        var published = port.Scalar("published");
        var publications = !string.IsNullOrEmpty(published) ? PortRange(published, allowZero: true) : null;
        if (publications is null) port.Map.Remove("published");
        else port.Map["published"] = new ScalarNode(publications.Count == 1 ? publications[0].ToString() : $"{publications[0]}-{publications[^1]}");
        if (targets.Count == 1)
        {
            port.Map["target"] = new ScalarNode(targets[0].ToString());
            return [port];
        }
        if (publications is not null && publications.Count != targets.Count)
            throw ShapeError("ports", "equally sized published and target ranges");
        var result = new List<Node>();
        for (var i = 0; i < targets.Count; i++)
        {
            var fields = new Dictionary<string, Node>(port.Map, StringComparer.Ordinal)
            {
                ["target"] = new ScalarNode(targets[i].ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            if (publications is not null)
                fields["published"] = new ScalarNode(publications[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.Add(new MappingNode(fields));
        }
        return result;
    }

    private static List<int> PortRange(string range, bool allowZero = false)
    {
        var values = range.Split('-', 2);
        if (!int.TryParse(values[0], out var start) || start < (allowZero ? 0 : 1) || start > 65535 ||
            !int.TryParse(values[^1], out var end) || end < start || end > 65535)
            throw ShapeError("ports", "numeric ports or ascending ranges between 1 and 65535");
        if (end - start > 10_000)
            throw ShapeError("ports", "ranges of at most 10,001 ports");
        return Enumerable.Range(start, end - start + 1).ToList();
    }

    private static MappingNode Fields(params (string Key, string? Value)[] fields) =>
        new(fields.Where(p => p.Value is not null).ToDictionary(p => p.Key,
            p => (Node)new ScalarNode(p.Value!), StringComparer.Ordinal));

    private static Node NormalizeResource(Node item, string key)
    {
        if (item is MappingNode map)
        {
            if (key == "volumes" && map.Scalar("type") is { } type && type is not ("bind" or "volume"))
                throw ShapeError("volumes.type", "bind or volume (other long mount types are unsupported)");
            if (key is "secrets" or "configs" && map.Child("target") is null && map.Scalar("source") is { } source)
                map.Map["target"] = new ScalarNode(source);
            if (key == "ports" && map.Child("protocol") is null)
                map.Map["protocol"] = new ScalarNode("tcp");
            return map;
        }
        if (item is not ScalarNode scalar) throw ShapeError(key, "resource strings or mappings");
        MappingNode result;
        if (key == "volumes")
        {
            var (source, target, mode) = SplitVolumeSpec(scalar.Value);
            result = Fields(("source", source), ("target", target),
                ("read_only", mode?.Split(',').Contains("ro") == true ? "true" : "false"));
            // Preserve legacy short-form mode hints in the projected mount representation.
            if (mode is not null && mode.Split(',').Any(m => m is not ("ro" or "rw" or "cached" or "delegated" or "consistent")))
                throw ShapeError(key, "supported volume modes (ro/rw or consistency hints)");
        }
        else if (key is "secrets" or "configs")
            result = Fields(("source", scalar.Value), ("target", scalar.Value));
        else
        {
            var parts = scalar.Value.Split('/', 2);
            var address = parts[0];
            var last = address.LastIndexOf(':');
            var target = last < 0 ? address : address[(last + 1)..];
            var prefix = last < 0 ? "" : address[..last];
            var publishedColon = prefix.LastIndexOf(':');
            result = Fields(("target", target),
                ("published", prefix.Length == 0 ? null : publishedColon < 0 ? prefix : prefix[(publishedColon + 1)..]),
                ("host_ip", publishedColon < 0 ? null : prefix[..publishedColon]),
                ("protocol", parts.Length == 2 ? parts[1] : "tcp"));
        }
        result.Tag = item.Tag;
        return result;
    }

    private static string ResourceKey(Node node, string context)
    {
        if (node is not MappingNode map) throw ShapeError(context, "normalized resource mappings");
        if (context == "service.ports")
            return string.Join('\0', map.Scalar("host_ip") ?? "0.0.0.0", map.Scalar("target") ?? "",
                map.Scalar("published") ?? "", map.Scalar("protocol") ?? "tcp");
        var target = map.Scalar("target") ?? throw ShapeError(context, "a target");
        if (context is "service.secrets" or "service.configs" && !target.StartsWith('/'))
            return (context == "service.secrets" ? "/run/secrets/" : "/") + target;
        return target;
    }

    private static ComposeConfigurationException ShapeError(string field, string expected) =>
        new($"Compose field '{field}' requires {expected}. Check the field's YAML shape.");

    private static void ValidateServices(MappingNode root)
    {
        if (root.Child("services") is null or NullNode) return;
        if (root.Child("services") is not MappingNode services)
            throw ShapeError("services", "a mapping of service names to definitions");
        foreach (var (name, node) in services.Map)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw ShapeError("services", "nonempty service names");
            if (node is not MappingNode service)
                throw ShapeError("services", "mapping service definitions");
            foreach (var (key, value) in service.Map)
            {
                if (value is NullNode) continue;
                var valid = key switch
                {
                    "environment" or "labels" => value is MappingNode values &&
                        values.Map.Values.All(v => v is ScalarNode or NullNode),
                    "networks" or "depends_on" or "healthcheck" or "build" or "deploy" or "ulimits" => value is MappingNode,
                    "ports" or "volumes" or "secrets" or "configs" => value is SequenceNode resources &&
                        resources.Items.All(v => v is MappingNode m && !string.IsNullOrWhiteSpace(m.Scalar("target")) &&
                            (key is not ("secrets" or "configs") || !string.IsNullOrWhiteSpace(m.Scalar("source")))),
                    "dns" or "dns_search" or "dns_opt" or "tmpfs" or "profiles" or "extra_hosts" => value is SequenceNode strings &&
                        strings.Items.All(v => v is ScalarNode),
                    "env_file" => value is SequenceNode files && files.Items.All(v => v is ScalarNode or MappingNode),
                    "command" or "entrypoint" => value is ScalarNode || value is SequenceNode args && args.Items.All(v => v is ScalarNode),
                    _ => !SupportedServiceKeys.Contains(key) || key == "extends" || value is ScalarNode,
                };
                if (!valid) throw ShapeError(key, "the documented Compose value type");
            }
            if (service.Child("healthcheck") is MappingNode health && health.Child("test") is { } test &&
                test is not NullNode && test is not ScalarNode &&
                !(test is SequenceNode testArgs && testArgs.Items.All(v => v is ScalarNode)))
                throw ShapeError("healthcheck.test", "a string or list of strings");
        }
    }
}
