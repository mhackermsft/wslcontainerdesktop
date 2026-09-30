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
using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>A volume row as returned by `wslc volume list --format json`.</summary>
public sealed class VolumeInfo
{
    /// <summary>
    /// Returns the volume name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Gets or sets the name.</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the driver.</summary>
    [JsonPropertyName("Driver")]
    public string Driver { get; set; } = string.Empty;

    /// <summary>Gets or sets the mountpoint.</summary>
    [JsonPropertyName("Mountpoint")]
    public string? Mountpoint { get; set; }

    // ---- Enriched from `volume inspect` + correlation (not part of list output) ----

    /// <summary>Gets or sets the created at.</summary>
    [JsonIgnore]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether this value is anonymous.</summary>
    [JsonIgnore]
    public bool IsAnonymous { get; set; }

    /// <summary>All exact container users, or explicitly labelled legacy estimates.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ContainerUsers { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the usage state.</summary>
    [JsonIgnore]
    public VolumeUsageState UsageState { get; set; }

    /// <summary>Gets the used by.</summary>
    [JsonIgnore]
    public string UsedBy => string.Join(", ", ContainerUsers);

    /// <summary>Gets the usage description.</summary>
    [JsonIgnore]
    public string UsageDescription =>
        $"{UsedByDisplay}\n\nInspect users include stopped containers. Estimated users are based on creation time only. " +
        "Unknown or partial usage is not proof that a volume is unused. The engine determines removal eligibility.";

    /// <summary>
    /// Short 12-char id for anonymous volumes (whose name is a hash); the real name
    /// for user-created volumes.
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        IsAnonymous && Name.Length > 12 ? Name[..12] : Name;

    /// <summary>Gets the type label.</summary>
    [JsonIgnore]
    public string TypeLabel => IsAnonymous ? "Anonymous" : "Named";

    /// <summary>Incomplete snapshots never imply that a volume is unused.</summary>
    [JsonIgnore]
    public string UsedByDisplay
    {
        get
        {
            if (UsageState == VolumeUsageState.Estimated)
            {
                return $"{UsedBy} (estimated; usage unknown)";
            }

            if (ContainerUsers.Count > 0)
            {
                return UsageState == VolumeUsageState.Partial ? $"{UsedBy} (other users unknown)" : UsedBy;
            }

            return UsageState == VolumeUsageState.Unused ? "Unused (inspect snapshot)" : "Unknown";
        }
    }

    /// <summary>Fills CreatedAt and IsAnonymous from `volume inspect` JSON.</summary>
    public void EnrichFromInspect(string inspectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inspectJson);
            var root = doc.RootElement;
            var el = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;

            if (el.TryGetProperty("CreatedAt", out var created) &&
                created.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(created.GetString(), out var dto))
            {
                CreatedAt = dto;
            }

            if (el.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            {
                foreach (var label in labels.EnumerateObject())
                {
                    if (label.Name == "com.docker.volume.anonymous")
                    {
                        IsAnonymous = true;
                        break;
                    }
                }
            }
        }
        catch
        {
            // Leave defaults on parse failure.
        }
    }
}
