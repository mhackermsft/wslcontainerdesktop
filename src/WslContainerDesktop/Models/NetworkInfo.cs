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
    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Driver")]
    public string? Driver { get; set; }

    [JsonPropertyName("Scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// True for the synthetic default "bridge" network. It is shown for context but
    /// cannot be inspected, edited, or removed (wslc does not expose it as an object).
    /// </summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public bool CanModify => !IsBuiltIn;

    [JsonIgnore]
    public string DriverDisplay => string.IsNullOrEmpty(Driver) ? "bridge" : Driver!;

    /// <summary>Creates the synthetic, read-only default bridge network entry.</summary>
    public static NetworkInfo DefaultBridge() => new()
    {
        Name = "bridge",
        Driver = "bridge",
        Scope = "local",
        Id = null,
        IsBuiltIn = true,
    };

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
