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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Formats.Tar;

namespace WslContainerDesktop.Services;

internal static class ContainerDownloadArchive
{
    public static async Task ExtractAsync(Stream archive, ContainerDownloadPath destination, CancellationToken ct)
    {
        if (!archive.CanSeek)
            throw new ArgumentException("Downloaded archives must be seekable for validation before extraction.");

        var start = archive.Position;
        using (var reader = new TarReader(archive, leaveOpen: true))
        {
            while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                var member = ResolvePath(entry.Name, destination.Directory, allowParentSegments: false);
                RequireContained(member, destination.Target);
                if (member == destination.Directory && entry.EntryType != TarEntryType.Directory)
                    throw new InvalidDataException("The archive root must be a directory.");

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    var basis = entry.EntryType == TarEntryType.SymbolicLink
                        ? Path.GetDirectoryName(member)! : destination.Directory;
                    var link = ResolvePath(entry.LinkName, basis, allowParentSegments: true);
                    RequireContained(link, destination.Target);
                }
                else if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw new InvalidDataException("The archive contains a file type that cannot be safely downloaded to Windows.");
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        archive.Position = start;
        destination.Prepare(ct);
        await TarFile.ExtractToDirectoryAsync(archive, destination.Directory, overwriteFiles: true, ct).ConfigureAwait(false);
    }

    private static string ResolvePath(string name, string directory, bool allowParentSegments)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith('/'))
            throw new InvalidDataException("Archive member and link paths must be relative.");

        var parts = name.TrimEnd('/').Split('/');
        var resolved = directory;
        var hasName = false;
        foreach (var part in parts)
        {
            // POSIX tar uses ./ for root downloads. Links may use ../ within the selected subtree.
            if (part == "." && (!hasName || allowParentSegments)) continue;
            if (part == ".." && allowParentSegments)
            {
                resolved = Path.GetFullPath(Path.Combine(resolved, ".."));
                continue;
            }
            ContainerDownloadPath.ValidateFileName(part);
            hasName = true;
            resolved = Path.GetFullPath(Path.Combine(resolved, part));
        }
        return resolved;
    }

    private static void RequireContained(string path, string root)
    {
        if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The archive contains a member or link outside the requested source subtree.");
        }
    }
}
