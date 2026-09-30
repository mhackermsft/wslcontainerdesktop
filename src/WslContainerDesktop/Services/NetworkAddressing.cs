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
                : "Enter a subnet to set a gateway or IP range.";
        }

        if (!IPNetwork.TryParse(subnet, out var network))
        {
            return $"'{subnet}' is not a valid subnet. Use CIDR notation, for example 172.28.0.0/16.";
        }

        if (!string.IsNullOrEmpty(gateway))
        {
            if (!IPAddress.TryParse(gateway, out var address) || gateway.Contains('/'))
            {
                return $"'{gateway}' is not a valid gateway address.";
            }

            if (!network.Contains(address))
            {
                return $"The gateway {gateway} is not inside the subnet {subnet}.";
            }
        }

        if (!string.IsNullOrEmpty(ipRange))
        {
            if (!IPNetwork.TryParse(ipRange, out var range))
            {
                return $"'{ipRange}' is not a valid IP range. Use CIDR notation, for example 172.28.5.0/24.";
            }

            if (range.BaseAddress.AddressFamily != network.BaseAddress.AddressFamily ||
                range.PrefixLength < network.PrefixLength ||
                !network.Contains(range.BaseAddress))
            {
                return $"The IP range {ipRange} must be inside the subnet {subnet}.";
            }
        }

        return null;
    }
}
