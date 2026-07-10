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
/// A container row as returned by `wslc list --all --format json`.
/// </summary>
public sealed class ContainerInfo
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Image")]
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("CreatedAt")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("StateChangedAt")]
    public long StateChangedAt { get; set; }

    [JsonPropertyName("State")]
    public int StateValue { get; set; }

    [JsonPropertyName("Ports")]
    public List<PortMapping> Ports { get; set; } = new();

    [JsonIgnore]
    public ContainerState State =>
        Enum.IsDefined(typeof(ContainerState), StateValue)
            ? (ContainerState)StateValue
            : ContainerState.Unknown;

    [JsonIgnore]
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;

    [JsonIgnore]
    public DateTimeOffset CreatedUtc => DateTimeOffset.FromUnixTimeSeconds(CreatedAt);

    [JsonIgnore]
    public DateTimeOffset StateChangedUtc => DateTimeOffset.FromUnixTimeSeconds(StateChangedAt);
}
