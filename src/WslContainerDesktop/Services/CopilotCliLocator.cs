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

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WslContainerDesktop.Services;

/// <summary>Where a Copilot CLI candidate was found.</summary>
public enum CopilotCliSource
{
    /// <summary>The <c>COPILOT_CLI_PATH</c> environment variable.</summary>
    Environment,

    /// <summary>A CLI downloaded by this app from Settings.</summary>
    AppManaged,

    /// <summary>A WinGet portable-package link.</summary>
    WinGetLinks,

    /// <summary>The default per-user install location used by the GitHub.Copilot WinGet/MSI package.</summary>
    WinGetInstall,

    /// <summary>A global npm install.</summary>
    Npm,

    /// <summary>A directory on <c>PATH</c>.</summary>
    Path,

    /// <summary>A path the user browsed to that none of the automatic sources found.</summary>
    Custom,
}

/// <summary>An executable that may host the Copilot runtime.</summary>
/// <param name="Path">Full path to the executable or command script.</param>
/// <param name="Source">How the candidate was discovered.</param>
public sealed record CopilotCliCandidate(string Path, CopilotCliSource Source);

/// <summary>File-system and environment access used for discovery, replaceable in tests.</summary>
internal sealed record CopilotCliProbeEnvironment(
    Func<string, string?> GetEnvironmentVariable,
    string LocalAppData,
    string AppData,
    string AppManagedRoot,
    Func<string, bool> FileExists,
    Func<string, IEnumerable<string>> EnumerateDirectories,
    Func<string, string?> ReadAppManagedVersion)
{
    /// <summary>Environment backed by the real process and file system.</summary>
    public static CopilotCliProbeEnvironment Current => new(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        CopilotCliLocator.DefaultAppManagedRoot,
        File.Exists,
        path => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [],
        CopilotCliInstaller.ReadVerifiedVersion);
}

/// <summary>Finds Copilot CLI executables and resolves which one the Copilot SDK should start.</summary>
public static partial class CopilotCliLocator
{
    /// <summary>Environment variable that forces a specific Copilot CLI executable.</summary>
    public const string EnvironmentVariable = "COPILOT_CLI_PATH";

    /// <summary>File written next to an app-managed install once it has been verified and extracted.</summary>
    internal const string CompleteMarker = ".copilot-runtime-complete";

    /// <summary>Root folder for CLIs downloaded by this app.</summary>
    public static string DefaultAppManagedRoot => Path.Combine(AppStoragePaths.LocalCache(), "copilot-cli");

    /// <summary>
    /// Returns every Copilot CLI candidate that exists, in the order automatic selection tries them.
    /// Duplicate paths are removed, keeping the earliest source.
    /// </summary>
    public static IReadOnlyList<CopilotCliCandidate> FindCandidates() => FindCandidates(CopilotCliProbeEnvironment.Current);

    /// <summary>Testable overload of <see cref="FindCandidates()"/>.</summary>
    internal static IReadOnlyList<CopilotCliCandidate> FindCandidates(CopilotCliProbeEnvironment env)
    {
        var candidates = new List<CopilotCliCandidate>();

        Add(env.GetEnvironmentVariable(EnvironmentVariable), CopilotCliSource.Environment);

        foreach (var managed in FindAppManaged(env))
            Add(managed, CopilotCliSource.AppManaged);

        Add(Path.Combine(env.LocalAppData, "Microsoft", "WinGet", "Links", "copilot.exe"), CopilotCliSource.WinGetLinks);
        Add(Path.Combine(env.LocalAppData, "GitHubCopilotCLI", "copilot.exe"), CopilotCliSource.WinGetInstall);
        Add(Path.Combine(env.AppData, "npm", "copilot.cmd"), CopilotCliSource.Npm);

        var path = env.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Add(SafeCombine(directory, "copilot.exe"), CopilotCliSource.Path);
            Add(SafeCombine(directory, "copilot.cmd"), CopilotCliSource.Path);
        }

