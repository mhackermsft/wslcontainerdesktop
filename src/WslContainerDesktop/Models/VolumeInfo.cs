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
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Driver")]
    public string Driver { get; set; } = string.Empty;

    [JsonPropertyName("Mountpoint")]
    public string? Mountpoint { get; set; }

    // ---- Enriched from `volume inspect` + correlation (not part of list output) ----

    [JsonIgnore]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonIgnore]
    public bool IsAnonymous { get; set; }

    /// <summary>Best-effort container this volume belongs to (empty when unknown/orphaned).</summary>
    [JsonIgnore]
    public string UsedBy { get; set; } = string.Empty;

    /// <summary>
    /// Short 12-char id for anonymous volumes (whose name is a hash); the real name
    /// for user-created volumes.
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        IsAnonymous && Name.Length > 12 ? Name[..12] : Name;

    [JsonIgnore]
    public string TypeLabel => IsAnonymous ? "Anonymous" : "Named";

    /// <summary>
    /// "Used by" display. For anonymous volumes we can correlate to a container by
    /// creation time, so an empty value means genuinely orphaned ("—"). For named
    /// volumes wslc exposes no usage data, so we say "Unknown" rather than imply unused.
    /// </summary>
    [JsonIgnore]
    public string UsedByDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(UsedBy))
            {
                return UsedBy;
            }

            return IsAnonymous ? "— (orphaned)" : "Unknown";
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
