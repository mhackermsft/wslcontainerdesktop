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

public sealed class ContainerFileEntry
{
    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = "/";

    public string Kind { get; init; } = "f";

    public string Permissions { get; init; } = "-";

    public string Owner { get; init; } = "-";

    public string Group { get; init; } = "-";

    public long SizeBytes { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }

    public bool IsDirectory => string.Equals(Kind, "d", StringComparison.Ordinal);

    public bool IsSymlink => string.Equals(Kind, "l", StringComparison.Ordinal);

    public string IconGlyph => IsDirectory
        ? "\uE838"
        : IsSymlink
            ? "\uE71B"
            : "\uE8A5";

    public string TypeDisplay
    {
        get
        {
            if (IsDirectory) return "Folder";
            if (IsSymlink) return "Shortcut";
            var ext = System.IO.Path.GetExtension(Name);
            return string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " File";
        }
    }

    public string OwnerDisplay => string.IsNullOrWhiteSpace(Group) || string.Equals(Owner, Group, StringComparison.Ordinal)
        ? Owner
        : $"{Owner}:{Group}";

    public string SizeDisplay => IsDirectory ? "-" : FormatSize(SizeBytes);

    public string ModifiedDisplay => ModifiedAt == default
        ? "-"
        : ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

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
