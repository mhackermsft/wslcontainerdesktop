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

using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>
/// A published port mapping as reported by `wslc list --format json`.
/// Protocol is an IANA protocol number (6 = TCP, 17 = UDP).
/// </summary>
public sealed class PortMapping
{
    [JsonPropertyName("BindingAddress")]
    public string? BindingAddress { get; set; }

    [JsonPropertyName("ContainerPort")]
    public int ContainerPort { get; set; }

    [JsonPropertyName("HostPort")]
    public int HostPort { get; set; }

    [JsonPropertyName("Protocol")]
    public int Protocol { get; set; }

    [JsonIgnore]
    public string ProtocolName => Protocol switch
    {
        6 => "tcp",
        17 => "udp",
        _ => Protocol.ToString(),
    };

    [JsonIgnore]
    public string Display =>
        $"{(string.IsNullOrEmpty(BindingAddress) ? "0.0.0.0" : BindingAddress)}:{HostPort} -> {ContainerPort}/{ProtocolName}";

    /// <summary>Host URL usable in a browser, or null when the binding is unroutable.</summary>
    [JsonIgnore]
    public string HostUrl
    {
        get
        {
            var host = string.IsNullOrEmpty(BindingAddress) || BindingAddress == "0.0.0.0"
                ? "localhost"
                : BindingAddress;
            return $"http://{host}:{HostPort}";
        }
    }
}
