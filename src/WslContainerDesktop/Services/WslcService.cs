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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed class WslcService : IWslcService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProcessRunner _runner;

    public WslcService(ProcessRunner runner)
    {
        _runner = runner;
    }

    // ---- Engine ---------------------------------------------------------

    public Task<CommandResult> GetVersionAsync(CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "version" }, ct);

    public async Task<bool> IsEngineAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _runner.RunAsync(new[] { "version" }, ct).ConfigureAwait(false);
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

        var result = await _runner.RunAsync(args, ct).ConfigureAwait(false);
        return Deserialize<ContainerInfo>(result);
    }

    public Task<CommandResult> StartContainerAsync(string id, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "start", id }, ct);

    public Task<CommandResult> StopContainerAsync(string id, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "stop", id }, ct);

    public async Task<CommandResult> RestartContainerAsync(string id, CancellationToken ct = default)
    {
        var stop = await _runner.RunAsync(new[] { "stop", id }, ct).ConfigureAwait(false);
        // Ignore stop failures (container may already be stopped) and attempt start.
        var start = await _runner.RunAsync(new[] { "start", id }, ct).ConfigureAwait(false);
        if (!start.Success && !stop.Success)
        {
            return stop;
        }

        return start;
    }

    public Task<CommandResult> KillContainerAsync(string id, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "kill", id }, ct);

    public Task<CommandResult> RemoveContainerAsync(string id, bool force = true, CancellationToken ct = default)
    {
        var args = new List<string> { "remove" };
        if (force)
        {
            args.Add("--force");
        }

        args.Add(id);
        return _runner.RunAsync(args, ct);
    }

    public Task<CommandResult> PruneContainersAsync(CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "container", "prune" }, ct);

    public Task<CommandResult> RunContainerAsync(RunContainerOptions options, CancellationToken ct = default) =>
        _runner.RunAsync(options.ToArguments(), ct);

    public Task<CommandResult> GetLogsAsync(string id, int tail = 500, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "logs", "--tail", tail.ToString(), id }, ct);

    public Task<CommandResult> InspectContainerAsync(string id, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "inspect", "--type", "container", id }, ct);

    public async Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new[] { "stats", "--all", "--format", "json" }, ct).ConfigureAwait(false);
        return Deserialize<ContainerStats>(result);
    }

    public async Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new[] { "stats", "--format", "json", id }, ct).ConfigureAwait(false);
        return Deserialize<ContainerStats>(result).FirstOrDefault();
    }

    public void OpenTerminal(string id) =>
        _runner.RunInteractive(new[] { "exec", "-it", id, "/bin/sh", "-c", "clear; (bash || sh)" });

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

        var result = await _runner.RunAsync(new[] { "exec", id, "sh", "-c", probe }, ct).ConfigureAwait(false);
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
        _runner.RunInteractive(new[] { "logs", "-f", "--tail", "200", id });

    // ---- Images ---------------------------------------------------------

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new[] { "images", "--format", "json" }, ct).ConfigureAwait(false);
        return Deserialize<ImageInfo>(result);
    }

    public Task<CommandResult> PullImageAsync(string reference, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "pull", reference }, ct);

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

        return _runner.RunWithStdinAsync(args, password, ct);
    }

    public Task<CommandResult> LogoutRegistryAsync(string server, CancellationToken ct = default)
    {
        var args = new List<string> { "logout" };
        if (!string.IsNullOrWhiteSpace(server))
        {
            args.Add(server);
        }

        return _runner.RunAsync(args, ct);
    }

    public Task<CommandResult> PushImageAsync(string reference, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "push", reference }, ct);

    public async Task<Models.RegistryLoginState> ProbeRegistryLoginAsync(string host, string repository, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Models.RegistryLoginState.Unknown;
        }

        // A HEAD on a nonexistent tag never transfers image data but exercises auth.
        var probeTag = "wslcd-login-probe-0000";
        var reference = $"{host.Trim().TrimEnd('/')}/{repository}:{probeTag}";
        var result = await _runner.RunAsync(new[] { "pull", reference }, ct).ConfigureAwait(false);

        var text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();

        if (text.Contains("unauthorized") || text.Contains("authentication required") ||
            text.Contains("forbidden") || text.Contains("denied"))
        {
            return Models.RegistryLoginState.LoggedOut;
        }

        if (text.Contains("not found") || text.Contains("manifest unknown") ||
            text.Contains("not be found") || result.Success)
        {
            return Models.RegistryLoginState.LoggedIn;
        }

        if (text.Contains("no such host") || text.Contains("timeout") ||
            text.Contains("dial tcp") || text.Contains("connection") ||
            text.Contains("could not resolve") || text.Contains("network"))
        {
            return Models.RegistryLoginState.Unreachable;
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
        return _runner.RunAsync(args, ct);
    }

    public Task<CommandResult> TagImageAsync(string source, string target, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "tag", source, target }, ct);

    public Task<CommandResult> PruneImagesAsync(CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "image", "prune" }, ct);

    public Task<CommandResult> InspectImageAsync(string id, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "inspect", "--type", "image", id }, ct);

    public Task<CommandResult> BuildImageAsync(string contextPath, string tag, string? dockerfile, CancellationToken ct = default)
    {
        var args = new List<string> { "build", "-t", tag };
        if (!string.IsNullOrWhiteSpace(dockerfile))
        {
            args.Add("-f");
            args.Add(dockerfile);
        }

        args.Add(contextPath);
        return _runner.RunAsync(args, ct);
    }

    // ---- Volumes --------------------------------------------------------

    public async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new[] { "volume", "list", "--format", "json" }, ct).ConfigureAwait(false);
        return Deserialize<VolumeInfo>(result);
    }

    public Task<CommandResult> CreateVolumeAsync(string name, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "volume", "create", name }, ct);

    public Task<CommandResult> RemoveVolumeAsync(string name, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "volume", "remove", name }, ct);

    public Task<CommandResult> PruneVolumesAsync(CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "volume", "prune", "--all" }, ct);

    public Task<CommandResult> InspectVolumeAsync(string name, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "volume", "inspect", name }, ct);

    // ---- Networks -------------------------------------------------------

    public async Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync(new[] { "network", "list", "--format", "json" }, ct).ConfigureAwait(false);
        return Deserialize<NetworkInfo>(result);
    }

    public Task<CommandResult> CreateNetworkAsync(string name, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "network", "create", name }, ct);

    public Task<CommandResult> RemoveNetworkAsync(string name, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "network", "remove", name }, ct);

    public Task<CommandResult> PruneNetworksAsync(CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "network", "prune" }, ct);

    public Task<CommandResult> InspectNetworkAsync(string name, CancellationToken ct = default) =>
        _runner.RunAsync(new[] { "network", "inspect", name }, ct);

    // ---- Helpers --------------------------------------------------------

    private static IReadOnlyList<T> Deserialize<T>(CommandResult result)
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
        catch (JsonException)
        {
            return Array.Empty<T>();
        }
    }
}
