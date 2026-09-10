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
    /// <summary>The ownership-verified container was already running; API readiness is separate.</summary>
    AlreadyRunning,

    /// <summary>An existing stopped container was started again.</summary>
    StartedExisting,

    /// <summary>A new container was started with GPU access requested, not proof of acceleration.</summary>
    CreatedWithGpu,

    /// <summary>A new container was started CPU-only because create help definitively lacks GPU support.</summary>
    CreatedCpuOnly,

    Failed,
    Cancelled,
}

/// <summary>Result of ensuring the local Ollama container exists and is running.</summary>
public sealed record LocalAiSetupResult(bool Success, LocalAiContainerState State, string Message,
    string? ContainerId = null, LocalRuntimeResourceState Runtime = LocalRuntimeResourceState.Unknown,
    LocalRuntimeResourceState ModelData = LocalRuntimeResourceState.Unknown);

public sealed record LocalAiRemovalResult(bool Success, LocalRuntimeResourceState Runtime,
    LocalRuntimeResourceState ModelData, string Message);

/// <summary>
/// Provisions an ownership-verified Ollama container from an already acquired immutable image.
/// This service never downloads images/models, starts native runtimes, or asserts model capabilities.
/// </summary>
public interface ILocalAiSetupService
{
    /// <summary>The container name this service manages.</summary>
    string ContainerName { get; }

    /// <summary>The published host port for the Ollama API.</summary>
    int HostPort { get; }

    /// <summary>Starts or reuses an owned container. CPU selection requires definitive pre-mutation
    /// evidence that the CLI does not support GPU creation. Failed mutations are never retried.</summary>
    Task<LocalAiSetupResult> EnsureOllamaContainerAsync(IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>Removes only the verified immutable container ID. Model deletion requests are
    /// reported separately; name-only volume deletion is unsafe and retains data.</summary>
    Task<LocalAiRemovalResult> RemoveOllamaContainerAsync(bool removeModelVolume, CancellationToken ct = default);
}
