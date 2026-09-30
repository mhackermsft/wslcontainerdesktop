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

/// <summary>Model object that stores container file entry information used by services, view models, or dialogs.</summary>
public sealed class ContainerFileEntry
{
    /// <summary>
    /// Returns the file or folder name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the path.</summary>
    public string Path { get; init; } = "/";

    /// <summary>Gets or sets the kind.</summary>
    public string Kind { get; init; } = "f";

    /// <summary>Gets or sets the permissions.</summary>
    public string Permissions { get; init; } = "-";

    /// <summary>Gets or sets the owner.</summary>
    public string Owner { get; init; } = "-";

    /// <summary>Gets or sets the group.</summary>
    public string Group { get; init; } = "-";

    /// <summary>Gets or sets the size bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Gets or sets the modified at.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Gets a value indicating whether this value is directory.</summary>
    public bool IsDirectory => string.Equals(Kind, "d", StringComparison.Ordinal);

    /// <summary>Gets a value indicating whether this value is symlink.</summary>
    public bool IsSymlink => string.Equals(Kind, "l", StringComparison.Ordinal);

    /// <summary>Gets the icon glyph.</summary>
    public string IconGlyph => IsDirectory
        ? "\uE838"
        : IsSymlink
            ? "\uE71B"
            : "\uE8A5";

    /// <summary>Gets the friendly file type label shown in the container file browser.</summary>
    public string TypeDisplay
    {
        get
        {
            if (_typeDisplay is null)
            {
                if (IsDirectory) _typeDisplay = "Folder";
                else if (IsSymlink) _typeDisplay = "Shortcut";
                else
                {
                    var ext = System.IO.Path.GetExtension(Name);
                    _typeDisplay = string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " File";
                }
            }

            return _typeDisplay;
        }
    }

    private string? _typeDisplay;

    /// <summary>Gets the owner display.</summary>
    public string OwnerDisplay => string.IsNullOrWhiteSpace(Group) || string.Equals(Owner, Group, StringComparison.Ordinal)
        ? Owner
        : $"{Owner}:{Group}";

    /// <summary>Gets the size display.</summary>
    public string SizeDisplay => IsDirectory ? "-" : FormatSize(SizeBytes);

    /// <summary>Gets the modified display.</summary>
    public string ModifiedDisplay => ModifiedAt == default
        ? "-"
        : ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Performs the static helper used by this model or dialog.</summary>
    /// <param name="CurrentPath">The current path value supplied by the caller.</param>
    /// <param name="Entries">The entries value supplied by the caller.</param>
    public static (string CurrentPath, IReadOnlyList<ContainerFileEntry> Entries) ParseListing(string output, string fallbackPath)
    {
        var currentPath = string.IsNullOrWhiteSpace(fallbackPath) ? "/" : fallbackPath;
        var entries = new List<ContainerFileEntry>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("PWD\t", StringComparison.Ordinal))
            {
                currentPath = line[4..];
                continue;
            }

            var parts = line.Split('\t', 8);
            if (parts.Length < 8 || !string.Equals(parts[0], "ENTRY", StringComparison.Ordinal))
            {
                continue;
            }

            _ = long.TryParse(parts[5], out var sizeBytes);
            _ = long.TryParse(parts[6], out var modifiedUnixSeconds);

            entries.Add(new ContainerFileEntry
            {
                Kind = parts[1],
                Permissions = parts[2],
                Owner = parts[3],
                Group = parts[4],
                SizeBytes = sizeBytes,
                ModifiedAt = modifiedUnixSeconds > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(modifiedUnixSeconds)
                    : default,
                Name = parts[7],
                Path = CombinePath(currentPath, parts[7]),
            });
        }

        return (currentPath, entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static string CombinePath(string directory, string name)
    {
        var normalizedDirectory = string.IsNullOrWhiteSpace(directory) ? "/" : directory;
        if (normalizedDirectory == "/")
        {
            return "/" + name;
        }

        return normalizedDirectory.TrimEnd('/') + "/" + name;
    }

    private static string FormatSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var suffixIndex = 0;

        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{bytes} {suffixes[suffixIndex]}"
            : $"{value:0.#} {suffixes[suffixIndex]}";
    }
}
