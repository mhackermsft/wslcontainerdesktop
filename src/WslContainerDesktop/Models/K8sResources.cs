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

namespace WslContainerDesktop.Models;

/// <summary>Simplified Kubernetes resource rows parsed from `kubectl get ... -o json`.</summary>
public sealed class K8sNode
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = "-";
    public string Roles { get; init; } = "-";
    public string Version { get; init; } = "-";
    public bool IsReady => string.Equals(Status, "Ready", StringComparison.OrdinalIgnoreCase);
}

public sealed class K8sPod
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Status { get; init; } = "-";
    public string Ready { get; init; } = "-";
    public int Restarts { get; init; }
    public string Node { get; init; } = "-";
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);
}

public sealed class K8sDeployment
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Ready { get; init; } = "-";
    public int Desired { get; init; }
    public int Available { get; init; }
    public bool IsHealthy => Desired > 0 && Available >= Desired;
}

public sealed class K8sService
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Type { get; init; } = "-";
    public string ClusterIP { get; init; } = "-";
    public string Ports { get; init; } = "-";
}

public sealed class K8sIngress
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Class { get; init; } = "-";
    public string Hosts { get; init; } = "-";
}

public sealed class K8sPvc
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Status { get; init; } = "-";
    public string Capacity { get; init; } = "-";
    public string StorageClass { get; init; } = "-";
    public bool IsBound => string.Equals(Status, "Bound", StringComparison.OrdinalIgnoreCase);
}

public sealed class K8sConfigMap
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public int Keys { get; init; }
}

public sealed class K8sSecret
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Type { get; init; } = "-";
    public int Keys { get; init; }
}

public sealed class K8sJob
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Completions { get; init; } = "-";
    public bool Complete { get; init; }
}

public sealed class K8sCronJob
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "-";
    public string Schedule { get; init; } = "-";
    public bool Suspended { get; init; }
    public int Active { get; init; }
}

/// <summary>
/// Identifies a single Kubernetes object plus the capabilities the UI should expose
/// for it. Passed to the detail page and used to build kubectl actions.
/// </summary>
public sealed record K8sResourceRef(
    string Kind,
    string DisplayKind,
    string Namespace,
    string Name,
    bool ClusterScoped,
    bool SupportsLogs,
    bool SupportsScale,
    bool SupportsCron);

/// <summary>Maps a parsed resource row to a <see cref="K8sResourceRef"/>.</summary>
public static class K8sRef
{
    public static K8sResourceRef For(object model) => model switch
    {
        K8sNode n => new("node", "Node", string.Empty, n.Name, true, false, false, false),
        K8sPod p => new("pod", "Pod", p.Namespace, p.Name, false, true, false, false),
        K8sDeployment d => new("deployment", "Deployment", d.Namespace, d.Name, false, false, true, false),
        K8sService s => new("service", "Service", s.Namespace, s.Name, false, false, false, false),
        K8sIngress i => new("ingress", "Ingress", i.Namespace, i.Name, false, false, false, false),
        K8sPvc v => new("pvc", "PersistentVolumeClaim", v.Namespace, v.Name, false, false, false, false),
        K8sConfigMap c => new("configmap", "ConfigMap", c.Namespace, c.Name, false, false, false, false),
        K8sSecret se => new("secret", "Secret", se.Namespace, se.Name, false, false, false, false),
        K8sJob j => new("job", "Job", j.Namespace, j.Name, false, true, false, false),
        K8sCronJob cj => new("cronjob", "CronJob", cj.Namespace, cj.Name, false, false, false, true),
        _ => throw new ArgumentException($"Unsupported resource model: {model.GetType().Name}"),
    };
}

