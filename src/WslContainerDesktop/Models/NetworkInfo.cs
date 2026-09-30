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

/// <summary>A network row as returned by `wslc network list --format json`.</summary>
public sealed class NetworkInfo
{
    /// <summary>
    /// Returns the network name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Gets or sets the id.</summary>
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the driver.</summary>
    [JsonPropertyName("Driver")]
    public string? Driver { get; set; }

    /// <summary>Gets or sets the scope.</summary>
    [JsonPropertyName("Scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// True for the built-in bridge, host, and none networks. They are shown for context
    /// but cannot be edited or removed.
    /// </summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    /// <summary>Gets a value indicating whether this value can modify.</summary>
    [JsonIgnore]
    public bool CanModify => !IsBuiltIn;

    /// <summary>Gets the driver display.</summary>
    [JsonIgnore]
    public string DriverDisplay => string.IsNullOrEmpty(Driver) ? "bridge" : Driver!;

    /// <summary>
    /// Names of the containers attached to this network, running or stopped. Filled in by
    /// <c>NetworkUsageResolver</c> after the list loads.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ContainerUsers { get; set; } = [];

    /// <summary>
    /// True when every container could be inspected, so an empty <see cref="ContainerUsers"/> really
    /// means the network is not in use. False means some containers are unknown.
    /// </summary>
    [JsonIgnore]
    public bool UsageComplete { get; set; }

    /// <summary>Text for the "Used by" column: the attached containers, "Not in use", or "Unknown".</summary>
    [JsonIgnore]
    public string UsedByDisplay => ContainerUsers.Count > 0
        ? string.Join(", ", ContainerUsers) + (UsageComplete ? string.Empty : " (others unknown)")
        : UsageComplete ? "Not in use" : "Unknown";

    /// <summary>Tooltip for the "Used by" column, explaining where the list comes from.</summary>
    [JsonIgnore]
    public string UsedByTooltip => $"{UsedByDisplay}\n\nIncludes stopped containers, which rejoin this network when they start.";

    /// <summary>Creates a synthesized default bridge entry when the engine omits it from a successful list.</summary>
    public static NetworkInfo DefaultBridge() => new()
    {
        Name = "bridge",
        Driver = "bridge",
        Scope = "local",
        Id = null,
        IsBuiltIn = true,
    };

    /// <summary>Gets the network ID shortened for table display.</summary>
    [JsonIgnore]
    public string ShortId
    {
        get
        {
            if (string.IsNullOrEmpty(Id))
            {
                return IsBuiltIn ? "default" : string.Empty;
            }

            var id = Id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Id[7..] : Id;
            return id.Length > 12 ? id[..12] : id;
        }
    }
}
