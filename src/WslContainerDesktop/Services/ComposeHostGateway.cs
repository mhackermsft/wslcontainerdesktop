// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Project-owned IPv4 hairpin publication rules in the WSLC session VM.</summary>
public static class ComposeHostGateway
{
    public const string MarkerLabel = "com.wslc-compose.host-gateway-compat";
    public sealed record Mapping(string Gateway, string Protocol, int HostPort, string Address, int Port);

    public static bool UsesGateway(ComposeService service) => service.ExtraHosts.Any(entry =>
        entry.Contains(':') && entry[(entry.IndexOf(':') + 1)..].Trim().Equals("host-gateway", StringComparison.OrdinalIgnoreCase));

    public static bool Desired(ComposeProject project) => project.Services.Any(UsesGateway);

    public static bool Requested(ComposeProject project) => Desired(project) ||
        project.AppliedServices.Values.Any(applied => UsesGateway(applied.Service));

    private static string Ip(string value) => IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() :
        throw new InvalidOperationException("Host-gateway requires usable IPv4 endpoint evidence.");

    public static IReadOnlyList<Mapping> Plan(ComposeProject project, IReadOnlyList<JsonElement> inspections)
    {
        var owned = inspections.Where(root => root.GetProperty("State").GetProperty("Running").GetBoolean() &&
            root.GetProperty("Config").GetProperty("Labels").TryGetProperty(ComposeProject.ProjectLabel, out var owner) &&
            owner.GetString() == project.Name).ToList();
        var clientNetworks = project.Services.Where(UsesGateway)
            .SelectMany(service => service.Options.GetNetworkAttachments().Select(endpoint => endpoint.Network))
            .ToHashSet(StringComparer.Ordinal);
        var gateways = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in owned)
            foreach (var network in root.GetProperty("NetworkSettings").GetProperty("Networks").EnumerateObject())
                if (clientNetworks.Contains(network.Name) && network.Value.TryGetProperty("Gateway", out var gateway) &&
                    !string.IsNullOrWhiteSpace(gateway.GetString())) gateways.Add(Ip(gateway.GetString()!));
        var routes = new Dictionary<(string Gateway, string Protocol, int HostPort), Mapping>();
        foreach (var root in owned)
        {
            var labels = root.GetProperty("Config").GetProperty("Labels");
            if (!labels.TryGetProperty(ComposeProject.ServiceLabel, out var serviceLabel)) continue;
            var service = project.Services.SingleOrDefault(s => s.Name == serviceLabel.GetString());
            if (service is null || !root.TryGetProperty("Ports", out var ports)) continue;
            var networks = root.GetProperty("NetworkSettings").GetProperty("Networks");
            var primary = root.GetProperty("HostConfig").GetProperty("NetworkMode").GetString();
            var endpoint = primary is not null && networks.TryGetProperty(primary, out var selected)
                ? selected : networks.EnumerateObject().Select(p => p.Value).FirstOrDefault();
            if (endpoint.ValueKind != JsonValueKind.Object || !endpoint.TryGetProperty("IPAddress", out var ip) ||
                string.IsNullOrWhiteSpace(ip.GetString())) continue;
            var address = Ip(ip.GetString()!);
            foreach (var publication in ports.EnumerateObject())
            {
                var parts = publication.Name.Split('/');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var port) || parts[1] is not ("tcp" or "udp"))
                    throw new InvalidOperationException("Unsupported host-gateway publication evidence.");
                foreach (var binding in publication.Value.EnumerateArray())
                {
                    var hostPort = int.Parse(binding.GetProperty("HostPort").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    if (!service.Options.PortMappings.Any(spec => Allows(spec, port, parts[1], hostPort))) continue;
                    foreach (var gateway in gateways)
                    {
                        var key = (gateway, parts[1], hostPort);
                        var mapping = new Mapping(gateway, parts[1], hostPort, address, port);
                        if (routes.TryGetValue(key, out var previous) && previous != mapping)
                            throw new InvalidOperationException("Host-gateway publication is ambiguous.");
                        routes[key] = mapping;
                    }
                }
            }
        }
        return routes.Values.OrderBy(r => r.Gateway, StringComparer.Ordinal).ThenBy(r => r.Protocol, StringComparer.Ordinal)
            .ThenBy(r => r.HostPort).ToArray();
    }

    private static bool Allows(string spec, int target, string protocol, int hostPort)
    {
        var transport = spec.Split('/');
        if ((transport.Length > 1 ? transport[1] : "tcp") != protocol) return false;
        var pieces = transport[0].Split(':');
        if (pieces.Length > 3 || pieces.Length == 3 && pieces[0] is not ("" or "0.0.0.0")) return false;
        static (int First, int Last) Range(string value)
        {
            var parts = value.Split('-');
            if (parts.Length > 2 || !int.TryParse(parts[0], out var first) ||
                !int.TryParse(parts[^1], out var last) || first < 1 || last > 65535 || last < first)
                throw new InvalidOperationException("Invalid host-gateway publication range.");
            return (first, last);
        }
        var targets = Range(pieces[^1]);
        if (target < targets.First || target > targets.Last) return false;
        if (pieces.Length == 1) return true;
        var published = Range(pieces[^2]);
        return hostPort == published.First + target - targets.First && hostPort <= published.Last;
    }

    public static string Script(string project, IReadOnlyList<Mapping> routes, bool cleanup = false)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project))).ToLowerInvariant();
        var incoming = "WCGP_" + digest[..12];
        var outgoing = "WCGS_" + digest[..12];
        var forward = "WCGF_" + digest[..12];
        var owner = "wslc-compose-host-gateway-" + digest;
        var lines = new List<string> { "set -eu", "command -v iptables >/dev/null", "command -v iptables-restore >/dev/null",
            $"exec 9>/run/{owner}.lock", "flock -w 10 9" };
        foreach (var (chain, kind, table) in new[] { (incoming, "nat", "PREROUTING"), (outgoing, "nat", "POSTROUTING"), (forward, "filter", "FORWARD") })
        {
            lines.Add($"if iptables -w -t {kind} -S {chain} >/dev/null 2>&1; then");
            lines.Add($"  iptables -w -t {kind} -S {chain} | grep -F -- {owner} >/dev/null || exit 71");
            if (cleanup)
            {
                lines.Add($"  while iptables -w -t {kind} -C {table} -j {chain} 2>/dev/null; do");
                lines.Add($"    iptables -w -t {kind} -D {table} -j {chain}");
                lines.Add("  done");
                lines.Add($"  iptables -w -t {kind} -F {chain}");
                lines.Add($"  iptables -w -t {kind} -X {chain}");
                lines.Add("fi");
            }
            else
            {
                lines.Add("else");
                lines.Add($"  iptables -w -t {kind} -N {chain}");
                lines.Add($"  iptables -w -t {kind} -A {chain} -m comment --comment {owner} -j RETURN");
                lines.Add("fi");
            }
        }
        if (cleanup) return (string.Join('\n', lines) + "\n").Replace("iptables -w -t", "iptables -w 10 -t", StringComparison.Ordinal);
        lines.Add("iptables-restore --wait 10 --noflush <<'WCG_RULES'");
        lines.AddRange(["*nat", $"-F {incoming}", $"-F {outgoing}"]);
        var filtering = new List<string> { "*filter", $"-F {forward}" };
        foreach (var route in routes)
        {
            var gateway = Ip(route.Gateway);
            var address = Ip(route.Address);
            if (route.Protocol is not ("tcp" or "udp") || route.HostPort is < 1 or > 65535 || route.Port is < 1 or > 65535)
                throw new InvalidOperationException("Invalid host-gateway mapping.");
            lines.Add($"-A {incoming} -d {gateway}/32 -p {route.Protocol} --dport {route.HostPort} -j DNAT --to-destination {address}:{route.Port}");
            lines.Add($"-A {outgoing} -d {address}/32 -p {route.Protocol} --dport {route.Port} -m conntrack --ctorigdst {gateway} --ctorigdstport {route.HostPort} -j MASQUERADE");
            filtering.Add($"-A {forward} -d {address}/32 -p {route.Protocol} --dport {route.Port} -m conntrack --ctdir ORIGINAL --ctorigdst {gateway} --ctorigdstport {route.HostPort} -j ACCEPT");
            filtering.Add($"-A {forward} -s {address}/32 -p {route.Protocol} --sport {route.Port} -m conntrack --ctstate ESTABLISHED --ctdir REPLY --ctorigdst {gateway} --ctorigdstport {route.HostPort} -j ACCEPT");
        }
        lines.AddRange([$"-A {incoming} -m comment --comment {owner} -j RETURN",
            $"-A {outgoing} -m comment --comment {owner} -j RETURN", "COMMIT"]);
        lines.AddRange(filtering);
        lines.AddRange([$"-A {forward} -m comment --comment {owner} -j RETURN", "COMMIT", "WCG_RULES"]);
        foreach (var (table, chain) in new[] { ("PREROUTING", incoming), ("POSTROUTING", outgoing) })
            lines.Add($"iptables -w -t nat -C {table} -j {chain} 2>/dev/null || iptables -w -t nat -I {table} 1 -j {chain}");
        lines.Add($"if ! iptables -w -t filter -C FORWARD -j {forward} 2>/dev/null; then");
        lines.Add("  position=$(iptables -w -t filter -S FORWARD | awk '$1 == \"-A\" {i++; if ($0 == \"-A FORWARD -j DOCKER-USER\") {print i+1; exit}}')");
        lines.Add("  [ -n \"$position\" ] || exit 72");
        lines.Add($"  iptables -w -t filter -I FORWARD \"$position\" -j {forward}");
        lines.Add("fi");
        return (string.Join('\n', lines) + "\n").Replace("iptables -w -t", "iptables -w 10 -t", StringComparison.Ordinal);
    }
}