        return candidates
            .Where(c => env.FileExists(c.Path))
            .GroupBy(c => NormalizeForComparison(c.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        void Add(string? candidate, CopilotCliSource source)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                candidates.Add(new CopilotCliCandidate(candidate.Trim().Trim('"'), source));
        }
    }

    /// <summary>
    /// Resolves the CLI the SDK should start. A non-empty <paramref name="configuredPath"/> is used as-is when it
    /// exists and never falls back to automatic discovery, so a stale selection is reported instead of silently
    /// starting a different CLI. An empty value selects the first automatic candidate.
    /// </summary>
    public static string? Resolve(string? configuredPath) => Resolve(configuredPath, CopilotCliProbeEnvironment.Current);

    /// <summary>Testable overload of <see cref="Resolve(string?)"/>.</summary>
    internal static string? Resolve(string? configuredPath, CopilotCliProbeEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim().Trim('"');
            return env.FileExists(trimmed) ? trimmed : null;
        }

        return FindCandidates(env).FirstOrDefault()?.Path;
    }

    /// <summary>
    /// Verified app-managed runtimes, newest version first, with the SDK's pinned version ahead of all others.
    /// Partial or damaged installs are not offered.
    /// </summary>
    internal static IEnumerable<string> FindAppManaged(CopilotCliProbeEnvironment env)
    {
        var platform = CopilotCliInstaller.PlatformSuffix;
        if (platform is null)
            return [];

        var pinned = CopilotCliInstaller.PinnedCliVersion;
        return env.EnumerateDirectories(env.AppManagedRoot)
            .Where(dir => !Path.GetFileName(dir).StartsWith('.'))
            .Select(dir => (Dir: dir, Version: env.ReadAppManagedVersion(dir)))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => string.Equals(x.Version, pinned, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => ParseVersion(x.Version!))
            .Select(x => CopilotCliInstaller.RuntimeExecutablePath(x.Dir, platform));
    }

    /// <summary>
    /// Returns the version a candidate runs when the Copilot SDK starts it, or <c>null</c> if it cannot be read.
    /// The SDK always passes <c>--no-auto-update</c>, which makes <c>copilot.exe</c> run its installed version instead
    /// of handing off to a newer self-updated copy, so the probe passes it too. Plain <c>--version</c> would report
    /// the self-updated version and wrongly mark an older install as compatible.
    /// </summary>
    public static async Task<string?> ProbeVersionAsync(string path, CancellationToken ct)
        // An app-managed runtime does not answer --version; its verified install records the version instead.
        => CopilotCliInstaller.TryReadInstalledVersion(path)
           ?? await ProbeVersionAsync(path, ["--no-auto-update", "--version"], ct).ConfigureAwait(false)
           // A runtime build that does not know the flag can still report its (fixed) version.
           ?? await ProbeVersionAsync(path, ["--version"], ct).ConfigureAwait(false);

    private static async Task<string?> ProbeVersionAsync(string path, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var isScript = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            FileName = isScript ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isScript)
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(path);
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                throw;
            }
            return ParseVersionOutput(await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Extracts the first semantic version from CLI <c>--version</c> output.</summary>
    internal static string? ParseVersionOutput(string output)
    {
        var match = VersionPattern().Match(output ?? string.Empty);
        return match.Success ? match.Groups["v"].Value : null;
    }

    /// <summary>Parses the numeric part of a version for ordering; unparseable values sort last.</summary>
    internal static Version ParseVersion(string value)
    {
        var core = value.Split('-', 2)[0];
        return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
    }

    private static string? SafeCombine(string directory, string file)
    {
        try
        {
            return Path.Combine(directory.Trim('"'), file);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string NormalizeForComparison(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    [GeneratedRegex(@"(?<![\d.])(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)")]
    private static partial Regex VersionPattern();
}
