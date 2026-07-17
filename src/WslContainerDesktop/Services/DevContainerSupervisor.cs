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

/// <summary>Orchestrates single-container Dev Container MVP lifecycles through wslc.</summary>
public sealed class DevContainerSupervisor(
    IWslcService wslc,
    IDevContainerStore store,
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
            if (config.Build is { } build)
            {
                if (rebuild || string.IsNullOrWhiteSpace(config.Image))
                {
                    var built = await wslc.BuildImageAsync(
                        build.Context,
                        DevContainerConfig.DevContainerImageTag(config.Id),
                        build.Dockerfile,
                        build.Args,
                        build.Target,
                        labels: new Dictionary<string, string>
                        {
                            ["com.wslcontainerdesktop.kind"] = "devcontainer",
                            ["com.wslcontainerdesktop.devcontainer.id"] = config.Id,
                        },
                        noCache: noCache,
                        pull: false,
                        ct).ConfigureAwait(false);
                    AppendLog(config, "build", built);
                    if (!built.Success)
                    {
                        store.Save(config);
                        return new DevContainerOperationResult(false, Summarize(built));
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(config.Image))
            {
                var pull = await wslc.PullImageAsync(config.Image, ct).ConfigureAwait(false);
                AppendLog(config, "pull", pull);
                if (!pull.Success)
                {
                    store.Save(config);
                    return new DevContainerOperationResult(false, Summarize(pull));
                }
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

            if (!string.IsNullOrWhiteSpace(config.PostCreateCommand))
            {
                var postCreate = await ExecLifecycleAsync(container.Id, config, config.PostCreateCommand!, ct).ConfigureAwait(false);
                AppendLog(config, "postCreateCommand", postCreate);
                if (!postCreate.Success)
                {
                    store.Save(config);
                    return new DevContainerOperationResult(false, $"postCreateCommand failed: {Summarize(postCreate)}");
                }
            }

            if (!string.IsNullOrWhiteSpace(config.PostStartCommand))
            {
                var postStart = await ExecLifecycleAsync(container.Id, config, config.PostStartCommand!, ct).ConfigureAwait(false);
                AppendLog(config, "postStartCommand", postStart);
                if (!postStart.Success)
                {
                    store.Save(config);
                    return new DevContainerOperationResult(false, $"postStartCommand failed: {Summarize(postStart)}");
                }
            }

            store.Save(config);
            return new DevContainerOperationResult(true, $"Started {config.Name} as {options.Name}.");
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

    public async Task StopAsync(DevContainerConfig config, CancellationToken ct = default)
    {
        var container = await FindContainerAsync(config, ct).ConfigureAwait(false);
        if (container is not null)
        {
            await wslc.StopContainerAsync(container.Id, ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveAsync(DevContainerConfig config, CancellationToken ct = default)
    {
        await RemoveExistingAsync(config, ct).ConfigureAwait(false);
        store.Delete(config.Id);
    }

    public void OpenTerminal(DevContainerConfig config, string containerId)
    {
        var command = "cd " + ShellQuote(config.WorkspaceFolder) + " 2>/dev/null || true; exec ${SHELL:-/bin/sh}";
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
        var name = ContainerName(config);
        var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        return containers.FirstOrDefault(c => string.Equals(c.Name.TrimStart('/'), name, StringComparison.Ordinal));
    }

    private async Task<CommandResult> ExecLifecycleAsync(string containerId, DevContainerConfig config, string command, CancellationToken ct)
    {
        var script = $"cd {ShellQuote(config.WorkspaceFolder)} && {command}";
        return await wslc.ExecAsync(containerId, script, ct).ConfigureAwait(false);
    }

    private static string ContainerName(DevContainerConfig config) => $"devcontainer-{config.Id}";

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
