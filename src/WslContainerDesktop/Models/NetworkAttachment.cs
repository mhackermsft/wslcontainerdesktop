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

namespace WslContainerDesktop.Models;

/// <summary>Desired endpoint settings, scoped to one network rather than to the container.</summary>
public sealed class NetworkAttachment
{
    /// <summary>
    /// Returns the network name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Network;

    /// <summary>Gets or sets the network.</summary>
    public string Network { get; set; } = string.Empty;
    /// <summary>Gets or sets the aliases.</summary>
    public List<string> Aliases { get; set; } = new();
    /// <summary>Gets or sets the ipv4 address.</summary>
    public string? Ipv4Address { get; set; }

    /// <summary>Gets the aliases display.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AliasesDisplay => Aliases.Count == 0 ? "-" : string.Join(", ", Aliases);

    /// <summary>Gets the ipv4 display.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Ipv4Display => string.IsNullOrWhiteSpace(Ipv4Address) ? "-" : Ipv4Address!;

    /// <summary>Creates a copy so callers can edit options without mutating the original instance.</summary>
    /// <returns>The requested value for the caller.</returns>
    public NetworkAttachment Clone() => new()
    {
        Network = Network,
        Aliases = new List<string>(Aliases),
        Ipv4Address = Ipv4Address,
    };

    /// <summary>Converts model data for to connect arguments scenarios.</summary>
    /// <param name="containerId">The container id value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public List<string> ToConnectArguments(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Network);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        var args = new List<string> { "network", "connect" };
        AddEndpointArguments(args);
        args.Add(Network);
        args.Add(containerId);
        return args;
    }

    /// <summary>Performs the add endpoint arguments helper used by this model or dialog.</summary>
    /// <param name="args">The args value supplied by the caller.</param>
    public void AddEndpointArguments(List<string> args)
    {
        foreach (var alias in Aliases.Where(a => !string.IsNullOrWhiteSpace(a))
                     .Select(a => a.Trim()).Distinct(StringComparer.Ordinal))
        {
            args.Add("--network-alias");
            args.Add(alias);
        }

        if (!string.IsNullOrWhiteSpace(Ipv4Address))
        {
            if (!System.Net.IPAddress.TryParse(Ipv4Address, out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                throw new ArgumentException($"Network '{Network}' has an invalid IPv4 address: {Ipv4Address}");
            }

            args.Add("--ip");
            args.Add(ip.ToString());
        }
    }
}
