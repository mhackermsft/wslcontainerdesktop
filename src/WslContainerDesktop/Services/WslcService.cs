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
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Implements the app's main container-engine facade by translating service calls into <c>wslc.exe</c> commands.
/// </summary>
/// <remarks>
/// This class is the boundary between the rest of the app and the external container CLI. It keeps command construction in one place, normalizes JSON and text output into models, and uses collaborators for capabilities, policy checks, file transfer, and restart suppression.
/// </remarks>
public sealed class WslcService(
    ProcessRunner runner,
    ILogger<WslcService> logger,
    IWslcCapabilitiesService capabilities,
    ISettingsService settings,
    RestartSuppressionState suppression,
    IWslPolicyService? policy = null,
    Func<string>? fileTransferStagingRoot = null) : IWslcService
{
    private readonly IWslcCapabilitiesService _capabilities = capabilities;
    private readonly IWslPolicyService _policy = policy ?? AllowAllWslPolicyService.Instance;
    private readonly ContainerPortResolver _containerPorts = new();
    /// <summary>
    /// Remembers requested health-check options until the created container can be inspected.
    /// </summary>
    private sealed record PendingHealth(RunContainerOptions Options, string ExecutablePath);
    private readonly ConcurrentDictionary<string, PendingHealth> _pendingHealth = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private const int PendingHealthLimit = 256;

    private WslcFileTransfer FileTransfer => new(
        ProcessRunner.RunAtPathAsync, ProcessRunner.RunCopyWithInputFileAtPathAsync, () => settings.WslcPath,
        () => fileTransferStagingRoot?.Invoke() ??
            Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "file-transfer"));

    /// <summary>
    /// Provides the default policy when no registry-based WSL policy service was registered.
    /// </summary>
    private sealed class AllowAllWslPolicyService : IWslPolicyService
    {
        /// <summary>
        /// Gets instance for callers in the service or view-model layer.
        /// </summary>
        public static readonly AllowAllWslPolicyService Instance = new();

        /// <summary>
        /// Gets policy information for callers in the service or view-model layer.
        /// </summary>
        public WslPolicySnapshot GetPolicy() =>
            new(true, true, new WslRegistryAllowlist(WslRegistryAllowlistState.Unrestricted, []));
    }

    // ---- Engine ---------------------------------------------------------

    /// <summary>
    /// Gets version information for callers in the service or view-model layer.
    /// </summary>
    public Task<CommandResult> GetVersionAsync(CancellationToken ct = default) =>
        runner.RunAsync(["version"], ct);

    /// <summary>
    /// Gets system information for callers in the service or view-model layer.
    /// </summary>
    public async Task<WslcSystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["system", "info", "--format", "json"], ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"System info failed: {result.ErrorText}");
        }

        try
        {
            return JsonSerializer.Deserialize<WslcSystemInfo>(result.StandardOutput, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new WslcSystemInfo();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse wslc system info JSON.");
            throw;
        }
    }

    /// <summary>
    /// Gets event information for callers in the service or view-model layer.
    /// </summary>
    public async Task<IReadOnlyList<EngineEvent>> GetEventsAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default)
    {
        var result = await runner.RunAsync([
            "events",
            "--since",
            since.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--until",
            until.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ], ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Event query failed: {result.ErrorText}");
        }

        return WslcEventParser.ParseLines(result.StandardOutput, logger);
    }

    /// <summary>
    /// Opens settings for the user.
    /// </summary>
    public Task<CommandResult> OpenSettingsAsync(CancellationToken ct = default) =>
        runner.RunAsync(["settings"], ct);

    /// <summary>
    /// Terminates the current <c>wslc</c> session. This is the only way to release the per-session
    /// bind-mount slots that <c>wslc</c> leaks (it caps distinct host bind sources at 15 and never
    /// frees them, even after containers are removed — a documented wslc limitation). Terminating the
    /// session also stops all running containers, so callers must confirm with the user first.
    /// </summary>
    public Task<CommandResult> RestartSessionAsync(CancellationToken ct = default) =>
        runner.RunAsync(["system", "session", "terminate"], ct);

    /// <summary>
    /// Verifies bind mount before a later operation relies on it.
    /// </summary>
    public async Task<BindMountProbeResult> VerifyBindMountAsync(string hostSource, CancellationToken ct = default)
    {
        // Bind the source into a throwaway busybox and report, from inside the VM, whether it is a
        // regular file, a directory (a raced/poisoned bind), or the probe could not run at all. When
        // the probe container itself fails (image unavailable, engine error, or the mount create
        // failing outright) we report ProbeUnavailable so the caller can fail open — the real run then
        // surfaces any genuine mount error through its own error handling rather than being masked as
        // a mount-limit failure.
        const string ProbeImage = "busybox:1.36";
        if (ValidateImagePolicy(ProbeImage) is not null)
        {
            // The registry allowlist forbids the probe image; fail open exactly as for an unavailable image.
            return BindMountProbeResult.ProbeUnavailable;
        }

        var args = new List<string>
        {
            "run", "--rm",
            "-v", $"{hostSource}:/wcd-probe:ro",
            ProbeImage,
            "sh", "-c",
            "if [ -f /wcd-probe ]; then echo __WCD_FILE__; " +
            "elif [ -d /wcd-probe ]; then echo __WCD_DIR__; fi",
        };

        try
        {
            var result = await runner.RunAsync(args, ct).ConfigureAwait(false);
            if (result.StandardOutput.Contains("__WCD_FILE__", StringComparison.Ordinal))
            {
                return BindMountProbeResult.MountsAsFile;
            }

            if (result.StandardOutput.Contains("__WCD_DIR__", StringComparison.Ordinal))
            {
                return BindMountProbeResult.MountsAsDirectory;
            }

            return BindMountProbeResult.ProbeUnavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BindMountProbeResult.ProbeUnavailable;
        }
    }

    /// <summary>
    /// Gets whether engine available for the current app or engine state.
    /// </summary>
    public async Task<bool> IsEngineAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            if (_policy.GetPolicy().WslContainersDisabled)
            {
                return false;
            }

            var result = await runner.RunAsync(["version"], ct).ConfigureAwait(false);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    // ---- Containers -----------------------------------------------------

    /// <summary>
    /// Lists container service resources used by WSL Container Desktop.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(bool all = true, CancellationToken ct = default, bool includeSize = false)
    {
        var args = new List<string> { "list", "--format", "json" };
        if (all)
        {
            args.Add("--all");
        }

        if (includeSize)
        {
            args.Add("--size");
        }

        var result = await runner.RunAsync(args, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            logger.LogWarning("Container list failed with exit code {ExitCode}.", result.ExitCode);
            throw new InvalidOperationException($"Container list failed (exit {result.ExitCode}).");
        }

        IReadOnlyList<ContainerInfo> containers;
        try
        {
            // All-or-nothing: a partial inventory could trigger destructive re-adoption/reconciliation.
            containers = WslcJsonParser.ParseContainers(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse container inventory; no partial list will be returned.");
            throw;
        }
        await _containerPorts.ResolveAsync(containers, all, (id, token) => InspectContainerAsync(id, token),
            (id, message) => logger.LogWarning("Container {Id} ports remain unknown: {Detail}", id, message), ct)
            .ConfigureAwait(false);
        return containers;
    }

    /// <summary>
    /// Starts container work requested by the UI or a supervisor.
    /// </summary>
    public async Task<CommandResult> StartContainerAsync(string id, CancellationToken ct = default, bool explicitStart = true)
    {
        var resume = explicitStart ? await CaptureStartIntentAsync(id, ct).ConfigureAwait(false) : null;
        var pending = FindPendingHealth(id);
        if (pending.Value is not null)
        {
            var current = await _capabilities.GetAsync(ct).ConfigureAwait(false);
            if (!string.Equals(current.ExecutablePath, pending.Value.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pending health configuration belongs to a different WSLC executable. Switch back before starting this container.");
            if (pending.Value.Options.Name == id)
            {
                var inspect = await InspectContainerAsync(id, ct).ConfigureAwait(false);
                if (!inspect.Success || NativeHealthParser.ContainerId(inspect.StandardOutput) != pending.Key)
                    throw new InvalidOperationException("The container name no longer identifies the container awaiting health enrollment. No start was attempted.");
            }
        }
        var result = await runner.RunAsync(["start", id], ct).ConfigureAwait(false);
        if (result.Success && pending.Value is not null)
        {
            RegisterHealth(pending.Value.Options);
            _pendingHealth.TryRemove(pending.Key, out _);
        }
        if (resume is { } token)
            suppression.CompleteExplicitStart(token, result.Success);
        return result;
    }

    private async Task<RestartSuppressionState.ResumeToken?> CaptureStartIntentAsync(string id, CancellationToken ct)
    {
        var version = suppression.Version;
        if (!suppression.HasSuppressedContainers)
            return null;
        var inspect = await InspectContainerAsync(id, ct).ConfigureAwait(false);
        if (!inspect.Success)
            throw new InvalidOperationException($"Cannot resolve container identity before explicitly starting '{id}': {inspect.ErrorText}");
        using var document = JsonDocument.Parse(inspect.StandardOutput);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
            root = root[0];
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Name", out var name) ||
            name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new InvalidOperationException("Container inspect did not provide a name; restart suppression cannot be safely resumed.");
        return suppression.CaptureExplicitStart(name.GetString()!, version);
    }

    /// <summary>
    /// Stops container work requested by the UI or a supervisor.
    /// </summary>
    public Task<CommandResult> StopContainerAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["stop", id], ct);

    /// <summary>
    /// Stops container work requested by the UI or a supervisor.
    /// </summary>
    public Task<CommandResult> StopContainerAsync(string id, int? timeSeconds, string? signal, CancellationToken ct = default)
    {
        var args = new List<string> { "stop" };
        if (timeSeconds is int t && t >= 0)
        {
            args.Add("-t");
            args.Add(t.ToString());
        }

        if (!string.IsNullOrWhiteSpace(signal))
        {
            args.Add("-s");
            args.Add(signal.Trim());
        }

        args.Add(id);
        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Restarts container through the service layer.
    /// </summary>
    public Task<CommandResult> RestartContainerAsync(string id, CancellationToken ct = default) =>
        RestartContainerAsync(id, null, null, ct);

    /// <summary>
    /// Restarts container through the service layer.
    /// </summary>
    public async Task<CommandResult> RestartContainerAsync(string id, int? timeSeconds, string? signal, CancellationToken ct = default)
    {
        var resume = await CaptureStartIntentAsync(id, ct).ConfigureAwait(false);
        var args = new List<string> { "restart" };
        if (timeSeconds is int t && t >= 0)
        {
            args.Add("-t");
            args.Add(t.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(signal))
        {
            args.Add("-s");
            args.Add(signal.Trim());
        }

        args.Add(id);
        var result = await runner.RunAsync(args, ct).ConfigureAwait(false);
        if (resume is { } token)
            suppression.CompleteExplicitStart(token, result.Success);
        return result;
    }

    /// <summary>
    /// Force-stops container through the service layer.
    /// </summary>
    public Task<CommandResult> KillContainerAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["kill", id], ct);

    /// <summary>
    /// Removes a container from persisted state or the engine.
    /// </summary>
    public async Task<CommandResult> RemoveContainerAsync(string id, bool force = true, CancellationToken ct = default,
        bool removeAnonymousVolumes = false)
    {
        var args = new List<string> { "remove" };
        if (force)
        {
            args.Add("--force");
        }

        if (removeAnonymousVolumes)
        {
            args.Add("--volumes");
        }

        args.Add(id);
        var result = await runner.RunNonInteractiveAsync(args, ct).ConfigureAwait(false);
        if (result.Success)
            foreach (var pending in _pendingHealth.Where(p => MatchesPendingHealth(p.Key, p.Value, id)))
                _pendingHealth.TryRemove(pending.Key, out _);
        return result;
    }

    /// <summary>
    /// Prunes container service resources used by WSL Container Desktop.
    /// </summary>
    public Task<CommandResult> PruneContainersAsync(CancellationToken ct = default) =>
        PruneAsync(WslcPruneTarget.Containers, ct);

    /// <summary>
    /// Runs prune non-interactively with <c>--force</c> after the app's own confirmation.
    /// </summary>
    private Task<CommandResult> PruneAsync(WslcPruneTarget target, CancellationToken ct) =>
        runner.RunNonInteractiveAsync(WslcPruneCommand.Select(target).Arguments!, ct);

    /// <summary>
    /// Runs container through the service layer.
    /// </summary>
    public async Task<CommandResult> RunContainerAsync(RunContainerOptions options, CancellationToken ct = default,
        long maximumStopVersion = long.MaxValue)
    {
        if (options.WaitsForTerminalInput())
            return new CommandResult { ExitCode = -1, StandardError = RunContainerOptions.ForegroundInteractiveError };
        if (await ValidateRunImagePolicyAsync(options.Image, ct).ConfigureAwait(false) is { } policyError)
            return policyError;
        var resume = string.IsNullOrWhiteSpace(options.Name)
            ? (RestartSuppressionState.ResumeToken?)null : suppression.CaptureExplicitStart(options.Name, maximumStopVersion);
        var selection = options.Health is null ? new NativeHealthSelection(true, [])
            : NativeHealthPolicy.Select(options.Health, await _capabilities.GetAsync(ct).ConfigureAwait(false));
        var effective = options.Clone();
        if (effective.Health is not null && string.IsNullOrWhiteSpace(effective.Name))
            effective.Name = "wcd-" + Guid.NewGuid().ToString("N")[..12];
        var result = await runner.RunAsync(effective.ToArguments(selection.Arguments), ct).ConfigureAwait(false);
        if (!result.Success)
            return result;
        if (resume is { } token)
            suppression.CompleteExplicitStart(token, true);
        RegisterHealth(effective);
        if (selection.Diagnostic is null)
            return result;
        logger.LogWarning("{Diagnostic}", selection.Diagnostic);
        return new CommandResult
        {
            ExitCode = result.ExitCode, StandardOutput = result.StandardOutput,
            StandardError = result.StandardError + Environment.NewLine + selection.Diagnostic,
        };
    }

    private void RegisterHealth(RunContainerOptions options)
    {
        if (options.Health is null || string.IsNullOrWhiteSpace(options.Name))
            return;
        var previous = settings.HealthChecks.FirstOrDefault(h => h.ContainerName == options.Name);
        var config = previous?.Clone() ?? new HealthCheckConfig { ContainerName = options.Name, MaxRestarts = 0 };
        config.DesiredHealth = options.Health.Clone();
        config.Kind = HealthProbeKind.Command;
        config.Enabled = true;
        config.Command = options.Health.Test.Count == 2 && options.Health.Test[0] == "CMD-SHELL"
            ? options.Health.Test[1] : string.Empty;
        settings.HealthChecks = settings.HealthChecks.Where(h => h.ContainerName != options.Name).Append(config).ToList();
        settings.Save();
    }

    /// <summary>
    /// Creates container values used by the service layer.
    /// </summary>
    public async Task<CommandResult> CreateContainerAsync(RunContainerOptions options, CancellationToken ct = default)
    {
        await _createGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CreateContainerCoreAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            _createGate.Release();
        }
    }

    private async Task<CommandResult> CreateContainerCoreAsync(RunContainerOptions options, CancellationToken ct)
    {
        if (await ValidateRunImagePolicyAsync(options.Image, ct).ConfigureAwait(false) is { } policyError)
            return policyError;
        var snapshot = options.Health is null ? null : await _capabilities.GetAsync(ct).ConfigureAwait(false);
        var selection = snapshot is null ? new NativeHealthSelection(true, [])
            : NativeHealthPolicy.Select(options.Health, snapshot, forCreate: true);
        if (options.Health is not null && _pendingHealth.Count >= PendingHealthLimit)
            throw new InvalidOperationException("Too many created containers await health enrollment. Start or remove those containers before creating another health-checked container.");
        var effective = options.Clone();
        if (effective.Health is not null && string.IsNullOrWhiteSpace(effective.Name))
            effective.Name = "wcd-" + Guid.NewGuid().ToString("N")[..12];
        var result = await runner.RunAsync(effective.ToCreateArguments(selection.Arguments), ct).ConfigureAwait(false);
        if (!result.Success)
            return result;
        // Creation alone is not enrollment: multi-network orchestration may still fail and remove it.
        if (effective.Health is not null)
        {
            var id = result.StandardOutput.Trim();
            if (id.Length != 64 || !id.All(Uri.IsHexDigit))
                throw new InvalidOperationException("Container creation succeeded but returned no unambiguous ID; health enrollment cannot be deferred safely. Inspect the created container before retrying.");
            _pendingHealth[id] = new(effective, snapshot!.ExecutablePath);
        }
        if (selection.Diagnostic is null)
            return result;
        logger.LogWarning("{Diagnostic}", selection.Diagnostic);
        return new CommandResult
        {
            ExitCode = result.ExitCode, StandardOutput = result.StandardOutput,
            StandardError = result.StandardError + Environment.NewLine + selection.Diagnostic,
        };
    }

    private static bool MatchesPendingHealth(string key, PendingHealth pending, string id) =>
        key == id || pending.Options.Name == id || id.Length >= 12 && key.StartsWith(id, StringComparison.Ordinal);

    private KeyValuePair<string, PendingHealth> FindPendingHealth(string id)
    {
        var matches = _pendingHealth.Where(p => MatchesPendingHealth(p.Key, p.Value, id)).Take(2).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("Container identifier is ambiguous for pending health enrollment. Use the full container ID.");
        return matches.FirstOrDefault();
    }

    /// <summary>
    /// Executes an app-owned health command inside a container.
    /// </summary>
    public Task<CommandResult> ExecHealthAsync(string id, NativeHealthOptions health, CancellationToken ct = default)
    {
        return runner.RunAsync(NativeHealthPolicy.ExecArguments(id, health), ct);
    }

    /// <summary>
    /// Gets log information for callers in the service or view-model layer.
    /// </summary>
    public Task<CommandResult> GetLogsAsync(string id, int tail = 500, CancellationToken ct = default, bool details = false,
        bool timestamps = false, DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        var args = BuildLogsArguments(id, tail, follow: false, details, timestamps, since, until);
        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Inspects container through the service layer.
    /// </summary>
    public Task<CommandResult> InspectContainerAsync(string id, CancellationToken ct = default, bool includeSize = false)
    {
        var args = new List<string> { "inspect", "--type", "container" };
        if (includeSize)
        {
            args.Add("--size");
        }

        args.Add(id);
        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Lists files resources for callers that should not invoke <c>wslc</c> directly.
    /// </summary>
    public Task<CommandResult> ListFilesAsync(string id, string path, CancellationToken ct = default) =>
        ExecShellAsync(id, BuildListFilesScript(path), ct);

    /// <summary>
    /// Reads text file through the service layer.
    /// </summary>
    public Task<CommandResult> ReadTextFileAsync(string id, string path, int maxBytes = 65_536, CancellationToken ct = default) =>
        ExecShellAsync(id, BuildReadTextFileScript(path, maxBytes), ct);

    /// <summary>
    /// Copies from container through the service layer.
    /// </summary>
    public Task<CommandResult> CopyFromContainerAsync(string id, string containerPath, string hostPath, CancellationToken ct = default,
        bool followSymlinks = false) =>
        FileTransfer.CopyFromAsync(id, containerPath, hostPath, ct, followSymlinks);

    /// <summary>
    /// Copies to container through the service layer.
    /// </summary>
    public Task<CommandResult> CopyToContainerAsync(string id, string hostPath, string containerPath, CancellationToken ct = default) =>
        FileTransfer.CopyToAsync(id, hostPath, containerPath, ct);

    /// <summary>
    /// Deletes a path from persisted state or the engine.
    /// </summary>
    public Task<CommandResult> DeletePathAsync(string id, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || path == "/" || !path.StartsWith('/') || ContainsPathTraversal(path))
        {
            return Task.FromResult(new CommandResult
            {
                ExitCode = -1,
                StandardError = "Refusing to delete: path must be an absolute non-root path without traversal segments.",
            });
        }

        return ExecShellAsync(id, $"rm -rf -- {WslRootShell.ShellEscape(path)}", ct);
    }

    /// <summary>
    /// Renames a file or directory inside a container through the service layer.
    /// </summary>
    public Task<CommandResult> RenamePathAsync(string id, string oldPath, string newPath, CancellationToken ct = default)
    {
        if (ContainsPathTraversal(oldPath) || ContainsPathTraversal(newPath))
        {
            return Task.FromResult(new CommandResult
            {
                ExitCode = -1,
                StandardError = "Path must not contain '.' or '..' segments.",
            });
        }

        return ExecShellAsync(id,
            $"mv -- {WslRootShell.ShellEscape(oldPath)} {WslRootShell.ShellEscape(newPath)}", ct);
    }

    /// <summary>
    /// Creates directory values used by the service layer.
    /// </summary>
    public Task<CommandResult> CreateDirectoryAsync(string id, string path, CancellationToken ct = default)
    {
        if (ContainsPathTraversal(path))
        {
            return Task.FromResult(new CommandResult
            {
                ExitCode = -1,
                StandardError = "Path must not contain '.' or '..' segments.",
            });
        }

        return ExecShellAsync(id, $"mkdir -p -- {WslRootShell.ShellEscape(path)}", ct);
    }

    // Container paths always use forward slashes (POSIX). Backslash is a valid character
    // in Linux filenames and is NOT a path separator, so we deliberately do not normalize it.
    private static bool ContainsPathTraversal(string path) =>
        path.Split('/').Any(s => s == "." || s == "..");

    /// <summary>
    /// Gets statistics information for callers in the service or view-model layer.
    /// </summary>
    public async Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken ct = default)
    {
        // Deliberately omit --all: like `docker stats`, that flag would also report stopped
        // containers (with zero usage), which is not what the dashboard's "live" list should show.
        var result = await runner.RunAsync(["stats", "--format", "json"], ct).ConfigureAwait(false);
        return Deserialize<ContainerStats>(result);
    }

    /// <summary>
    /// Gets statistics information for callers in the service or view-model layer.
    /// </summary>
    public async Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["stats", "--format", "json", id], ct).ConfigureAwait(false);
        return Deserialize<ContainerStats>(result).FirstOrDefault();
    }

    /// <summary>
    /// Opens terminal for the user.
    /// </summary>
    public void OpenTerminal(string id) =>
        runner.RunInteractive(["exec", "-it", id, "/bin/sh", "-c", "clear; (bash || sh)"]);

    /// <summary>
    /// Executes a command inside a container through <c>wslc</c>.
    /// </summary>
    public Task<CommandResult> ExecAsync(string id, string command, CancellationToken ct = default) =>
        runner.RunAsync(["exec", id, "sh", "-c", command], ct);

    // Walks a filesystem staying on the root device (-xdev skips /proc, /sys, /dev, tmpfs, and
    // bind-mounted volumes automatically) and emits one "mode|size|mtime|path" line per entry.
    // %f/%s/%Y are numeric so a maxsplit of 4 keeps a path containing '|' intact.
    private const string DiffWalkScript =
        "find / -xdev -exec stat -c '%f|%s|%Y|%n' {} + 2>/dev/null";

    /// <summary>
    /// Reads filesystem changes from a container through <c>wslc</c>.
    /// </summary>
    public async Task<IReadOnlyList<ContainerFsChange>> DiffContainerAsync(
        string id, string image, CancellationToken ct = default)
    {
        // wslc exposes no `diff` primitive, so the changeset is emulated: walk the running
        // container's rootfs and compare it against a pristine baseline walk of the same image.
        //
        // NOTE: `find … -exec stat …` exits non-zero when stat fails for any single entry, which
        // happens routinely on a live rootfs when a transient file (e.g. under /run or /tmp)
        // vanishes mid-walk. That's expected and stdout is still complete, so success is judged by
        // whether the walk produced output rather than by the process exit code.
        var containerResult = await ExecShellAsync(id, DiffWalkScript, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(containerResult.StandardOutput))
        {
            throw new InvalidOperationException(
                "Could not read the container filesystem. The container must be running and include a POSIX shell.");
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            throw new InvalidOperationException("The container's image is unknown, so no baseline is available.");
        }

        var baselineResult = await runner
            .RunAsync(["run", "--rm", "--entrypoint", "sh", image, "-c", DiffWalkScript], ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baselineResult.StandardOutput))
        {
            throw new InvalidOperationException(
                $"Could not build a filesystem baseline from image '{image}'. " +
                "The image may not include a shell (e.g. distroless or scratch).");
        }

        // Exclude the container's mount targets (and the engine-injected /.dockerenv) so
        // bind-mounted files (/etc/hosts, /etc/resolv.conf, …), volumes, and pseudo filesystems
        // aren't reported as spurious changes — matching `docker diff` behavior.
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "/.dockerenv" };
        var mountResult = await ExecShellAsync(id, "awk '{print $2}' /proc/mounts 2>/dev/null", ct)
            .ConfigureAwait(false);
        if (mountResult.Success)
        {
            foreach (var line in mountResult.StandardOutput.Split('\n'))
            {
                var target = line.Trim();
                if (target.Length > 0)
                {
                    excluded.Add(target);
                }
            }
        }

        var container = ParseDiffWalk(containerResult.StandardOutput);
        var baseline = ParseDiffWalk(baselineResult.StandardOutput);

        var changes = new List<ContainerFsChange>();
        foreach (var (path, meta) in container)
        {
            if (excluded.Contains(path))
            {
                continue;
            }

            if (!baseline.TryGetValue(path, out var baseMeta))
            {
                changes.Add(new ContainerFsChange { Kind = FsChangeKind.Added, Path = path });
            }
            else if (!string.Equals(meta, baseMeta, StringComparison.Ordinal))
            {
                changes.Add(new ContainerFsChange { Kind = FsChangeKind.Changed, Path = path });
            }
        }

        foreach (var path in baseline.Keys)
        {
            if (!excluded.Contains(path) && !container.ContainsKey(path))
            {
                changes.Add(new ContainerFsChange { Kind = FsChangeKind.Deleted, Path = path });
            }
        }

        changes.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return changes;
    }

    private static Dictionary<string, string> ParseDiffWalk(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // "mode|size|mtime|path"; keep the metadata triple as the comparison key.
            var parts = line.Split('|', 4);
            if (parts.Length != 4)
            {
                continue;
            }

            map[parts[3]] = string.Concat(parts[0], "|", parts[1], "|", parts[2]);
        }

        return map;
    }

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

    /// <summary>
    /// Starts following container logs through the service layer.
    /// </summary>
    public void FollowLogs(string id, bool details = false, bool timestamps = false) =>
        runner.RunInteractive(BuildLogsArguments(id, 200, follow: true, details, timestamps));

    /// <summary>
    /// Attaches an interactive session to a running container.
    /// </summary>
    public void AttachContainer(string id) =>
        runner.RunInteractive(["attach", id]);

    private static List<string> BuildLogsArguments(string id, int tail, bool follow, bool details, bool timestamps,
        DateTimeOffset? since = null, DateTimeOffset? until = null)
    {
        var args = new List<string> { "logs" };
        if (details)
        {
            args.Add("--details");
        }

        if (timestamps)
        {
            args.Add("--timestamps");
        }

        if (follow)
        {
            args.Add("--follow");
        }

        args.Add("--tail");
        args.Add(tail.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (since is DateTimeOffset sinceValue)
        {
            args.Add("--since");
            args.Add(sinceValue.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (until is DateTimeOffset untilValue)
        {
            args.Add("--until");
            args.Add(untilValue.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(id);
        return args;
    }

    // ---- Images ---------------------------------------------------------

    /// <summary>
    /// Lists image service resources used by WSL Container Desktop.
    /// </summary>
    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default, bool showAll = false)
    {
        var args = new List<string> { "images", "--digests", "--format", "json" };
        if (showAll)
        {
            args.Add("--all");
        }

        var result = await runner.RunAsync(args, ct).ConfigureAwait(false);
        return WslcJsonParser.ParseImages(result);
    }

    /// <summary>
    /// Pulls image through the service layer.
    /// </summary>
    public Task<CommandResult> PullImageAsync(string reference, CancellationToken ct = default, bool allTags = false)
    {
        // wslc rejects a tag or digest together with --all-tags ("tag can't be used with --all-tags").
        var target = allTags ? StripTag(reference) : reference;
        return ValidateImagePolicy(target) is { } policyError
            ? Task.FromResult(policyError)
            : runner.RunAsync(BuildPullPushArguments("pull", target, allTags), ct);
    }

    /// <summary>
    /// Pulls image through the service layer.
    /// </summary>
    public Task<CommandResult> PullImageAsync(string reference, Action<string> onLine, CancellationToken ct = default, bool allTags = false)
    {
        var target = allTags ? StripTag(reference) : reference;
        return ValidateImagePolicy(target) is { } policyError
            ? Task.FromResult(policyError)
            : runner.RunStreamingAsync(BuildPullPushArguments("pull", target, allTags), onLine, ct);
    }

    /// <summary>
    /// Logs in to a registry without exposing credentials on the command line.
    /// </summary>
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

    /// <summary>
    /// Logs out of a container registry through the service layer.
    /// </summary>
    public Task<CommandResult> LogoutRegistryAsync(string server, CancellationToken ct = default)
    {
        var args = new List<string> { "logout" };
        if (!string.IsNullOrWhiteSpace(server))
        {
            args.Add(server);
        }

        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Pushes image through the service layer.
    /// </summary>
    public Task<CommandResult> PushImageAsync(string reference, CancellationToken ct = default, bool allTags = false)
    {
        var target = allTags ? StripTag(reference) : reference;
        return ValidateImagePolicy(target) is { } policyError
            ? Task.FromResult(policyError)
            : runner.RunAsync(BuildPullPushArguments("push", target, allTags), ct);
    }

    private static List<string> BuildPullPushArguments(string command, string reference, bool allTags)
    {
        var args = new List<string> { command };
        if (allTags)
        {
            args.Add("--all-tags");
        }

        args.Add(reference);
        return args;
    }

    /// <summary>
    /// Probes registry authentication without relying on localized CLI prose.
    /// </summary>
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

    /// <summary>
    /// Removes an image from persisted state or the engine.
    /// </summary>
    public Task<CommandResult> RemoveImageAsync(string id, bool force = true, CancellationToken ct = default)
    {
        var args = new List<string> { "rmi" };
        if (force)
        {
            args.Add("--force");
        }

        args.Add(id);
        return runner.RunNonInteractiveAsync(args, ct);
    }

    /// <summary>
    /// Tags image through the service layer.
    /// </summary>
    public Task<CommandResult> TagImageAsync(string source, string target, CancellationToken ct = default) =>
        runner.RunAsync(["tag", source, target], ct);

    /// <summary>
    /// Prunes image service resources used by WSL Container Desktop.
    /// </summary>
    public Task<CommandResult> PruneImagesAsync(CancellationToken ct = default) =>
        PruneAsync(WslcPruneTarget.Images, ct);

    /// <summary>
    /// Inspects image through the service layer.
    /// </summary>
    public Task<CommandResult> InspectImageAsync(string id, CancellationToken ct = default) =>
        runner.RunAsync(["inspect", "--type", "image", id], ct);

    /// <summary>
    /// Returns the local <c>RepoDigests</c> for an image (e.g. "nginx@sha256:…"), read from
    /// <c>wslc inspect</c>. These are the manifest digests of what was pulled and are compared
    /// against the upstream registry digest to detect available updates. Empty if the image was
    /// built locally or has never been pushed/pulled (no digest).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetImageRepoDigestsAsync(string id, CancellationToken ct = default)
    {
        // Keep inspect here: `images --digests` exposes the row digest, but update checks need the
        // full RepoDigests list that records the registry-qualified manifest digests for this image.
        var result = await InspectImageAsync(id, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Array.Empty<string>();
        }

        var json = result.StandardOutput.Trim();
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<string>();
        }

        if (json[0] == '\uFEFF')
        {
            json = json[1..];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var element = root.ValueKind == JsonValueKind.Array
                ? (root.GetArrayLength() > 0 ? root[0] : default)
                : root;

            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("RepoDigests", out var digests) ||
                digests.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var d in digests.EnumerateArray())
            {
                var value = d.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    list.Add(value);
                }
            }

            return list;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse RepoDigests from wslc inspect for {Id}.", id);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Saves images so the app can restore it later.
    /// </summary>
    public Task<CommandResult> SaveImagesAsync(IReadOnlyList<string> references, string outputPath, CancellationToken ct = default)
    {
        if (references.Count == 0)
        {
            return Task.FromResult(new CommandResult { ExitCode = -1, StandardError = "Select at least one image to save." });
        }

        var args = new List<string> { "save", "--output", outputPath };
        args.AddRange(references);
        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Loads image data from persisted app state or an external tool.
    /// </summary>
    public Task<CommandResult> LoadImageAsync(string inputPath, CancellationToken ct = default) =>
        runner.RunAsync(["load", "--input", inputPath, "--quiet"], ct);

    /// <summary>
    /// Imports image through the service layer.
    /// </summary>
    public Task<CommandResult> ImportImageAsync(string inputPath, string? imageReference = null, CancellationToken ct = default)
    {
        var args = new List<string> { "import", inputPath };
        if (!string.IsNullOrWhiteSpace(imageReference))
        {
            args.Add(imageReference.Trim());
        }

        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Exports container through the service layer.
    /// </summary>
    public Task<CommandResult> ExportContainerAsync(string id, string outputPath, CancellationToken ct = default) =>
        runner.RunAsync(["export", "--output", outputPath, id], ct);

    /// <summary>
    /// Returns an image reference without its tag portion.
    /// </summary>
    internal static string StripTag(string reference)
    {
        var value = reference.Trim();
        var digestIndex = value.IndexOf('@');
        if (digestIndex >= 0)
        {
            value = value[..digestIndex];
        }

        var lastSlash = value.LastIndexOf('/');
        var lastColon = value.LastIndexOf(':');
        return lastColon > lastSlash ? value[..lastColon] : value;
    }

    /// <summary>
    /// Builds image values without exposing command details to callers.
    /// </summary>
    public Task<CommandResult> BuildImageAsync(
        string contextPath,
        string tag,
        string? dockerfile,
        IReadOnlyList<string>? buildArgs = null,
        string? target = null,
        IReadOnlyDictionary<string, string>? labels = null,
        bool noCache = false,
        bool pull = false,
        CancellationToken ct = default)
    {
        if (WslRegistryPolicyGuard.ValidateBuild(_policy.GetPolicy()) is { } buildError)
            return Task.FromResult(new CommandResult { ExitCode = -1, StandardError = buildError });
        var args = new List<string> { "build", "-t", tag };
        if (!string.IsNullOrWhiteSpace(dockerfile))
        {
            // `-f -` reads the Dockerfile from stdin, which the app can never supply.
            if (IsStdinDockerfile(dockerfile))
                return Task.FromResult(new CommandResult { ExitCode = -1, StandardError = StdinDockerfileError });
            args.Add("-f");
            args.Add(dockerfile);
        }

        if (buildArgs is not null)
        {
            foreach (var kv in buildArgs.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                args.Add("--build-arg");
                args.Add(kv.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            args.Add("--target");
            args.Add(target.Trim());
        }

        if (labels is not null)
        {
            foreach (var kv in labels.Where(l => !string.IsNullOrWhiteSpace(l.Key)))
            {
                args.Add("--label");
                args.Add(string.IsNullOrEmpty(kv.Value) ? kv.Key : $"{kv.Key}={kv.Value}");
            }
        }

        if (noCache)
        {
            args.Add("--no-cache");
        }

        if (pull)
        {
            args.Add("--pull");
        }

        args.Add(contextPath);
        return runner.RunAsync(args, ct);
    }

    private CommandResult? ValidateImagePolicy(string reference)
    {
        var message = WslRegistryPolicyGuard.ValidateImageReference(_policy.GetPolicy(), reference);
        return message is null ? null : new CommandResult { ExitCode = -1, StandardError = message };
    }

    /// <summary>
    /// The allowlist check for run and create. An image ID that matches an image already on this
    /// machine is allowed: running it fetches nothing, and Compose runs every service this way after
    /// its pull passed the same check. An ID that isn't present locally is checked like a name.
    /// </summary>
    private async Task<CommandResult?> ValidateRunImagePolicyAsync(string reference, CancellationToken ct)
    {
        if (ValidateImagePolicy(reference) is not { } error)
            return null;
        if (!WslRegistryPolicyGuard.IsImageId(reference))
            return error;

        var id = reference.Trim();
        if (id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            id = id[7..];
        try
        {
            var images = await ListImagesAsync(ct).ConfigureAwait(false);
            var local = images.Any(i =>
            {
                var imageId = i.Id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? i.Id[7..] : i.Id;
                return imageId.Length > 0 &&
                    (imageId.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                     id.StartsWith(imageId, StringComparison.OrdinalIgnoreCase));
            });
            return local ? null : error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If the local images can't be listed, keep the stricter answer.
            logger.LogDebug(ex, "Could not list images to check a local image ID against the registry allowlist.");
            return error;
        }
    }

    public const string StdinDockerfileError =
        "'-' reads the Dockerfile from standard input, which the app cannot supply. Enter a Dockerfile path instead.";

    /// <summary>True for the <c>-f -</c> form, which would make <c>wslc build</c> wait on stdin.</summary>
    public static bool IsStdinDockerfile(string? dockerfile) => dockerfile?.Trim() == "-";

    // ---- Volumes --------------------------------------------------------

    /// <summary>
    /// Lists volume service resources used by WSL Container Desktop.
    /// </summary>
    public async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["volume", "list", "--format", "json"], ct).ConfigureAwait(false);
        return WslcJsonParser.ParseVolumes(result);
    }

    /// <summary>
    /// Creates volume values used by the service layer.
    /// </summary>
    public Task<CommandResult> CreateVolumeAsync(
        string name,
        string? driver = null,
        IReadOnlyList<string>? driverOpts = null,
        IReadOnlyDictionary<string, string>? labels = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "volume", "create" };
        AppendResourceOptions(args, driver, driverOpts, labels);
        args.Add(name);
        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Removes a volume from persisted state or the engine.
    /// </summary>
    public Task<CommandResult> RemoveVolumeAsync(string name, CancellationToken ct = default) =>
        runner.RunNonInteractiveAsync(["volume", "remove", name], ct);

    /// <summary>
    /// Prunes volume service resources used by WSL Container Desktop.
    /// </summary>
    public Task<CommandResult> PruneVolumesAsync(CancellationToken ct = default) =>
        PruneAsync(WslcPruneTarget.Volumes, ct);

    /// <summary>
    /// Inspects volume through the service layer.
    /// </summary>
    public Task<CommandResult> InspectVolumeAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["volume", "inspect", name], ct);

    // ---- Networks -------------------------------------------------------

    /// <summary>
    /// Connects network through the service layer.
    /// </summary>
    public async Task<CommandResult> ConnectNetworkAsync(NetworkAttachment endpoint, string containerId, CancellationToken ct = default)
    {
        return await runner.RunAsync(endpoint.ToConnectArguments(containerId), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Disconnects network through the service layer.
    /// </summary>
    public async Task<CommandResult> DisconnectNetworkAsync(string network, string containerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        return await runner.RunAsync(["network", "disconnect", network, containerId], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists network service resources used by WSL Container Desktop.
    /// </summary>
    public async Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["network", "list", "--format", "json"], ct).ConfigureAwait(false);
        return Deserialize<NetworkInfo>(result);
    }

    /// <summary>
    /// Creates network values used by the service layer.
    /// </summary>
    public Task<CommandResult> CreateNetworkAsync(
        string name,
        string? driver = null,
        IReadOnlyList<string>? driverOpts = null,
        IReadOnlyDictionary<string, string>? labels = null,
        CancellationToken ct = default,
        bool internalNetwork = false) =>
        CreateNetworkAsync(name, driver, driverOpts, labels, null, null, null, ct, internalNetwork);

    /// <summary>
    /// Creates network values used by the service layer.
    /// </summary>
    public Task<CommandResult> CreateNetworkAsync(
        string name,
        string? driver,
        IReadOnlyList<string>? driverOpts,
        IReadOnlyDictionary<string, string>? labels,
        string? subnet,
        string? gateway,
        string? ipRange,
        CancellationToken ct = default,
        bool internalNetwork = false)
    {
        var args = new List<string> { "network", "create" };
        AppendResourceOptions(args, driver, driverOpts, labels);
        if (internalNetwork)
        {
            args.Add("--internal");
        }

        if (!string.IsNullOrWhiteSpace(subnet))
        {
            args.AddRange(["--subnet", subnet]);
        }

        if (!string.IsNullOrWhiteSpace(gateway))
        {
            args.AddRange(["--gateway", gateway]);
        }

        if (!string.IsNullOrWhiteSpace(ipRange))
        {
            args.AddRange(["--ip-range", ipRange]);
        }

        args.Add(name);
        return runner.RunAsync(args, ct);
    }

    /// <summary>
    /// Removes a network from persisted state or the engine.
    /// </summary>
    public Task<CommandResult> RemoveNetworkAsync(string name, CancellationToken ct = default) =>
        runner.RunNonInteractiveAsync(["network", "remove", name], ct);

    /// <summary>
    /// Prunes network service resources used by WSL Container Desktop.
    /// </summary>
    public Task<CommandResult> PruneNetworksAsync(CancellationToken ct = default) =>
        PruneAsync(WslcPruneTarget.Networks, ct);

    /// <summary>
    /// Inspects network through the service layer.
    /// </summary>
    public Task<CommandResult> InspectNetworkAsync(string name, CancellationToken ct = default) =>
        runner.RunAsync(["network", "inspect", name], ct);

    /// <summary>Appends shared <c>--driver</c> / <c>--opt</c> / <c>--label</c> options for network/volume create.</summary>
    private static void AppendResourceOptions(
        List<string> args,
        string? driver,
        IReadOnlyList<string>? driverOpts,
        IReadOnlyDictionary<string, string>? labels)
    {
        if (!string.IsNullOrWhiteSpace(driver))
        {
            args.Add("--driver");
            args.Add(driver.Trim());
        }

        if (driverOpts is not null)
        {
            foreach (var opt in driverOpts.Where(o => !string.IsNullOrWhiteSpace(o)))
            {
                args.Add("--opt");
                args.Add(opt.Trim());
            }
        }

        if (labels is not null)
        {
            foreach (var kv in labels.Where(l => !string.IsNullOrWhiteSpace(l.Key)))
            {
                args.Add("--label");
                args.Add(string.IsNullOrEmpty(kv.Value) ? kv.Key : $"{kv.Key}={kv.Value}");
            }
        }
    }

    // ---- Helpers --------------------------------------------------------

    private Task<CommandResult> ExecShellAsync(string id, string script, CancellationToken ct = default) =>
        runner.RunAsync(["exec", id, "sh", "-c", script], ct);

    private static string PosixParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/";
        }
        var idx = trimmed.LastIndexOf('/');
        if (idx < 0)
        {
            return ".";
        }

        return idx == 0 ? "/" : trimmed[..idx];
    }

    private static string PosixCombine(string directory, string name) =>
        string.IsNullOrEmpty(directory) || directory == "/"
            ? "/" + name
            : directory.TrimEnd('/') + "/" + name;

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

        try
        {
            return WslcJsonParser.ParseList<T>(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            var json = result.StandardOutput.Trim().TrimStart('\uFEFF');
            logger.LogWarning(ex, "Failed to parse wslc JSON output as {Type}. Raw output: {Output}", typeof(T).Name, json);
            return Array.Empty<T>();
        }
    }
}
