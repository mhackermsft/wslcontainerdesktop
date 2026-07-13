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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class WslcService(ProcessRunner runner, ILogger<WslcService> logger) : IWslcService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ---- Engine ---------------------------------------------------------

    public Task<CommandResult> GetVersionAsync(CancellationToken ct = default) =>
        runner.RunAsync(["version"], ct);

    public async Task<bool> IsEngineAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await runner.RunAsync(["version"], ct).ConfigureAwait(false);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    // ---- Containers -----------------------------------------------------

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(bool all = true, CancellationToken ct = default)
    {
        var args = new List<string> { "list", "--format", "json" };
        if (all)
        {
            args.Add("--all");
        }

        var result = await runner.RunAsync(args, ct).ConfigureAwait(false);
        return Deserialize<ContainerInfo>(result);
    }

    public Task<CommandResult> StartContainerAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["start", id], ct);

    public Task<CommandResult> StopContainerAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["stop", id], ct);

    public async Task<CommandResult> RestartContainerAsync(string id, CancellationToken ct = default)
    {
        var stop = await runner.RunAsync(["stop", id], ct).ConfigureAwait(false);
        // Ignore stop failures (container may already be stopped) and attempt start.
        var start = await runner.RunAsync(["start", id], ct).ConfigureAwait(false);
        if (!start.Success && !stop.Success)
        {
            return stop;
        }

        return start;
    }

    public Task<CommandResult> KillContainerAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["kill", id], ct);

    public Task<CommandResult> RemoveContainerAsync(string id, bool force = true, CancellationToken ct = default)
    {
        var args = new List<string> { "remove" };
        if (force)
        {
            args.Add("--force");
        }

        args.Add(id);
        return runner.RunAsync(args, ct);
    }

    public Task<CommandResult> PruneContainersAsync(CancellationToken ct = default) =>
        runner.RunAsync(["container", "prune"], ct);

    public Task<CommandResult> RunContainerAsync(RunContainerOptions options, CancellationToken ct = default) =>
        runner.RunAsync(options.ToArguments(), ct);

    public Task<CommandResult> GetLogsAsync(string id, int tail = 500, CancellationToken ct = default) =>
        runner.RunAsync(["logs", "--tail", tail.ToString(), id], ct);

    public Task<CommandResult> InspectContainerAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["inspect", "--type", "container", id], ct);

    public Task<CommandResult> ListFilesAsync(string id, string path, CancellationToken ct = default) =>
        ExecShellAsync(id, BuildListFilesScript(path), ct);

    public Task<CommandResult> ReadTextFileAsync(string id, string path, int maxBytes = 65_536, CancellationToken ct = default) =>
        ExecShellAsync(id, BuildReadTextFileScript(path, maxBytes), ct);

    public Task<CommandResult> CopyFromContainerAsync(string id, string containerPath, string hostPath, CancellationToken ct = default) =>
        runner.RunAsync(["cp", $"{id}:{containerPath}", hostPath], ct);

    public Task<CommandResult> CopyToContainerAsync(string id, string hostPath, string containerPath, CancellationToken ct = default) =>
        runner.RunAsync(["cp", hostPath, $"{id}:{containerPath}"], ct);

    public Task<CommandResult> DeletePathAsync(string id, string path, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (normalized == "/")
        {
            return Task.FromResult(new CommandResult
            {
                ExitCode = -1,
                StandardError = "Refusing to delete the container root directory.",
            });
        }

        return ExecShellAsync(id, $"rm -rf -- {WslRootShell.ShellEscape(normalized)}", ct);
    }

    public async Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["stats", "--all", "--format", "json"], ct).ConfigureAwait(false);
        return Deserialize<ContainerStats>(result);
    }

    public async Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["stats", "--format", "json", id], ct).ConfigureAwait(false);
        return Deserialize<ContainerStats>(result).FirstOrDefault();
    }

    public void OpenTerminal(string id) =>
        runner.RunInteractive(["exec", "-it", id, "/bin/sh", "-c", "clear; (bash || sh)"]);

    /// <summary>
    /// Detects whether a running container has GPU passthrough by checking for the WSL
    /// DirectX kernel device (/dev/dxg), which is only mounted when the container was started
    /// with --gpus. Also returns the GPU name when NVIDIA's WSL nvidia-smi is available.
    /// </summary>
    public async Task<(bool HasGpu, string? GpuName)> GetGpuInfoAsync(string id, CancellationToken ct = default)
    {
        var probe =
            "if [ -e /dev/dxg ]; then " +
            "echo HASGPU; " +
            "/usr/lib/wsl/lib/nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -n1; " +
            "else echo NOGPU; fi";

        var result = await runner.RunAsync(["exec", id, "sh", "-c", probe], ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return (false, null);
        }

        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !lines[0].Equals("HASGPU", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        var name = lines.Length > 1 ? lines[1] : null;
        return (true, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    public void FollowLogs(string id) =>
        runner.RunInteractive(["logs", "-f", "--tail", "200", id]);

    // ---- Images ---------------------------------------------------------

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["images", "--format", "json"], ct).ConfigureAwait(false);
        return Deserialize<ImageInfo>(result);
    }

    public Task<CommandResult> PullImageAsync(string reference, CancellationToken ct = default) =>
        runner.RunAsync(["pull", reference], ct);

    public Task<CommandResult> LoginRegistryAsync(string server, string username, string password, CancellationToken ct = default)
    {
        var args = new List<string> { "login" };
        if (!string.IsNullOrWhiteSpace(username))
        {
            args.Add("-u");
            args.Add(username);
        }

        args.Add("--password-stdin");
        if (!string.IsNullOrWhiteSpace(server))
        {
            args.Add(server);
        }

        return runner.RunWithStdinAsync(args, password, ct);
    }

    public Task<CommandResult> LogoutRegistryAsync(string server, CancellationToken ct = default)
    {
        var args = new List<string> { "logout" };
        if (!string.IsNullOrWhiteSpace(server))
        {
            args.Add(server);
        }

        return runner.RunAsync(args, ct);
    }

    public Task<CommandResult> PushImageAsync(string reference, CancellationToken ct = default) =>
        runner.RunAsync(["push", reference], ct);

    public async Task<Models.RegistryLoginState> ProbeRegistryLoginAsync(string host, string repository, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Models.RegistryLoginState.Unknown;
        }

        // Pulling a nonexistent tag never transfers image data but forces the engine to reach the
        // registry's manifest endpoint, which exercises the stored credentials.
        var probeTag = "wslcd-login-probe-0000";
        var reference = $"{host.Trim().TrimEnd('/')}/{repository}:{probeTag}";
        var result = await runner.RunAsync(["pull", reference], ct).ConfigureAwait(false);

        // Prefer stable, locale-independent signals over the CLI's prose:
        //   1. the process exit code,
        //   2. wslc's own structured "Error code: WSLC_E_*" token,
        //   3. registry/OCI protocol codes (unauthorized/denied/manifest unknown) and Go net
        //      errors (dial tcp/no such host), which the engine passes through verbatim from the
        //      wire and does not localize.
        // Order matters: reachability is checked before auth (a network failure must not read as
        // "logged out"), and the strong 401 signal is checked before the weaker/ambiguous 403.
        // Anything we cannot classify confidently returns Unknown rather than a wrong answer.

        if (result.Success)
        {
            return Models.RegistryLoginState.LoggedIn;
        }

        var text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();

        // Reachability first: these are Go standard-library network errors, emitted in a stable
        // (non-localized) form regardless of the user's UI language.
        if (text.Contains("no such host") || text.Contains("dial tcp") ||
            text.Contains("could not resolve") || text.Contains("server misbehaving") ||
            text.Contains("connection refused") || text.Contains("no route to host") ||
            text.Contains("i/o timeout") || text.Contains("timeout") ||
            text.Contains("tls handshake") || text.Contains("x509") || text.Contains("certificate"))
        {
            return Models.RegistryLoginState.Unreachable;
        }

        // Strong auth-required signal (HTTP 401 / OCI "unauthorized"): the registry demanded
        // credentials we did not (successfully) supply.
        if (text.Contains("unauthorized") || text.Contains("authentication required") ||
            text.Contains(" 401") || text.Contains("http 401"))
        {
            return Models.RegistryLoginState.LoggedOut;
        }

        // Reached the registry and got far enough to conclude the tag/repo is simply absent, which
        // means authentication was accepted. wslc's own WSLC_E_IMAGE_NOT_FOUND is the most reliable
        // token here; the OCI "manifest unknown"/"name unknown" codes are equivalent fallbacks.
        if (text.Contains("wslc_e_image_not_found") || text.Contains("manifest unknown") ||
            text.Contains("name unknown") || text.Contains("not found") || text.Contains("not be found"))
        {
            return Models.RegistryLoginState.LoggedIn;
        }

        // Weaker auth signal (HTTP 403 / OCI "denied") checked last: on some registries this means
        // "authenticated but forbidden" and on others "anonymous access to a private resource".
        if (text.Contains("denied") || text.Contains("forbidden") || text.Contains(" 403"))
        {
            return Models.RegistryLoginState.LoggedOut;
        }

        return Models.RegistryLoginState.Unknown;
    }

    public Task<CommandResult> RemoveImageAsync(string id, bool force = true, CancellationToken ct = default)
    {
        var args = new List<string> { "rmi" };
        if (force)
        {
            args.Add("--force");
        }

        args.Add(id);
        return runner.RunAsync(args, ct);
    }

    public Task<CommandResult> TagImageAsync(string source, string target, CancellationToken ct = default) =>
        runner.RunAsync(["tag", source, target], ct);

    public Task<CommandResult> PruneImagesAsync(CancellationToken ct = default) =>
        runner.RunAsync(["image", "prune"], ct);

    public Task<CommandResult> InspectImageAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["inspect", "--type", "image", id], ct);

    public Task<CommandResult> BuildImageAsync(string contextPath, string tag, string? dockerfile, CancellationToken ct = default)
    {
        var args = new List<string> { "build", "-t", tag };
        if (!string.IsNullOrWhiteSpace(dockerfile))
        {
            args.Add("-f");
            args.Add(dockerfile);
        }

        args.Add(contextPath);
        return runner.RunAsync(args, ct);
    }

    // ---- Volumes --------------------------------------------------------

    public async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["volume", "list", "--format", "json"], ct).ConfigureAwait(false);
        return Deserialize<VolumeInfo>(result);
    }

    public Task<CommandResult> CreateVolumeAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["volume", "create", name], ct);

    public Task<CommandResult> RemoveVolumeAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["volume", "remove", name], ct);

    public Task<CommandResult> PruneVolumesAsync(CancellationToken ct = default) =>
        runner.RunAsync(["volume", "prune", "--all"], ct);

    public Task<CommandResult> InspectVolumeAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["volume", "inspect", name], ct);

    // ---- Networks -------------------------------------------------------

    public async Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["network", "list", "--format", "json"], ct).ConfigureAwait(false);
        return Deserialize<NetworkInfo>(result);
    }

    public Task<CommandResult> CreateNetworkAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["network", "create", name], ct);

    public Task<CommandResult> RemoveNetworkAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["network", "remove", name], ct);

    public Task<CommandResult> PruneNetworksAsync(CancellationToken ct = default) =>
        runner.RunAsync(["network", "prune"], ct);

    public Task<CommandResult> InspectNetworkAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["network", "inspect", name], ct);

    // ---- Helpers --------------------------------------------------------

    private Task<CommandResult> ExecShellAsync(string id, string script, CancellationToken ct = default) =>
        runner.RunAsync(["exec", id, "sh", "-c", script], ct);

    private static string BuildListFilesScript(string path)
    {
        var escapedPath = WslRootShell.ShellEscape(string.IsNullOrWhiteSpace(path) ? "/" : path);
        return "target=" + escapedPath + "; " +
               "if [ ! -d \"$target\" ]; then echo __WSLCD_NOT_DIR__; exit 3; fi; " +
               "cd \"$target\" || exit 4; " +
               "printf 'PWD\\t%s\\n' \"$PWD\"; " +
               // Match regular entries, ".foo" entries, and "..foo" entries while excluding "." and "..".
               "for entry in .[!.]* ..?* *; do " +
               "[ -e \"$entry\" ] || continue; " +
               "kind=f; " +
               "if [ -d \"$entry\" ]; then kind=d; elif [ -L \"$entry\" ]; then kind=l; fi; " +
               // Use literal tab characters here rather than "\t" escapes: BusyBox stat (Alpine)
               // does not interpret backslash escapes in its format string and would emit them
               // verbatim, breaking the tab-delimited parser.
               "stat -c \"ENTRY\t${kind}\t%A\t%U\t%G\t%s\t%Y\t%n\" -- \"$entry\"; " +
               "done";
    }

    private static string BuildReadTextFileScript(string path, int maxBytes)
    {
        var escapedPath = WslRootShell.ShellEscape(path);
        // Keep previews reasonably small for the inline UI while preventing an empty/unsafe limit.
        var safeMaxBytes = Math.Clamp(maxBytes, 1_024, 1_048_576);
        return "target=" + escapedPath + "; " +
               "if [ ! -f \"$target\" ]; then echo __WSLCD_NOT_FILE__; exit 3; fi; " +
               "size=$(wc -c < \"$target\" 2>/dev/null || echo 0); " +
               $"if [ \"$size\" -gt {safeMaxBytes} ]; then echo __WSLCD_TOO_LARGE__:$size; exit 4; fi; " +
               "cat -- \"$target\"";
    }

    private IReadOnlyList<T> Deserialize<T>(CommandResult result)
    {
        if (!result.Success)
        {
            return Array.Empty<T>();
        }

        var json = result.StandardOutput.Trim();
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<T>();
        }

        // Strip a leading UTF-8 BOM if present.
        if (json[0] == '\uFEFF')
        {
            json = json[1..];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<T>>(json, JsonOptions);
            return items ?? new List<T>();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse wslc JSON output as {Type}. Raw output: {Output}", typeof(T).Name, json);
            return Array.Empty<T>();
        }
    }
}
