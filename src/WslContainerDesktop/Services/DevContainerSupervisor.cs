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
using System.Text;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Orchestrates Dev Container lifecycles through wslc and the Compose supervisor.</summary>
public sealed class DevContainerSupervisor(
    IWslcService wslc,
    IDevContainerStore store,
    IDevContainerFeatureResolver features,
    ComposeProjectSupervisor composeSupervisor,
    ProcessRunner runner,
    ILogger<DevContainerSupervisor> logger) : IDevContainerSupervisor
{
    private readonly SemaphoreSlim _upGate = new(1, 1);

    public async Task<DevContainerOperationResult> UpAsync(
        DevContainerConfig config,
        bool rebuild = false,
        bool noCache = false,
        CancellationToken ct = default)
    {
        await _upGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (config.Compose is not null)
                return await UpComposeAsync(config, rebuild, noCache, ct).ConfigureAwait(false);
            await RunHostLifecycleAsync(config, ct).ConfigureAwait(false);
            return await UpSingleContainerAsync(config, rebuild, noCache, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dev container up failed for {Name}.", config.Name);
            return new DevContainerOperationResult(false, ex.Message);
        }
        finally
        {
            _upGate.Release();
        }
    }

    private async Task<DevContainerOperationResult> UpSingleContainerAsync(
        DevContainerConfig config,
        bool rebuild,
        bool noCache,
        CancellationToken ct)
    {
        var image = await PrepareImageAsync(config, config.RunOptions, rebuild, noCache, ct).ConfigureAwait(false);
        if (!image.Success)
        {
            store.Save(config);
            return image;
        }

        await RemoveExistingAsync(config, ct).ConfigureAwait(false);
        var options = config.RunOptions.Clone();
        options.Image = config.EffectiveImage;
        options.Name = ContainerName(config);
        options.Command = "sleep infinity";

        var run = await wslc.RunContainerAsync(options, ct).ConfigureAwait(false);
        AppendLog(config, "run", run);
        if (!run.Success && IsNameConflict(run))
        {
            await wslc.RemoveContainerAsync(options.Name!, force: true, ct).ConfigureAwait(false);
            run = await wslc.RunContainerAsync(options, ct).ConfigureAwait(false);
            AppendLog(config, "run retry", run);
        }

        if (!run.Success)
        {
            store.Save(config);
            return new DevContainerOperationResult(false, Summarize(run));
        }

        var container = await FindContainerAsync(config, ct).ConfigureAwait(false);
        if (container is null)
        {
            store.Save(config);
            return new DevContainerOperationResult(false, "Container started but could not be found in wslc list.");
        }

        var lifecycle = await RunCreateLifecycleAsync(container.Id, config, ct).ConfigureAwait(false);
        if (!lifecycle.Success)
        {
            store.Save(config);
            return lifecycle;
        }

        store.Save(config);
        return new DevContainerOperationResult(true, $"Started {config.Name} as {options.Name}.");
    }

    private async Task<DevContainerOperationResult> UpComposeAsync(
        DevContainerConfig config,
        bool rebuild,
        bool noCache,
        CancellationToken ct)
    {
        var compose = config.Compose!;
        config.ComposeLifecycleProgress = store.Get(config.Id)?.ComposeLifecycleProgress ?? config.ComposeLifecycleProgress;
        var project = System.Text.Json.JsonSerializer.Deserialize<ComposeProject>(
            System.Text.Json.JsonSerializer.Serialize(compose.Project))!;
        var primary = project.Services.FirstOrDefault(s => string.Equals(s.Name, compose.Service, StringComparison.Ordinal));
        if (primary is null)
        {
            return new DevContainerOperationResult(false, $"Compose service '{compose.Service}' was not found.");
        }

        // Dynamic feature acquisition and initializeCommand can alter the reviewed inputs. They
        // cannot execute before a resolved review, nor be quietly skipped after it.
        if (config.Features.Count > 0 || config.Lifecycle.Initialize.Any(c => !string.IsNullOrWhiteSpace(c)))
            project.Warnings.Add("Blocked deployment: Compose dev-container features and host initialize commands require externally prepared inputs before a resolved compatibility review. No preparation was executed.");
        if (config.Build is { } build)
            primary.Build = new()
            {
                Context = build.Context, Dockerfile = build.Dockerfile, Args = [.. build.Args],
                Target = build.Target, NoCache = noCache,
            };
        if (primary.Build is not null) primary.Build.NoCache |= noCache;
        var result = await composeSupervisor.UpAsync(project, new ComposeOperationRequest { Build = rebuild }, completed =>
        {
            if (completed.Service == compose.Service && completed.InstanceIndex == 1 &&
                completed.Success && completed.ContainerId is { Length: > 0 } id)
                ScheduleComposeLifecycle(id, completed.Action, config);
        }, ct).ConfigureAwait(false);
        var failures = result.Services.Where(s => !s.Success).Select(s => $"{s.InstanceKey}: {s.Detail}").ToList();
        if (result.IsCancelled) failures.Add("Compose operation cancelled.");
        var primaryResult = result.Services.SingleOrDefault(s => s.Service == compose.Service && s.InstanceIndex == 1);
        if (!result.IsCancelled && primaryResult is { Success: true, ContainerId: { Length: > 0 } containerId })
        {
            // A successful primary still needs its hooks when a sibling fails. Otherwise
            // the retry keeps it and loses the creation event.
            var lifecycle = await RunComposeLifecycleAsync(containerId, config, ct).ConfigureAwait(false);
            if (!lifecycle.Success)
                failures.Add(lifecycle.Detail);
        }
        else if (!result.IsCancelled && (primaryResult is null || primaryResult.Success))
        {
            failures.Add("Compose did not return a successful container identity for the dev container service.");
        }

        store.Save(config);
        if (failures.Count > 0)
            return new DevContainerOperationResult(false, new ComposePreviewProjection(project).Redact(string.Join("\n", failures)));
        return new DevContainerOperationResult(true, $"Started {config.Name} using Compose service {compose.Service}.");
    }

    private void ScheduleComposeLifecycle(string containerId, ComposeServiceAction action, DevContainerConfig config)
    {
        if (action is not (ComposeServiceAction.Create or ComposeServiceAction.Recreate or
            ComposeServiceAction.Start or ComposeServiceAction.Restart))
            return;

        var progress = config.ComposeLifecycleProgress;
        if (progress is null || ContainerIdentity.ResolveId([containerId], progress.ContainerId) != containerId)
        {
            progress = new() { ContainerId = containerId };
            if (action is ComposeServiceAction.Create or ComposeServiceAction.Recreate)
            {
                progress.PendingCreate = config.Lifecycle.ContainerCreateSteps()
                    .SelectMany(step => SnapshotCommands(step.Step, step.Commands)).ToList();
            }
            config.ComposeLifecycleProgress = progress;
        }

        progress.PendingStart = SnapshotCommands("postStartCommand", config.Lifecycle.PostStart).ToList();
        store.Save(config);

        IEnumerable<DevContainerLifecycleCommand> SnapshotCommands(string step, IEnumerable<string> commands) =>
            commands.Where(c => !string.IsNullOrWhiteSpace(c)).Select(command => new DevContainerLifecycleCommand
            {
                Step = step, Command = BuildRemoteCommand(config, command, allowFailure: false),
            });
    }

    private async Task<DevContainerOperationResult> RunComposeLifecycleAsync(
        string containerId, DevContainerConfig config, CancellationToken ct)
    {
        var progress = config.ComposeLifecycleProgress;
        if (progress is null || ContainerIdentity.ResolveId([containerId], progress.ContainerId) != containerId)
            return new(true, "Existing container retained; no lifecycle hooks scheduled.");

        // Keep resumes only previously scheduled, unacknowledged work. Commands and
        // remote context are frozen so config edits cannot replay completed steps.
        foreach (var pending in new[] { progress.PendingCreate, progress.PendingStart })
        {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var command = pending[0];
                var result = await wslc.ExecAsync(containerId, command.Command, ct).ConfigureAwait(false);
                AppendLog(config, command.Step, result);
                if (result.Success)
                    pending.RemoveAt(0);
                store.Save(config);
                if (!result.Success)
                    return new(false, $"{command.Step} failed: {Summarize(result)}");
            }
        }
        return new(true, "Lifecycle complete.");
    }

    private async Task<DevContainerOperationResult> PrepareImageAsync(
        DevContainerConfig config,
        RunContainerOptions options,
        bool rebuild,
        bool noCache,
        CancellationToken ct,
        ComposeService? composeService = null)
    {
        var baseImage = options.Image;
        if (config.Build is { } build && (rebuild || string.IsNullOrWhiteSpace(config.Image) || config.Features.Count > 0))
        {
            baseImage = config.Features.Count > 0 ? DevContainerConfig.BaseImageTag(config.Id) : DevContainerConfig.DevContainerImageTag(config.Id);
            var built = await wslc.BuildImageAsync(
                build.Context,
                baseImage,
                build.Dockerfile,
                build.Args,
                build.Target,
                labels: Labels(config),
                noCache: noCache,
                pull: false,
                ct).ConfigureAwait(false);
            AppendLog(config, "build", built);
            if (!built.Success)
            {
                return new DevContainerOperationResult(false, Summarize(built));
            }
        }
        else if (composeService?.Build is { } composeBuild && composeBuild.IsValid && (rebuild || config.Features.Count > 0))
        {
            baseImage = config.Features.Count > 0 ? DevContainerConfig.BaseImageTag(config.Id) : DevContainerConfig.DevContainerImageTag(config.Id);
            var built = await wslc.BuildImageAsync(
                composeBuild.Context,
                baseImage,
                composeBuild.Dockerfile,
                composeBuild.Args,
                composeBuild.Target,
                labels: Labels(config),
                noCache: noCache || composeBuild.NoCache,
                pull: composeBuild.Pull,
                ct).ConfigureAwait(false);
            AppendLog(config, "compose service build", built);
            if (!built.Success)
            {
                return new DevContainerOperationResult(false, Summarize(built));
            }
            composeService.Build = null;
        }
        else if (!string.IsNullOrWhiteSpace(baseImage) && config.Features.Count == 0)
        {
            var pull = await wslc.PullImageAsync(baseImage, ct).ConfigureAwait(false);
            AppendLog(config, "pull", pull);
        }

        if (config.Features.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(baseImage))
            {
                baseImage = config.Image;
            }

            if (string.IsNullOrWhiteSpace(baseImage))
            {
                return new DevContainerOperationResult(false, "Features require an image or build base.");
            }

            var derived = await features.PrepareDerivedImageAsync(config, baseImage, ct).ConfigureAwait(false);
            if (derived is not null)
            {
                foreach (var warning in derived.Warnings)
                {
                    if (!config.Warnings.Contains(warning, StringComparer.Ordinal))
                    {
                        config.Warnings.Add(warning);
                    }
                }
                foreach (var env in derived.ContainerEnv)
                {
                    config.ContainerEnv[env.Key] = env.Value;
                    options.EnvironmentVariables.RemoveAll(e => e.StartsWith(env.Key + "=", StringComparison.Ordinal));
                    options.EnvironmentVariables.Add($"{env.Key}={env.Value}");
                }
                if (!string.IsNullOrWhiteSpace(derived.RemoteUser) && string.IsNullOrWhiteSpace(config.RemoteUser))
                {
                    config.RemoteUser = derived.RemoteUser;
                    options.User = derived.RemoteUser;
                }

                var featureBuild = await wslc.BuildImageAsync(
                    derived.ContextPath,
                    derived.ImageTag,
                    derived.DockerfilePath,
                    labels: Labels(config),
                    noCache: noCache,
                    pull: false,
                    ct: ct).ConfigureAwait(false);
                AppendLog(config, "features", featureBuild);
                if (!featureBuild.Success)
                {
                    return new DevContainerOperationResult(false, Summarize(featureBuild));
                }

                config.Image = derived.ImageTag;
                config.Build = null;
                options.Image = derived.ImageTag;
                if (composeService is not null)
                {
                    composeService.Options.Image = derived.ImageTag;
                    composeService.Build = null;
                }
            }
        }

        return new DevContainerOperationResult(true, "Image ready.");
    }

    public async Task StopAsync(DevContainerConfig config, CancellationToken ct = default)
    {
        if (config.Compose is not null)
        {
            await composeSupervisor.DownAsync(config.Compose.Project.Name, removeVolumes: false, ct).ConfigureAwait(false);
            return;
        }

        var container = await FindContainerAsync(config, ct).ConfigureAwait(false);
        if (container is not null)
        {
            await wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(DevContainerConfig config, CancellationToken ct = default)
    {
        if (config.Compose is not null)
        {
            await composeSupervisor.DownAsync(config.Compose.Project.Name, removeVolumes: true, ct).ConfigureAwait(false);
            composeSupervisor.CleanStaging(config.Compose.Project.Name);
        }
        else
        {
            await RemoveExistingAsync(config, ct).ConfigureAwait(false);
        }

        store.Delete(config.Id);
    }

    public void OpenTerminal(DevContainerConfig config, string containerId)
    {
        _ = RunPostAttachAsync(config, containerId);
        var command = BuildRemoteCommand(config, "exec ${SHELL:-/bin/sh}", allowFailure: true);
        runner.RunInteractive(["exec", "-it", containerId, "sh", "-lc", command]);
    }

    private async Task RunHostLifecycleAsync(DevContainerConfig config, CancellationToken ct)
    {
        foreach (var command in config.Lifecycle.Initialize.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = config.WorkspacePath,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            var result = await ProcessExecutor.RunAsync(psi, launchErrorContext: "Could not run initializeCommand.", ct: ct).ConfigureAwait(false);
            AppendLog(config, "initializeCommand", result);
            if (!result.Success)
            {
                throw new InvalidOperationException($"initializeCommand failed: {Summarize(result)}");
            }
        }
    }

    private async Task<DevContainerOperationResult> RunCreateLifecycleAsync(string containerId, DevContainerConfig config, CancellationToken ct)
    {
        foreach (var (step, commands) in config.Lifecycle.ContainerCreateSteps())
        {
            foreach (var command in commands.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                var result = await ExecLifecycleAsync(containerId, config, command, ct).ConfigureAwait(false);
                AppendLog(config, step, result);
                if (!result.Success)
                {
                    return new DevContainerOperationResult(false, $"{step} failed: {Summarize(result)}");
                }
            }
        }

        foreach (var command in config.Lifecycle.PostStart.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var result = await ExecLifecycleAsync(containerId, config, command, ct).ConfigureAwait(false);
            AppendLog(config, "postStartCommand", result);
            if (!result.Success)
            {
                return new DevContainerOperationResult(false, $"postStartCommand failed: {Summarize(result)}");
            }
        }

        return new DevContainerOperationResult(true, "Lifecycle complete.");
    }

    private async Task RunPostAttachAsync(DevContainerConfig config, string containerId)
    {
        foreach (var command in config.Lifecycle.PostAttach.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            try
            {
                var result = await ExecLifecycleAsync(containerId, config, command, CancellationToken.None).ConfigureAwait(false);
                AppendLog(config, "postAttachCommand", result);
                store.Save(config);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "postAttachCommand failed for {Name}.", config.Name);
            }
        }
    }

    private async Task RemoveExistingAsync(DevContainerConfig config, CancellationToken ct)
    {
        var container = await FindContainerAsync(config, ct).ConfigureAwait(false);
        if (container is null)
        {
            return;
        }

        try
        {
            await wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Stopping dev container {Name} failed; removing anyway.", config.Name);
        }

        await wslc.RemoveContainerAsync(container.Id, force: true, ct).ConfigureAwait(false);
    }

    private async Task<ContainerInfo?> FindContainerAsync(DevContainerConfig config, CancellationToken ct)
    {
        var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        return containers.FirstOrDefault(c => string.Equals(c.Name.TrimStart('/'), ContainerName(config), StringComparison.Ordinal));
    }

    private async Task<ContainerInfo?> FindComposeServiceContainerAsync(DevContainerConfig config, CancellationToken ct)
    {
        if (config.Compose is null)
        {
            return null;
        }

        var service = config.Compose.Project.Services.FirstOrDefault(s => string.Equals(s.Name, config.Compose.Service, StringComparison.Ordinal));
        if (service is null)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(service.Options.Name)
            ? config.Compose.Project.ContainerNameFor(service.Name)
            : service.Options.Name!.Trim();
        var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        return containers.FirstOrDefault(c => string.Equals(c.Name.TrimStart('/'), name, StringComparison.Ordinal));
    }

    private async Task<CommandResult> ExecLifecycleAsync(string containerId, DevContainerConfig config, string command, CancellationToken ct)
    {
        var script = BuildRemoteCommand(config, command, allowFailure: false);
        return await wslc.ExecAsync(containerId, script, ct).ConfigureAwait(false);
    }

    private static string BuildRemoteCommand(DevContainerConfig config, string command, bool allowFailure)
    {
        var sb = new StringBuilder();
        sb.Append("cd ");
        sb.Append(ShellQuote(config.WorkspaceFolder));
        sb.Append(allowFailure ? " 2>/dev/null || true; " : " && ");
        foreach (var env in config.RemoteEnv.Where(kv => !string.IsNullOrWhiteSpace(kv.Key)))
        {
            sb.Append("export ");
            sb.Append(env.Key);
            sb.Append('=');
            sb.Append(ShellQuote(env.Value));
            sb.Append("; ");
        }
        sb.Append(command);
        return sb.ToString();
    }

    private static string ContainerName(DevContainerConfig config) => $"devcontainer-{config.Id}";

    private static Dictionary<string, string> Labels(DevContainerConfig config) => new(StringComparer.Ordinal)
    {
        ["com.wslcontainerdesktop.kind"] = "devcontainer",
        ["com.wslcontainerdesktop.devcontainer.id"] = config.Id,
        ["com.wslcontainerdesktop.devcontainer.name"] = config.Name,
    };

    private static void AppendLog(DevContainerConfig config, string step, CommandResult result)
    {
        var sb = new StringBuilder(config.LifecycleLog ?? string.Empty);
        sb.AppendLine($"$ {step}");
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            sb.AppendLine(result.StandardOutput.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            sb.AppendLine(result.StandardError.TrimEnd());
        }
        sb.AppendLine(result.Success ? "[ok]" : $"[failed: {result.ExitCode}]");
        config.LifecycleLog = sb.ToString();
    }

    private static string Summarize(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        text = string.IsNullOrWhiteSpace(text) ? $"wslc exited with {result.ExitCode}" : text.Trim();

        if (IsVolumeMountLimit(result))
        {
            return "The WSL container engine hit its per-session volume mount limit (15 volumes). " +
                   "This is a known wslc preview limitation. Restart the container session with " +
                   "\"wslc system session terminate\" (or restart WSL), then try again. " +
                   "Original error: " + text;
        }

        return text;
    }

    // wslc caps mounted volumes per session at 15. When exhausted, a build either reports
    // "Too many volumes have been mounted" outright, or its context mounts empty and buildkit
    // fails the COPY with a "failed to calculate checksum ... : not found" error. Both mean the
    // same thing: the session is out of mount slots and needs to be restarted.
    private static bool IsVolumeMountLimit(CommandResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
        if (text.Contains("too many volumes have been mounted", StringComparison.Ordinal))
        {
            return true;
        }

        return text.Contains("failed to calculate checksum", StringComparison.Ordinal)
            && text.Contains(": not found", StringComparison.Ordinal);
    }

    private static bool IsNameConflict(CommandResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
        return text.Contains("already exists", StringComparison.Ordinal) || text.Contains("name", StringComparison.Ordinal) && text.Contains("conflict", StringComparison.Ordinal);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
