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

using System.Net;

namespace WslContainerDesktop.Services;

/// <summary>Validates the optional IPAM fields of <c>wslc network create</c> before running it.</summary>
public static class NetworkAddressing
{
    /// <summary>Returns a user-facing error, or null when the combination is valid (all blank is valid).</summary>
    public static string? Validate(string? subnet, string? gateway, string? ipRange)
    {
        subnet = subnet?.Trim();
        gateway = gateway?.Trim();
        ipRange = ipRange?.Trim();

        if (string.IsNullOrEmpty(subnet))
        {
            return string.IsNullOrEmpty(gateway) && string.IsNullOrEmpty(ipRange)
                ? null
                : UiText.Get("NetworkAddressing_SubnetRequired", "Enter a subnet to set a gateway or IP range.");
        }

        if (!IPNetwork.TryParse(subnet, out var network))
        {
            return UiText.Get("NetworkAddressing_InvalidSubnet",
                "'{0}' is not a valid subnet. Use CIDR notation, for example 172.28.0.0/16.", subnet);
        }

        if (!string.IsNullOrEmpty(gateway))
        {
            if (!IPAddress.TryParse(gateway, out var address) || gateway.Contains('/'))
            {
                return UiText.Get("NetworkAddressing_InvalidGateway", "'{0}' is not a valid gateway address.", gateway);
            }

            if (!network.Contains(address))
            {
                return UiText.Get("NetworkAddressing_GatewayOutside", "The gateway {0} is not inside the subnet {1}.", gateway, subnet);
            }
        }

        if (!string.IsNullOrEmpty(ipRange))
        {
            if (!IPNetwork.TryParse(ipRange, out var range))
            {
                return UiText.Get("NetworkAddressing_InvalidRange",
                    "'{0}' is not a valid IP range. Use CIDR notation, for example 172.28.5.0/24.", ipRange);
            }

            if (range.BaseAddress.AddressFamily != network.BaseAddress.AddressFamily ||
                range.PrefixLength < network.PrefixLength ||
                !network.Contains(range.BaseAddress))
            {
                return UiText.Get("NetworkAddressing_RangeOutside", "The IP range {0} must be inside the subnet {1}.", ipRange, subnet);
            }
        }

        return null;
    }
}
