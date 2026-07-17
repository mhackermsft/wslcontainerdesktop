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
    public async Task<DevContainerOperationResult> UpAsync(
        DevContainerConfig config,
        bool rebuild = false,
        bool noCache = false,
        CancellationToken ct = default)
    {
        try
        {
            await RunHostLifecycleAsync(config, ct).ConfigureAwait(false);
            return config.Compose is not null
                ? await UpComposeAsync(config, rebuild, noCache, ct).ConfigureAwait(false)
                : await UpSingleContainerAsync(config, rebuild, noCache, ct).ConfigureAwait(false);
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
        var primary = compose.Project.Services.FirstOrDefault(s => string.Equals(s.Name, compose.Service, StringComparison.Ordinal));
        if (primary is null)
        {
            return new DevContainerOperationResult(false, $"Compose service '{compose.Service}' was not found.");
        }

        var image = await PrepareImageAsync(config, primary.Options, rebuild, noCache, ct, primary).ConfigureAwait(false);
        if (!image.Success)
        {
            store.Save(config);
            return image;
        }

        var result = await composeSupervisor.UpAsync(compose.Project, ct).ConfigureAwait(false);
        if (!result.AllSucceeded)
        {
            store.Save(config);
            return new DevContainerOperationResult(false, string.Join("\n", result.Services.Where(s => !s.Success).Select(s => $"{s.Service}: {s.Detail}")));
        }

        var container = await FindComposeServiceContainerAsync(config, ct).ConfigureAwait(false);
        if (container is null)
        {
            store.Save(config);
            return new DevContainerOperationResult(false, "Compose project started but the dev container service could not be found.");
        }

        var lifecycle = await RunCreateLifecycleAsync(container.Id, config, ct).ConfigureAwait(false);
        if (!lifecycle.Success)
        {
            store.Save(config);
            return lifecycle;
        }

        store.Save(config);
        return new DevContainerOperationResult(true, $"Started {config.Name} using Compose service {compose.Service}.");
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

    public void OpenInVsCode(DevContainerConfig config)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "code",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(config.WorkspacePath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not launch VS Code for {Path}.", config.WorkspacePath);
        }
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
        return string.IsNullOrWhiteSpace(text) ? $"wslc exited with {result.ExitCode}" : text.Trim();
    }

    private static bool IsNameConflict(CommandResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).ToLowerInvariant();
        return text.Contains("already exists", StringComparison.Ordinal) || text.Contains("name", StringComparison.Ordinal) && text.Contains("conflict", StringComparison.Ordinal);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
