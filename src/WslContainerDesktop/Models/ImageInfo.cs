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

using System.Globalization;
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

    [JsonPropertyName("CreatedAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("Size")]
    [JsonConverter(typeof(WslcByteSizeJsonConverter))]
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
    public DateTimeOffset CreatedUtc
    {
        get
        {
            if (Created != 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(Created);
            }

            return TryParseCreatedAt(CreatedAt, out var createdAt)
                ? createdAt
                : DateTimeOffset.UnixEpoch;
        }
    }

    internal static bool TryParseCreatedAt(string? value, out DateTimeOffset createdAt)
    {
        createdAt = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var timestamp = value.Trim();
        var zoneSeparator = timestamp.LastIndexOf(' ');
        var offsetSeparator = zoneSeparator > 0
            ? timestamp.LastIndexOf(' ', zoneSeparator - 1)
            : -1;
        if (offsetSeparator > 0 &&
            IsCompactOffset(timestamp.AsSpan(offsetSeparator + 1, zoneSeparator - offsetSeparator - 1)))
        {
            timestamp = timestamp[..zoneSeparator];
        }

        if (timestamp.Length >= 5 && IsCompactOffset(timestamp.AsSpan(timestamp.Length - 5)))
        {
            timestamp = timestamp.Insert(timestamp.Length - 2, ":");
        }

        return DateTimeOffset.TryParseExact(
                   timestamp,
                   "yyyy-MM-dd HH:mm:ss zzz",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out createdAt) ||
               DateTimeOffset.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                   out createdAt);
    }

    private static bool IsCompactOffset(ReadOnlySpan<char> value) =>
        value.Length == 5 &&
        value[0] is '+' or '-' &&
        char.IsAsciiDigit(value[1]) &&
        char.IsAsciiDigit(value[2]) &&
        char.IsAsciiDigit(value[3]) &&
        char.IsAsciiDigit(value[4]);
}
