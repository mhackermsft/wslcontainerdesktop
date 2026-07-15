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
using CommunityToolkit.Mvvm.ComponentModel;

namespace WslContainerDesktop.Models;

/// <summary>Result of an "is a newer image available upstream?" check for an image tag.</summary>
public enum ImageUpdateState
{
    /// <summary>Not checked, or not checkable (local-only image, digest-pinned reference).</summary>
    Unknown,

    /// <summary>A check is currently in flight.</summary>
    Checking,

    /// <summary>The local digest matches the registry's current digest for the tag.</summary>
    UpToDate,

    /// <summary>The registry has a different (newer) digest for the tag.</summary>
    UpdateAvailable,

    /// <summary>The check could not be completed (network error, private registry without creds).</summary>
    CheckFailed,
}

/// <summary>
/// An image row as returned by `wslc images --format json`.
/// </summary>
public sealed partial class ImageInfo : ObservableObject
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Repository")]
    public string Repository { get; set; } = string.Empty;

    [JsonPropertyName("Tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("Created")]
    public long Created { get; set; }

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    /// <summary>Live result of the upstream update check for this image's tag (not persisted).</summary>
    [JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(IsCheckingUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateTooltip))]
    private ImageUpdateState _updateState = ImageUpdateState.Unknown;

    [JsonIgnore]
    public bool UpdateAvailable => UpdateState == ImageUpdateState.UpdateAvailable;

    [JsonIgnore]
    public bool IsCheckingUpdate => UpdateState == ImageUpdateState.Checking;

    [JsonIgnore]
    public string UpdateTooltip => UpdateState switch
    {
        ImageUpdateState.UpdateAvailable => "A newer image is available upstream. Pull to update.",
        ImageUpdateState.UpToDate => "Up to date with the registry.",
        ImageUpdateState.Checking => "Checking for updates…",
        ImageUpdateState.CheckFailed => "Couldn't check for updates (private registry or network error).",
        _ => "Update status unknown.",
    };

    [JsonIgnore]
    public string ShortId
    {
        get
        {
            var id = Id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Id[7..] : Id;
            return id.Length > 12 ? id[..12] : id;
        }
    }

    [JsonIgnore]
    public string Reference =>
        string.IsNullOrEmpty(Tag) || Tag == "<none>" ? Repository : $"{Repository}:{Tag}";

    [JsonIgnore]
    public DateTimeOffset CreatedUtc => DateTimeOffset.FromUnixTimeSeconds(Created);
}
