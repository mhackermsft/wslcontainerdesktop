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

namespace WslContainerDesktop.Services;

/// <summary>
/// Validates a container-to-host download target before <c>wslc cp</c> overwrites anything on Windows.
/// It keeps the selected destination inside the chosen folder and rejects host links that could redirect the write elsewhere.
/// </summary>
internal sealed class ContainerDownloadPath
{
    private ContainerDownloadPath(string source, string directory, string name, string target)
    {
        Source = source;
        Directory = directory;
        Name = name;
        Target = target;
    }

    /// <summary>Normalized absolute path inside the container.</summary>
    public string Source { get; }
    /// <summary>Validated host directory selected by the user.</summary>
    public string Directory { get; }
    /// <summary>Windows-safe leaf name copied from the container path.</summary>
    public string Name { get; }
    /// <summary>Full host path that will receive the downloaded file or directory.</summary>
    public string Target { get; }

    /// <summary>Builds and validates a download plan from a container path and host directory.</summary>
    public static ContainerDownloadPath Create(string containerPath, string hostDirectory)
    {
        if (string.IsNullOrWhiteSpace(containerPath) || !containerPath.StartsWith('/') ||
            containerPath.Contains('\0') || containerPath.Split('/').Any(part => part is "." or ".."))
        {
            throw new ArgumentException("The container path must be absolute and must not contain '.' or '..' segments.");
        }

        var source = containerPath.TrimEnd('/');
        if (source.Length == 0) source = "/";
        var name = source[(source.LastIndexOf('/') + 1)..];
        if (name.Length > 0) ValidateFileName(name);

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostDirectory));
        if (string.Equals(directory, Path.GetPathRoot(directory), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select a destination folder below the drive or share root.");
        }

        var target = name.Length == 0 ? directory : Path.GetFullPath(Path.Combine(directory, name));
        if (name.Length > 0 &&
            !string.Equals(Path.GetDirectoryName(target), directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The download must remain inside its destination folder.");
        }

        return new(source, directory, name, target);
    }

    /// <summary>Rejects names Windows cannot safely create, including reserved device names.</summary>
    internal static void ValidateFileName(string name)
    {
        if (name.Length == 0 || name.Any(c => c < ' ' || "<>:\"/\\|?*".Contains(c)) ||
            name.EndsWith('.') || name.EndsWith(' ') || IsDeviceName(name))
        {
            throw new ArgumentException("A downloaded name cannot be represented safely as a Windows filename.");
        }
    }

    /// <summary>Creates the destination directory and prepares any existing target for overwrite.</summary>
    public void Prepare(CancellationToken ct)
    {
        RequireUnlinkedAncestors(Directory, ct);
        System.IO.Directory.CreateDirectory(Directory);
        RequireUnlinkedAncestors(Directory, ct);
        PrepareOverwrite(Target, ct);
    }

    /// <summary>Checks an existing destination tree for links and clears read-only file attributes.</summary>
    internal static void PrepareOverwrite(
        string path, CancellationToken ct, Func<string, FileAttributes>? readAttributes = null)
    {
        ct.ThrowIfCancellationRequested();
        var attributes = ReadAttributes(path, readAttributes);
        if (attributes is null) return;
        RejectLink(path, attributes.Value);
        if ((attributes.Value & FileAttributes.Directory) != 0)
        {
            foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path))
                PrepareOverwrite(child, ct, readAttributes);
        }
        else if ((attributes.Value & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes.Value & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>Ensures no existing ancestor directory is a symbolic link or junction.</summary>
    internal static void RequireUnlinkedAncestors(string path, CancellationToken ct)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            ct.ThrowIfCancellationRequested();
            if (ReadAttributes(current) is { } attributes) RejectLink(current, attributes);
        }
    }

    /// <summary>Reads file attributes while treating a missing path as an expected case.</summary>
    private static FileAttributes? ReadAttributes(string path, Func<string, FileAttributes>? readAttributes = null)
    {
        try
        {
            return (readAttributes ?? File.GetAttributes)(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing destinations are expected; access errors must still fail the transfer.
            return null;
        }
    }

    /// <summary>Rejects symbolic links and junctions while allowing non-link reparse points.</summary>
    private static void RejectLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return;
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path) : new FileInfo(path);
        // Cloud placeholders are reparse points too, but do not redirect to a link target.
        if (info.LinkTarget is not null)
            throw new IOException("The download would traverse or overwrite a host symbolic link. Choose another destination.");
    }

    /// <summary>Detects Windows reserved device names such as <c>CON</c> and <c>LPT1</c>.</summary>
    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }
}
