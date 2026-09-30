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
[JsonConverter(typeof(ContainerInfoJsonConverter))]
public sealed class ContainerInfo
{
    /// <summary>Gets or sets the native health.</summary>
    [JsonIgnore]
    public NativeHealthObservation NativeHealth { get; set; } =
        new(NativeHealthState.Unknown, Diagnostic: "Health has not been inspected.");

    /// <summary>Gets or sets the id.</summary>
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the image.</summary>
    [JsonPropertyName("Image")]
    public string Image { get; set; } = string.Empty;

    /// <summary>Gets or sets the created at.</summary>
    [JsonPropertyName("CreatedAt")]
    public long CreatedAt { get; set; }

    // Legacy never-started sentinel 18446744011573954816 is normalized to unavailable by the adapter.
    // Keep ulong for existing consumers; the display accessor also guards manually populated values.
    /// <summary>Gets or sets the state changed at.</summary>
    [JsonPropertyName("StateChangedAt")]
    public ulong StateChangedAt { get; set; }

    /// <summary>Gets or sets the state value.</summary>
    [JsonPropertyName("State")]
    public int StateValue { get; set; }

    /// <summary>Gets or sets the ports.</summary>
    [JsonPropertyName("Ports")]
    public List<PortMapping> Ports { get; set; } = new();

    /// <summary>Gets or sets the size.</summary>
    [JsonIgnore]
    public string Size { get; set; } = string.Empty;

    /// <summary>Gets or sets the size rw bytes.</summary>
    [JsonIgnore]
    public long? SizeRwBytes { get; set; }

    /// <summary>Gets or sets the size root fs bytes.</summary>
    [JsonIgnore]
    public long? SizeRootFsBytes { get; set; }

    /// <summary>False when list/inspect did not establish the complete published-port configuration.</summary>
    [JsonIgnore]
    public bool PortsKnown { get; set; }

    /// <summary>Gets or sets a value indicating whether the created at known flag is set.</summary>
    [JsonIgnore]
    public bool CreatedAtKnown { get; set; }

    /// <summary>Gets or sets a value indicating whether the state changed at known flag is set.</summary>
    [JsonIgnore]
    public bool StateChangedAtKnown { get; set; }

    /// <summary>Gets the state.</summary>
    [JsonIgnore]
    public ContainerState State =>
        Enum.IsDefined(typeof(ContainerState), StateValue)
            ? (ContainerState)StateValue
            : ContainerState.Unknown;

    /// <summary>Gets the short id.</summary>
    [JsonIgnore]
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;

    /// <summary>Gets a value indicating whether the size known flag is set.</summary>
    [JsonIgnore]
    public bool SizeKnown => SizeRwBytes is not null || SizeRootFsBytes is not null || !string.IsNullOrWhiteSpace(Size);

    /// <summary>Gets the size rw display.</summary>
    [JsonIgnore]
    public string SizeRwDisplay => SizeRwBytes is long bytes ? HumanSize(bytes) : "-";

    /// <summary>Gets the size root fs display.</summary>
    [JsonIgnore]
    public string SizeRootFsDisplay => SizeRootFsBytes is long bytes ? HumanSize(bytes) : "-";

    /// <summary>Gets the size display.</summary>
    [JsonIgnore]
    public string SizeDisplay => SizeKnown
        ? SizeRwBytes is long rw && SizeRootFsBytes is long root
            ? $"{HumanSize(rw)} (virtual {HumanSize(root)})"
            : !string.IsNullOrWhiteSpace(Size) ? Size : SizeRwDisplay
        : "-";

    /// <summary>Gets the created utc.</summary>
    [JsonIgnore]
    public DateTimeOffset CreatedUtc => CreatedAt is >= -62135596800 and <= 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds(CreatedAt)
        : DateTimeOffset.UnixEpoch;

    /// <summary>
    /// The container's last state-change time, or <see cref="CreatedUtc"/> if wslc reported its
    /// out-of-range "never changed" sentinel (see <see cref="StateChangedAt"/>).
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset StateChangedUtc =>
        StateChangedAt is > 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds((long)StateChangedAt)
            : CreatedUtc;

    private static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
