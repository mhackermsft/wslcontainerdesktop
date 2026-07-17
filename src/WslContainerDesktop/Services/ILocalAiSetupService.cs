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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>How the app-managed Ollama container ended up running.</summary>
public enum LocalAiContainerState
{
    /// <summary>The container was already up (or a non-container Ollama is answering the endpoint).</summary>
    AlreadyRunning,

    /// <summary>An existing stopped container was started again.</summary>
    StartedExisting,

    /// <summary>A new container was created with GPU acceleration (<c>--gpus all</c>).</summary>
    CreatedWithGpu,

    /// <summary>A new container was created CPU-only (GPU unavailable or GPU start failed).</summary>
    CreatedCpuOnly,
}

/// <summary>Result of ensuring the local Ollama container exists and is running.</summary>
public sealed record LocalAiSetupResult(bool Success, LocalAiContainerState State, string Message);

/// <summary>
/// One-click provisioning of a local AI engine: deploys the <c>ollama/ollama</c> container (GPU
/// preferred, CPU fallback), so users get an offline AI assistant without any manual container work.
/// The container is engine-managed (<c>--restart unless-stopped</c>); the app has no background daemon.
/// </summary>
public interface ILocalAiSetupService
{
    /// <summary>The container name this service manages.</summary>
    string ContainerName { get; }

    /// <summary>The published host port for the Ollama API.</summary>
    int HostPort { get; }

    /// <summary>Pulls the image if needed and starts (or reuses) the Ollama container. Prefers GPU,
    /// falling back to CPU when GPU acceleration is unavailable.</summary>
    Task<LocalAiSetupResult> EnsureOllamaContainerAsync(IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>Stops and removes the app-managed Ollama container. Optionally removes its model volume.</summary>
    Task<CommandResult> RemoveOllamaContainerAsync(bool removeModelVolume, CancellationToken ct = default);
}