/// <summary>Parsers turning `kubectl get -o json` list output into the simplified rows above.</summary>
public static class K8sParser
{
    private static JsonElement.ArrayEnumerator? Items(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                return items.EnumerateArray();
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private static string Str(JsonElement el, params string[] path)
    {
        var cur = el;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur))
            {
                return string.Empty;
            }
        }

        return cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? string.Empty : cur.ToString();
    }

    public static List<K8sNode> Nodes(string json)
    {
        var result = new List<K8sNode>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var name = Str(item, "metadata", "name");
            var version = Str(item, "status", "nodeInfo", "kubeletVersion");

            var status = "NotReady";
            if (item.TryGetProperty("status", out var st) &&
                st.TryGetProperty("conditions", out var conds) &&
                conds.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in conds.EnumerateArray())
                {
                    if (Str(c, "type") == "Ready" && Str(c, "status") == "True")
                    {
                        status = "Ready";
                        break;
                    }
                }
            }

            var roles = "-";
            if (item.TryGetProperty("metadata", out var meta) &&
                meta.TryGetProperty("labels", out var labels) &&
                labels.ValueKind == JsonValueKind.Object)
            {
                var roleList = labels.EnumerateObject()
                    .Where(p => p.Name.StartsWith("node-role.kubernetes.io/"))
                    .Select(p => p.Name["node-role.kubernetes.io/".Length..])
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList();
                if (roleList.Count > 0)
                {
                    roles = string.Join(",", roleList);
                }
            }

            result.Add(new K8sNode { Name = name, Status = status, Roles = roles, Version = version });
        }

        return result;
    }

    public static List<K8sPod> Pods(string json)
    {
        var result = new List<K8sPod>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var phase = Str(item, "status", "phase");
            var node = Str(item, "spec", "nodeName");

            var total = 0;
            var ready = 0;
            var restarts = 0;
            if (item.TryGetProperty("status", out var st) &&
                st.TryGetProperty("containerStatuses", out var cs) &&
                cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray())
                {
                    total++;
                    if (c.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True)
                    {
                        ready++;
                    }

                    if (c.TryGetProperty("restartCount", out var rc) && rc.TryGetInt32(out var rcv))
                    {
                        restarts += rcv;
                    }
                }
            }

            result.Add(new K8sPod
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Status = string.IsNullOrEmpty(phase) ? "-" : phase,
                Ready = $"{ready}/{total}",
                Restarts = restarts,
                Node = string.IsNullOrEmpty(node) ? "-" : node,
            });
        }

        return result;
    }

    public static List<K8sDeployment> Deployments(string json)
    {
        var result = new List<K8sDeployment>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var desired = 0;
            var available = 0;
            if (item.TryGetProperty("spec", out var spec) &&
                spec.TryGetProperty("replicas", out var rep) && rep.TryGetInt32(out var repv))
            {
                desired = repv;
            }

            if (item.TryGetProperty("status", out var st) &&
                st.TryGetProperty("availableReplicas", out var av) && av.TryGetInt32(out var avv))
            {
                available = avv;
            }

            result.Add(new K8sDeployment
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Desired = desired,
                Available = available,
                Ready = $"{available}/{desired}",
            });
        }

        return result;
    }

    public static List<K8sService> Services(string json)
    {
        var result = new List<K8sService>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var ports = "-";
            if (item.TryGetProperty("spec", out var spec) &&
                spec.TryGetProperty("ports", out var pl) &&
                pl.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var p in pl.EnumerateArray())
                {
                    var port = p.TryGetProperty("port", out var pp) ? pp.ToString() : "";
                    var proto = p.TryGetProperty("protocol", out var pr) ? pr.GetString() : "TCP";
                    if (!string.IsNullOrEmpty(port))
                    {
                        parts.Add($"{port}/{proto}");
                    }
                }

                if (parts.Count > 0)
                {
                    ports = string.Join(", ", parts);
                }
            }

            result.Add(new K8sService
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Type = Str(item, "spec", "type"),
                ClusterIP = Str(item, "spec", "clusterIP"),
                Ports = ports,
            });
        }

        return result;
    }

    public static List<string> Namespaces(string json)
    {
        var result = new List<string>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var name = Str(item, "metadata", "name");
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    public static List<K8sIngress> Ingresses(string json)
    {
        var result = new List<K8sIngress>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var cls = Str(item, "spec", "ingressClassName");

            var hosts = new List<string>();
            if (item.TryGetProperty("spec", out var spec) &&
                spec.TryGetProperty("rules", out var rules) &&
                rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rules.EnumerateArray())
                {
                    var host = rule.TryGetProperty("host", out var h) ? h.GetString() : null;
                    if (!string.IsNullOrEmpty(host))
                    {
                        hosts.Add(host!);
                    }
                }
            }

            result.Add(new K8sIngress
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Class = string.IsNullOrEmpty(cls) ? "-" : cls,
                Hosts = hosts.Count == 0 ? "*" : string.Join(", ", hosts),
            });
        }

        return result;
    }

    public static List<K8sPvc> Pvcs(string json)
    {
        var result = new List<K8sPvc>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var capacity = "-";
            if (item.TryGetProperty("status", out var st) &&
                st.TryGetProperty("capacity", out var cap) &&
                cap.TryGetProperty("storage", out var stor))
            {
                capacity = stor.GetString() ?? "-";
            }

            result.Add(new K8sPvc
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Status = Str(item, "status", "phase"),
                Capacity = capacity,
                StorageClass = Str(item, "spec", "storageClassName"),
            });
        }

        return result;
    }

    public static List<K8sConfigMap> ConfigMaps(string json)
    {
        var result = new List<K8sConfigMap>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var keys = 0;
            if (item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                keys = data.EnumerateObject().Count();
            }

            result.Add(new K8sConfigMap
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Keys = keys,
            });
        }

        return result;
    }

    public static List<K8sSecret> Secrets(string json)
    {
        var result = new List<K8sSecret>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var keys = 0;
            if (item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                keys = data.EnumerateObject().Count();
            }

            result.Add(new K8sSecret
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Type = Str(item, "type"),
                Keys = keys,
            });
        }

        return result;
    }

    public static List<K8sJob> Jobs(string json)
    {
        var result = new List<K8sJob>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var desired = 1;
            if (item.TryGetProperty("spec", out var spec) &&
                spec.TryGetProperty("completions", out var comp) && comp.TryGetInt32(out var compv))
            {
                desired = compv;
            }

            var succeeded = 0;
            if (item.TryGetProperty("status", out var st) &&
                st.TryGetProperty("succeeded", out var suc) && suc.TryGetInt32(out var sucv))
            {
                succeeded = sucv;
            }

            result.Add(new K8sJob
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Completions = $"{succeeded}/{desired}",
                Complete = succeeded >= desired,
            });
        }

        return result;
    }

    public static List<K8sCronJob> CronJobs(string json)
    {
        var result = new List<K8sCronJob>();
        var items = Items(json);
        if (items is null)
        {
            return result;
        }

        foreach (var item in items.Value)
        {
            var suspend = false;
            if (item.TryGetProperty("spec", out var spec) &&
                spec.TryGetProperty("suspend", out var sus) && sus.ValueKind == JsonValueKind.True)
            {
                suspend = true;
            }

            var active = 0;
            if (item.TryGetProperty("status", out var st) &&
                st.TryGetProperty("active", out var act) && act.ValueKind == JsonValueKind.Array)
            {
                active = act.GetArrayLength();
            }

            result.Add(new K8sCronJob
            {
                Name = Str(item, "metadata", "name"),
                Namespace = Str(item, "metadata", "namespace"),
                Schedule = Str(item, "spec", "schedule"),
                Suspended = suspend,
                Active = active,
            });
        }

        return result;
    }
}
