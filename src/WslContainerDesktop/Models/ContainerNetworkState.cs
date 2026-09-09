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

/// <summary>Strict inspect data for ownership and endpoint reconciliation; unknown is not empty.</summary>
public sealed class ContainerNetworkState
{
    public string Id { get; private init; } = string.Empty;
    public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NetworkAttachment> Networks { get; } = new(StringComparer.Ordinal);

    public bool HasLabel(string key, string value) => Labels.TryGetValue(key, out var actual) && actual == value;

    public bool IsOwnedBy(ComposeProject project, ComposeService service) =>
        HasLabel(ComposeProject.ProjectLabel, project.Name) && HasLabel(ComposeProject.ServiceLabel, service.Name);

    public void RequireCompatible(NetworkAttachment desired)
    {
        if (!Networks.TryGetValue(desired.Network, out var actual))
        {
            return;
        }

        if (desired.Aliases.Any(a => !actual.Aliases.Contains(a, StringComparer.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(desired.Ipv4Address) && desired.Ipv4Address != actual.Ipv4Address))
        {
            throw new InvalidOperationException(
                $"Network '{desired.Network}' has different aliases or IPv4 settings. Bring the project up again to recreate its container.");
        }
    }

    public static ContainerNetworkState Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
        {
            root = root[0];
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()) ||
            !root.TryGetProperty("NetworkSettings", out var settings) || settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("Networks", out var networks) || networks.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Container inspect did not provide a usable ID and network endpoint map.");
        }

        var result = new ContainerNetworkState { Id = id.GetString()! };
        if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
        {
            foreach (var label in labels.EnumerateObject())
            {
                if (label.Value.ValueKind == JsonValueKind.String)
                {
                    result.Labels[label.Name] = label.Value.GetString()!;
                }
            }
        }

        foreach (var network in networks.EnumerateObject())
        {
            if (network.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Inspect returned an invalid endpoint for '{network.Name}'.");
            }

            var endpoint = new NetworkAttachment { Network = network.Name };
            if (network.Value.TryGetProperty("Aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                endpoint.Aliases = aliases.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetString()!).ToList();
            }

            // Before start, IPAddress is empty but the requested static address is in IPAMConfig.
            if (network.Value.TryGetProperty("IPAMConfig", out var ipam) && ipam.ValueKind == JsonValueKind.Object &&
                ipam.TryGetProperty("IPv4Address", out var requested) && requested.ValueKind == JsonValueKind.String)
            {
                endpoint.Ipv4Address = requested.GetString();
            }

            if (string.IsNullOrWhiteSpace(endpoint.Ipv4Address) &&
                network.Value.TryGetProperty("IPAddress", out var address) && address.ValueKind == JsonValueKind.String)
            {
                endpoint.Ipv4Address = address.GetString();
            }

            result.Networks.Add(network.Name, endpoint);
        }

        return result;
    }
}
