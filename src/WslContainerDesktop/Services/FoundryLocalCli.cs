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
using System.Text.Json;
using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Read-only standalone CLI adapter. Source:
/// https://learn.microsoft.com/azure/foundry-local/reference/reference-cli
/// Never calls model list (which can download EPs), start, restart, or config.
/// CLI 0.10.3 help is covered by recorded fixtures; status output remains a separate contract.
/// Status requires positive installed-help evidence, never a command mentioned in prose.
/// </summary>
public sealed class FoundryLocalCli
{
    private readonly Func<string?> _findExecutable;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> _run;

    public FoundryLocalCli() : this(FindExecutable, (start, ct) => ProcessExecutor.RunAsync(start,
        timeout: TimeSpan.FromSeconds(10), launchErrorContext: "Could not launch Foundry Local CLI.", ct: ct)) { }

    internal FoundryLocalCli(Func<string?> findExecutable,
        Func<ProcessStartInfo, CancellationToken, Task<CommandResult>> run)
    {
        _findExecutable = findExecutable;
        _run = run;
    }

    public async Task<FoundryLocalDiscovery> DiscoverAsync(IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var executable = RequireExecutable();
        progress?.Report("Reading standalone CLI version and help; no model or EP downloads requested…");
        var version = await RunAsync(executable, ["--version"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 256)
            throw new InvalidDataException("Foundry Local returned an unrecognized CLI version.");
        var help = await RunAsync(executable, ["--help"], ct).ConfigureAwait(false);
        // Select from the executable's advertised commands, not its version. Legacy previews
        // used 'service'; a failed status command is never retried with a different command.
        var group = AdvertisesCommand(help, "server") ? "server"
            : AdvertisesCommand(help, "service") ? "service" : null;
        if (group is null)
            throw new InvalidDataException("This CLI does not advertise a recognized server/service status command. No fallback was attempted.");
        var groupHelp = await RunAsync(executable, [group, "--help"], ct).ConfigureAwait(false);
        if (!AdvertisesCommand(groupHelp, "status"))
            throw new InvalidDataException("The installed CLI does not advertise status in its server/service help. Enter the actual endpoint manually; no status syntax was inferred from the version.");
        progress?.Report("Reading the external server's actual endpoint; not starting or adopting it…");
        if (version.Trim() == "0.10.3")
        {
            var observed = ParseServerStatus(await RunAsync(executable, [group, "status", "--output", "json"], ct).ConfigureAwait(false));
            if (!observed.Running)
                throw new InvalidDataException("Foundry Local is stopped. Stale stored URLs/PIDs cannot be used; explicitly start the runtime before connecting.");
            if (observed.Endpoints.Count != 1)
                throw new InvalidDataException("Foundry Local reports multiple service URLs; choose the actual endpoint explicitly.");
            return new("0.10.3", observed.Endpoints[0]);
        }
        var status = await RunAsync(executable, [group, "status"], ct).ConfigureAwait(false);
        return new(AiTextSanitizer.Sanitize(version.Trim(), 256), ParseEndpoint(status));
    }

    public async Task<FoundryLocalServerStatus> ReadServerStatusAsync(CancellationToken ct)
    {
        var executable = RequireExecutable();
        if ((await RunAsync(executable, ["--version"], ct).ConfigureAwait(false)).Trim() != "0.10.3")
            throw new InvalidDataException("Standalone server status is supported only for observed CLI 0.10.3.");
        return ParseServerStatus(await RunAsync(executable, ["server", "status", "--output", "json"], ct).ConfigureAwait(false));
    }

    internal static FoundryLocalServerStatus ParseServerStatus(string output)
    {
        using var json = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || !root.TryGetProperty("running", out var running)
            || running.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Unknown Foundry server-status schema.");
        // A stopped 0.10.3 daemon retains previous pid/URLs/start time. Never resurrect it
        // from those stale fields or use them to authorize inference/owned-process actions.
        if (!running.GetBoolean()) return new(false, null, null, []);
        if (!root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number
            || !pid.TryGetInt32(out var processId) || processId <= 0
            || !root.TryGetProperty("startedAt", out var started) || started.ValueKind != JsonValueKind.String
            || !started.TryGetDateTimeOffset(out var startedAt)
            || !root.TryGetProperty("webUrls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Running Foundry status lacks a process/start identity or service URLs.");
        var endpoints = new List<string>();
        foreach (var url in urls.EnumerateArray())
        {
            if (url.ValueKind != JsonValueKind.String) throw new InvalidDataException("Unknown Foundry URL metadata.");
            var endpoint = url.GetString()!;
            FoundryLocalEndpoint.Validate(endpoint);
            endpoints.Add(endpoint);
        }
        if (endpoints.Count == 0 || endpoints.Distinct(StringComparer.Ordinal).Count() != endpoints.Count)
            throw new InvalidDataException("Foundry status has missing or duplicate service URLs.");
        return new(true, processId, startedAt, endpoints);
    }

    public async Task<FoundryLocalCacheLocation> ReadCacheLocationAsync(CancellationToken ct)
    {
        var executable = RequireExecutable();
        var version = await RunAsync(executable, ["--version"], ct).ConfigureAwait(false);
        if (version.Trim() != "0.10.3")
            throw new InvalidDataException("Cache-location schema is verified only for CLI 0.10.3. No cache or model mutation attempted.");
        var help = await RunAsync(executable, ["--help"], ct).ConfigureAwait(false);
        if (!AdvertisesCommand(help, "cache"))
            throw new InvalidDataException("This CLI does not advertise cache commands; no fallback attempted.");
        var cacheHelp = await RunAsync(executable, ["cache", "--help"], ct).ConfigureAwait(false);
        if (!AdvertisesCommand(cacheHelp, "location"))
            throw new InvalidDataException("This CLI does not advertise cache location; no fallback attempted.");
        // The real isolated capture establishes this small response. Do not substitute
        // cache list: it timed out under outbound isolation and is not a safe inventory probe.
        return ParseCacheLocation(await RunAsync(executable, ["cache", "location", "--output", "json"], ct).ConfigureAwait(false));
    }

    internal static FoundryLocalCacheLocation ParseCacheLocation(string output)
    {
        using var json = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("userSet", out var userSet)
            || userSet.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Unknown Foundry cache-location schema. No cache or settings changed.");
        var value = path.GetString()!;
        if (value.Length > 2048 || value.Any(char.IsControl) || value != value.Trim()
            || !IsLocalAbsolutePath(value) || value.Split('\\').Any(segment => segment is "." or "..")
            || value.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("Foundry reported an unsafe cache location. No files written.");
        return new(Path.GetFullPath(value), userSet.GetBoolean());
    }

    private string RequireExecutable()
    {
        var executable = _findExecutable();
        if (string.IsNullOrEmpty(executable) || !IsLocalAbsolutePath(executable)
            || !Path.GetFileName(executable).Equals("foundry.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Foundry Local CLI was not found on the absolute Windows PATH. No installation or runtime start was attempted. Standalone metadata requires CLI process identity even for a manually entered runtime URL.");
        return executable;
    }

    internal static bool AdvertisesCommand(string help, string command)
    {
        var section = false;
        foreach (var line in help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line == "Commands:") { section = true; continue; }
            if (!section) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
            if (Regex.IsMatch(line, @"^\s+" + Regex.Escape(command) + @"(?:,|\s|$)"))
                return true;
        }
        return false;
    }

    private async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var result = await _run(start, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!result.Success)
            throw new InvalidOperationException("Foundry Local CLI discovery failed. Inspect the standalone installation and running server; no automatic retry, install or start was attempted.");
        if (result.StandardOutput.Length > 64 * 1024)
            throw new InvalidDataException("Foundry Local CLI discovery output exceeded its metadata limit.");
        return result.StandardOutput;
    }

    internal static string ParseEndpoint(string output)
    {
        // Generic text evidence only, not a claimed version-locked status/JSON schema.
        // The setup service separately verifies REST inventory before offering a connection.
        var urls = Regex.Matches(output, @"https?://[^\s<>""'|]+", RegexOptions.IgnoreCase)
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length != 1)
            throw new InvalidDataException("Server status did not report exactly one unambiguous endpoint. Enter the actual loopback URL manually; no default port was inferred.");
        FoundryLocalEndpoint.Validate(urls[0]);
        return urls[0];
    }

    private static string? FindExecutable()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!IsLocalAbsolutePath(directory)) continue;
            directory = Path.GetFullPath(directory);
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory),
                Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(directory, "foundry.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static bool IsLocalAbsolutePath(string path) =>
        Path.IsPathFullyQualified(path) && Regex.IsMatch(path, @"^[A-Za-z]:\\");
}

public sealed record FoundryLocalDiscovery(string CliVersion, string Endpoint);
public sealed record FoundryLocalCacheLocation(string Path, bool UserConfigured);
public sealed record FoundryLocalServerStatus(bool Running, int? Pid, DateTimeOffset? StartedAt, IReadOnlyList<string> Endpoints);
