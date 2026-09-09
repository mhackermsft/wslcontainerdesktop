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

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WslContainerDesktop.Models;

internal static class ContainerPortParser
{
    internal static List<PortMapping> ReadStructured(JsonElement value, JsonSerializerOptions options)
    {
        var ports = JsonSerializer.Deserialize<List<PortMapping>>(value, options)
            ?? throw new JsonException("Invalid structured container ports.");
        if (ports.Any(p => p is null || p.HostPort is < 0 or > 65535 ||
            p.ContainerPort is < 0 or > 65535 || p.Protocol < 0))
            throw new JsonException("Invalid structured container port mapping.");
        return ports;
    }

    // Only explicit single-port bindings are lossless. Empty/exposed-only/range displays need inspect.
    internal static bool TryDisplay(string display, out List<PortMapping> ports)
    {
        ports = [];
        if (string.IsNullOrWhiteSpace(display))
            return false;
        foreach (var entry in display.Split(','))
        {
            var parts = entry.Trim().Split("->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryTarget(parts[1], out var containerPort, out var protocol))
            {
                ports.Clear();
                return false;
            }
            var separator = parts[0].LastIndexOf(':');
            var host = separator >= 0 ? parts[0][..separator].Trim('[', ']') : "";
            var portText = separator >= 0 ? parts[0][(separator + 1)..] : parts[0];
            if (!TryPort(portText, out var hostPort) ||
                host.Length > 0 && !IPAddress.TryParse(host, out _))
            {
                ports.Clear();
                return false;
            }
            ports.Add(new PortMapping
            {
                BindingAddress = host, HostPort = hostPort, ContainerPort = containerPort, Protocol = protocol,
            });
        }
        return true;
    }

    internal static bool TryInspect(JsonElement root, JsonSerializerOptions options, out List<PortMapping> ports)
    {
        ports = [];
        // WSLC's top-level Ports survives while stopped, unlike Docker's runtime NetworkSettings.Ports.
        var value = ContainerInfoJsonConverter.Property(root, "Ports");
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            value = ContainerInfoJsonConverter.Property(ContainerInfoJsonConverter.Property(root, "HostConfig"), "PortBindings");
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            value = ContainerInfoJsonConverter.Property(ContainerInfoJsonConverter.Property(root, "NetworkSettings"), "Ports");
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;
        if (value.ValueKind == JsonValueKind.Array)
        {
            ports = ReadStructured(value, options);
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Invalid inspect port configuration.");
        foreach (var binding in value.EnumerateObject())
        {
            if (!TryTarget(binding.Name, out var containerPort, out var protocol))
                throw new JsonException("Invalid inspect container port.");
            if (binding.Value.ValueKind == JsonValueKind.Null)
                continue; // Exposed but explicitly not published.
            if (binding.Value.ValueKind != JsonValueKind.Array)
                throw new JsonException("Invalid inspect host bindings.");
            foreach (var host in binding.Value.EnumerateArray())
            {
                var hostPortValue = ContainerInfoJsonConverter.Property(host, "HostPort");
                if (!TryPort(hostPortValue.ToString(), out var hostPort))
                    throw new JsonException("Invalid inspect host port.");
                ports.Add(new PortMapping
                {
                    ContainerPort = containerPort, Protocol = protocol, HostPort = hostPort,
                    BindingAddress = ContainerInfoJsonConverter.ReadString(host, "HostIp"),
                });
            }
        }
        return true;
    }

    private static bool TryTarget(string text, out int port, out int protocol)
    {
        var parts = text.Split('/');
        protocol = parts.Length == 2 ? parts[1].ToLowerInvariant() switch
        {
            "tcp" => 6, "udp" => 17, "sctp" => 132, _ => 0,
        } : 0;
        port = 0;
        return protocol != 0 && TryPort(parts[0], out port);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && ValidPort(port);

    private static bool ValidPort(int port) => port is > 0 and <= 65535;
}
