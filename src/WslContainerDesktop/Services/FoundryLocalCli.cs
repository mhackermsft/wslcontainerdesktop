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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Read-only standalone CLI adapter. Source:
/// https://learn.microsoft.com/azure/foundry-local/reference/reference-cli
/// Never calls model list (which can download EPs), start, restart, or config.
/// Status requires positive installed-help evidence; no version-locked output schema is assumed.
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
        var executable = _findExecutable();
        if (string.IsNullOrEmpty(executable) || !IsLocalAbsolutePath(executable)
            || !Path.GetFileName(executable).Equals("foundry.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Foundry Local CLI was not found on the absolute Windows PATH. No installation or runtime start was attempted. You can still enter an existing runtime URL manually.");
        progress?.Report("Reading standalone CLI version and help; no model or EP downloads requested…");
        var version = await RunAsync(executable, ["--version"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(version) || version.Length > 256)
            throw new InvalidDataException("Foundry Local returned an unrecognized CLI version.");
        var help = await RunAsync(executable, ["--help"], ct).ConfigureAwait(false);
        // Select from the executable's advertised commands, not its version. Legacy previews
        // used 'service'; a failed status command is never retried with a different command.
        var group = Regex.IsMatch(help, @"(?m)^\s*server(?:\s|$)") ? "server"
            : Regex.IsMatch(help, @"(?m)^\s*service(?:\s|$)") ? "service" : null;
        if (group is null)
            throw new InvalidDataException("This CLI does not advertise a recognized server/service status command. No fallback was attempted.");
        var groupHelp = await RunAsync(executable, [group, "--help"], ct).ConfigureAwait(false);
        if (!Regex.IsMatch(groupHelp, @"(?m)^\s*status(?:\s|$)"))
            throw new InvalidDataException("The installed CLI does not advertise status in its server/service help. Enter the actual endpoint manually; no status syntax was inferred from the version.");
        progress?.Report("Reading the external server's actual endpoint; not starting or adopting it…");
        var status = await RunAsync(executable, [group, "status"], ct).ConfigureAwait(false);
        return new(AiTextSanitizer.Sanitize(version.Trim(), 256), ParseEndpoint(status));
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
