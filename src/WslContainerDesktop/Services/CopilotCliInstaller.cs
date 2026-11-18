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

using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WslContainerDesktop.Services;

/// <summary>Progress reported while a Copilot CLI is being installed.</summary>
/// <param name="Stage">Current step.</param>
/// <param name="BytesReceived">Archive bytes downloaded so far.</param>
/// <param name="TotalBytes">Archive size when the server reports it.</param>
public sealed record CopilotCliInstallProgress(CopilotCliInstallStage Stage, long BytesReceived = 0, long? TotalBytes = null);

/// <summary>Steps of a Copilot CLI install.</summary>
public enum CopilotCliInstallStage
{
    /// <summary>Downloading the release checksum list.</summary>
    FetchingChecksums,

    /// <summary>Downloading the runtime archive.</summary>
    Downloading,

    /// <summary>Checking the archive against the release checksum.</summary>
    Verifying,

    /// <summary>Unpacking the archive.</summary>
    Extracting,

    /// <summary>The runtime is ready to use.</summary>
    Completed,
}

/// <summary>
/// Downloads the Copilot CLI runtime that matches the Copilot SDK this app was built with. It uses the same GitHub
/// release assets and checksum verification as the SDK's own build-time download, and installs into app-owned
/// storage so the runtime is not replaced by the CLI's self-update.
/// </summary>
public sealed partial class CopilotCliInstaller(ILogger<CopilotCliInstaller> logger)
{
    /// <summary>Base URL of the Copilot CLI GitHub releases.</summary>
    internal const string ReleaseBaseUrl = "https://github.com/github/copilot-cli/releases/download";

