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

using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <inheritdoc cref="ILocalAiSetupService"/>
public sealed class LocalAiSetupService(IWslcService wslc, ILogger<LocalAiSetupService> logger) : ILocalAiSetupService
{
    private const string ImageReference = "ollama/ollama";
    private const string ManagedContainerName = "wslcd-ollama";
    private const string ModelVolumeName = "wslcd-ollama";
    private const int Port = 11434;

    public string ContainerName => ManagedContainerName;

    public int HostPort => Port;

    public async Task<LocalAiSetupResult> EnsureOllamaContainerAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        // Reuse an existing app-managed container if one is already present.
        var existing = await FindManagedContainerAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.State.IsRunning())
            {
                progress?.Report("Ollama container is already running.");
                return new LocalAiSetupResult(true, LocalAiContainerState.AlreadyRunning, "Ollama container is already running.");
            }

            progress?.Report("Starting the existing Ollama container…");
            var start = await wslc.StartContainerAsync(existing.Id, ct).ConfigureAwait(false);
            return start.Success
                ? new LocalAiSetupResult(true, LocalAiContainerState.StartedExisting, "Started the existing Ollama container.")
                : new LocalAiSetupResult(false, LocalAiContainerState.StartedExisting, $"Could not start the Ollama container: {start.ErrorText}");
        }

        // Pull the image (a no-op if already present).
        progress?.Report($"Pulling {ImageReference} (first run may take a few minutes)…");
        var pull = await wslc.PullImageAsync(ImageReference, ct).ConfigureAwait(false);
        if (!pull.Success)
        {
            return new LocalAiSetupResult(false, LocalAiContainerState.CreatedCpuOnly, $"Could not pull {ImageReference}: {pull.ErrorText}");
        }

        // Prefer GPU acceleration; fall back to CPU when the GPU start fails.
        progress?.Report("Starting Ollama with GPU acceleration…");
        var gpu = await wslc.RunContainerAsync(BuildRunOptions(useGpu: true), ct).ConfigureAwait(false);
        if (gpu.Success)
        {
            progress?.Report("Ollama is running with GPU acceleration.");
            return new LocalAiSetupResult(true, LocalAiContainerState.CreatedWithGpu, "Ollama is running with GPU acceleration.");
        }

        logger.LogInformation("GPU start of Ollama failed; falling back to CPU. Details: {Error}", gpu.ErrorText);
        // A failed `run` may still leave a created-but-not-started container behind; clear it first.
        await RemoveByNameAsync(ct).ConfigureAwait(false);

        progress?.Report("GPU unavailable — starting Ollama on CPU…");
        var cpu = await wslc.RunContainerAsync(BuildRunOptions(useGpu: false), ct).ConfigureAwait(false);
        return cpu.Success
            ? new LocalAiSetupResult(true, LocalAiContainerState.CreatedCpuOnly, "Ollama is running on CPU (no GPU acceleration).")
            : new LocalAiSetupResult(false, LocalAiContainerState.CreatedCpuOnly, $"Could not start Ollama: {cpu.ErrorText}");
    }

    public async Task<CommandResult> RemoveOllamaContainerAsync(bool removeModelVolume, CancellationToken ct = default)
    {
        var result = await RemoveByNameAsync(ct).ConfigureAwait(false);
        if (removeModelVolume)
        {
            // Best-effort: the volume only frees once the container using it is gone.
            var volume = await wslc.RemoveVolumeAsync(ModelVolumeName, ct).ConfigureAwait(false);
            if (!volume.Success)
            {
                logger.LogDebug("Could not remove Ollama model volume '{Volume}': {Error}", ModelVolumeName, volume.ErrorText);
            }
        }

        return result;
    }

    private RunContainerOptions BuildRunOptions(bool useGpu) => new()
    {
        Image = ImageReference,
        Name = ManagedContainerName,
        Detached = true,
        AllGpus = useGpu,
        PortMappings = { $"{Port}:{Port}" },
        Volumes = { $"{ModelVolumeName}:/root/.ollama" },
        Labels = { ["com.wslcontainerdesktop.managed"] = "local-ai" },
    };

    private async Task<ContainerInfo?> FindManagedContainerAsync(CancellationToken ct)
    {
        var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        return containers.FirstOrDefault(c =>
            string.Equals(c.Name, ManagedContainerName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CommandResult> RemoveByNameAsync(CancellationToken ct)
    {
        var existing = await FindManagedContainerAsync(ct).ConfigureAwait(false);
        if (existing is null)
        {
            return new CommandResult { ExitCode = 0 };
        }

        return await wslc.RemoveContainerAsync(existing.Id, force: true, ct).ConfigureAwait(false);
    }
}