    private const string RuntimeExecutable = "copilot-runtime.exe";
    private const string RuntimeLibrary = "runtime.node";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>
    /// Copilot CLI version the referenced SDK was built and tested against, recorded at build time from the SDK
    /// package. <c>null</c> when the build did not record it.
    /// </summary>
    public static string? PinnedCliVersion { get; } = typeof(CopilotCliInstaller).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "CopilotCliVersion")?.Value is { Length: > 0 } v ? v : null;

    /// <summary>Release-asset platform name for this machine, or <c>null</c> when the CLI is not published for it.</summary>
    public static string? PlatformSuffix => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "win32-x64",
        Architecture.Arm64 => "win32-arm64",
        _ => null,
    };

    /// <summary>Path of the runtime executable inside an extracted install directory.</summary>
    internal static string RuntimeExecutablePath(string installDirectory, string platform)
        => Path.Combine(installDirectory, "prebuilds", platform, RuntimeExecutable);

    /// <summary>Release asset name for a version and platform.</summary>
    internal static string AssetName(string version, string platform) => $"github-copilot-{version}-{platform}.tgz";

    /// <summary>Returns the installed runtime path for the pinned version when it is already present and verified.</summary>
    public static string? FindInstalled(string? root = null)
    {
        if (PinnedCliVersion is not { } version || PlatformSuffix is not { } platform)
            return null;
        var directory = Path.Combine(root ?? CopilotCliLocator.DefaultAppManagedRoot, version);
        var executable = RuntimeExecutablePath(directory, platform);
        return ReadVerifiedVersion(directory) == version && File.Exists(executable) ? executable : null;
    }

    /// <summary>Install directories under <paramref name="root"/> that hold a verified runtime.</summary>
    public static IReadOnlyList<string> ListInstalled(string? root = null)
    {
        root ??= CopilotCliLocator.DefaultAppManagedRoot;
        if (!Directory.Exists(root))
            return [];
        return Directory.EnumerateDirectories(root)
            .Where(dir => ReadVerifiedVersion(dir) is not null)
            .ToList();
    }

    /// <summary>
    /// Returns the version of an app-managed install, or <c>null</c> when it cannot be trusted. The version comes
    /// from the bundle's own <c>package.json</c> and must match both the completion marker (written only after the
    /// release checksum was verified) and the install folder name.
    /// </summary>
    internal static string? ReadVerifiedVersion(string installDirectory)
    {
        try
        {
            var marker = Path.Combine(installDirectory, CopilotCliLocator.CompleteMarker);
            var manifest = Path.Combine(installDirectory, "package.json");
            if (!File.Exists(marker) || !File.Exists(manifest))
                return null;

            var expected = File.ReadAllText(marker).Trim();
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var version = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            var folder = Path.GetFileName(installDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return !string.IsNullOrEmpty(version)
                && string.Equals(version, expected, StringComparison.Ordinal)
                && string.Equals(version, folder, StringComparison.OrdinalIgnoreCase)
                    ? version
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the verified version when <paramref name="runtimePath"/> is the runtime of an app-managed install
    /// (<c>&lt;version&gt;\prebuilds\&lt;platform&gt;\copilot-runtime.exe</c>), otherwise <c>null</c>.
    /// </summary>
    public static string? TryReadInstalledVersion(string runtimePath)
    {
        if (!string.Equals(Path.GetFileName(runtimePath), RuntimeExecutable, StringComparison.OrdinalIgnoreCase))
            return null;
        var platformDirectory = Path.GetDirectoryName(runtimePath);
        var prebuilds = platformDirectory is null ? null : Path.GetDirectoryName(platformDirectory);
        if (prebuilds is null || !string.Equals(Path.GetFileName(prebuilds), "prebuilds", StringComparison.OrdinalIgnoreCase))
            return null;
        var installDirectory = Path.GetDirectoryName(prebuilds);
        return installDirectory is null ? null : ReadVerifiedVersion(installDirectory);
    }

    /// <summary>Removes every Copilot CLI this app has installed, including partial or damaged installs.</summary>
    /// <exception cref="IOException">A file is in use, for example by a running Copilot session.</exception>
    public void UninstallAll(string? root = null)
    {
        root ??= CopilotCliLocator.DefaultAppManagedRoot;
        if (!Directory.Exists(root))
            return;
        Directory.Delete(root, recursive: true);
        logger.LogInformation("Removed app-installed Copilot CLI runtimes from {Directory}.", root);
    }

    /// <summary>
    /// Installs the pinned Copilot CLI runtime if it is not already present and returns the executable path.
    /// An existing complete install is reused without network access.
    /// </summary>
    public Task<string> InstallAsync(IProgress<CopilotCliInstallProgress>? progress, CancellationToken ct)
    {
        if (PinnedCliVersion is not { } version)
            throw new InvalidOperationException("This build does not record which Copilot CLI version its Copilot SDK expects.");
        if (PlatformSuffix is not { } platform)
            throw new PlatformNotSupportedException($"The Copilot CLI is not published for {RuntimeInformation.OSArchitecture} Windows.");
        return InstallAsync(version, platform, CopilotCliLocator.DefaultAppManagedRoot, ReleaseBaseUrl, s_http, progress, ct);
    }

    /// <summary>Testable install core.</summary>
    internal async Task<string> InstallAsync(
        string version,
        string platform,
        string root,
        string releaseBaseUrl,
        HttpClient http,
        IProgress<CopilotCliInstallProgress>? progress,
        CancellationToken ct)
    {
        var installDirectory = Path.Combine(root, version);
        var executable = RuntimeExecutablePath(installDirectory, platform);
        if (ReadVerifiedVersion(installDirectory) == version && File.Exists(executable))
        {
            progress?.Report(new(CopilotCliInstallStage.Completed));
            return executable;
        }

        Directory.CreateDirectory(root);
        var work = Path.Combine(root, $".install-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var asset = AssetName(version, platform);
            var releaseUrl = $"{releaseBaseUrl.TrimEnd('/')}/v{Uri.EscapeDataString(version)}";

            progress?.Report(new(CopilotCliInstallStage.FetchingChecksums));
            var sums = await http.GetStringAsync($"{releaseUrl}/SHA256SUMS.txt", ct).ConfigureAwait(false);
            var expected = FindChecksum(sums, asset)
                ?? throw new InvalidDataException($"SHA256SUMS.txt for Copilot CLI {version} does not list exactly one checksum for {asset}.");

            var archive = Path.Combine(work, "copilot.tgz");
            await DownloadAsync(http, $"{releaseUrl}/{asset}", archive, progress, ct).ConfigureAwait(false);

            progress?.Report(new(CopilotCliInstallStage.Verifying));
            string actual;
            await using (var stream = File.OpenRead(archive))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch for {asset}: expected {expected}, got {actual}.");

            progress?.Report(new(CopilotCliInstallStage.Extracting));
            var staged = Path.Combine(work, "bundle");
            await ExtractAsync(archive, staged, ct).ConfigureAwait(false);

            var stagedExecutable = RuntimeExecutablePath(staged, platform);
            if (!File.Exists(stagedExecutable) || !File.Exists(Path.Combine(Path.GetDirectoryName(stagedExecutable)!, RuntimeLibrary)))
                throw new InvalidDataException($"The Copilot CLI {version} archive does not contain prebuilds/{platform}/{RuntimeExecutable} with its {RuntimeLibrary}.");

            await File.WriteAllTextAsync(Path.Combine(staged, CopilotCliLocator.CompleteMarker), version, ct).ConfigureAwait(false);
            // The marker alone is not enough: the bundle must also say it is the version we asked for.
            if (!BundleVersionMatches(staged, version))
                throw new InvalidDataException($"The Copilot CLI archive for {version} reports a different version in package.json.");

            if (Directory.Exists(installDirectory))
                Directory.Delete(installDirectory, recursive: true);
            Directory.Move(staged, installDirectory);

            logger.LogInformation("Installed Copilot CLI {Version} to {Directory}.", version, installDirectory);
            progress?.Report(new(CopilotCliInstallStage.Completed));
            return executable;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not remove temporary Copilot CLI install folder {Folder}.", work);
            }
        }
    }

    private static bool BundleVersionMatches(string directory, string version)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
            return document.RootElement.TryGetProperty("version", out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), version, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns the checksum for <paramref name="asset"/> when the list contains exactly one entry for it.</summary>
    internal static string? FindChecksum(string sums, string asset)
    {
        var matches = (sums ?? string.Empty)
            .Split('\n')
            .Select(line => ChecksumLine().Match(line.TrimEnd('\r')))
            .Where(m => m.Success && string.Equals(m.Groups["name"].Value, asset, StringComparison.Ordinal))
            .Select(m => m.Groups["hash"].Value.ToUpperInvariant())
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Extracts a gzip tarball into <paramref name="destination"/>, dropping the archive's top-level folder like
    /// <c>tar --strip-components=1</c>. Links and entries that would escape the destination are rejected.
    /// </summary>
    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            var parts = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
                continue; // top-level folder (stripped)

            if (parts.Any(p => p is "." or ".."))
                throw new InvalidDataException($"Archive entry '{entry.Name}' has a relative path segment.");

            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts[1..])));
            if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry '{entry.Name}' escapes the install folder.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var output = File.Create(target))
                    {
                        if (entry.DataStream is { } data)
                            await data.CopyToAsync(output, ct).ConfigureAwait(false);
                    }
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    throw new InvalidDataException($"Archive entry '{entry.Name}' is a link, which is not supported.");
                default:
                    break; // PAX/GNU metadata entries are consumed by TarReader; anything else is ignored.
            }
        }
    }

    private static async Task DownloadAsync(
        HttpClient http,
        string url,
        string destination,
        IProgress<CopilotCliInstallProgress>? progress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long received = 0;
        var lastReport = 0L;
        progress?.Report(new(CopilotCliInstallStage.Downloading, 0, total));
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (received - lastReport >= 1024 * 1024)
            {
                lastReport = received;
                progress?.Report(new(CopilotCliInstallStage.Downloading, received, total));
            }
        }
        progress?.Report(new(CopilotCliInstallStage.Downloading, received, total));
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})[\t ]+\*?(?<name>\S+)[\t ]*$")]
    private static partial Regex ChecksumLine();
}
