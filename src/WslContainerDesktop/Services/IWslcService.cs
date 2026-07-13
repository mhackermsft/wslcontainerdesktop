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

public interface IWslcService
{
    // Engine
    Task<CommandResult> GetVersionAsync(CancellationToken ct = default);
    Task<bool> IsEngineAvailableAsync(CancellationToken ct = default);

    // Containers
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(bool all = true, CancellationToken ct = default);
    Task<CommandResult> StartContainerAsync(string id, CancellationToken ct = default);
    Task<CommandResult> StopContainerAsync(string id, CancellationToken ct = default);
    Task<CommandResult> RestartContainerAsync(string id, CancellationToken ct = default);
    Task<CommandResult> KillContainerAsync(string id, CancellationToken ct = default);
    Task<CommandResult> RemoveContainerAsync(string id, bool force = true, CancellationToken ct = default);
    Task<CommandResult> PruneContainersAsync(CancellationToken ct = default);
    Task<CommandResult> RunContainerAsync(RunContainerOptions options, CancellationToken ct = default);
    Task<CommandResult> GetLogsAsync(string id, int tail = 500, CancellationToken ct = default);
    Task<CommandResult> InspectContainerAsync(string id, CancellationToken ct = default);
    Task<CommandResult> ListFilesAsync(string id, string path, CancellationToken ct = default);
    Task<CommandResult> ReadTextFileAsync(string id, string path, int maxBytes = 65_536, CancellationToken ct = default);
    Task<CommandResult> CopyFromContainerAsync(string id, string containerPath, string hostPath, CancellationToken ct = default);
    Task<CommandResult> CopyToContainerAsync(string id, string hostPath, string containerPath, CancellationToken ct = default);
    Task<CommandResult> DeletePathAsync(string id, string path, CancellationToken ct = default);
    Task<CommandResult> RenamePathAsync(string id, string oldPath, string newPath, CancellationToken ct = default);
    Task<CommandResult> CreateDirectoryAsync(string id, string path, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken ct = default);
    Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default);
    void OpenTerminal(string id);
    void FollowLogs(string id);

    /// <summary>Detects GPU passthrough for a running container (checks /dev/dxg), and the GPU name if available.</summary>
    Task<(bool HasGpu, string? GpuName)> GetGpuInfoAsync(string id, CancellationToken ct = default);

    // Images
    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default);
    Task<CommandResult> PullImageAsync(string reference, CancellationToken ct = default);
    Task<CommandResult> RemoveImageAsync(string id, bool force = true, CancellationToken ct = default);
    Task<CommandResult> TagImageAsync(string source, string target, CancellationToken ct = default);
    Task<CommandResult> PruneImagesAsync(CancellationToken ct = default);
    Task<CommandResult> InspectImageAsync(string id, CancellationToken ct = default);
    Task<CommandResult> BuildImageAsync(string contextPath, string tag, string? dockerfile, CancellationToken ct = default);

    // Registries
    /// <summary>Logs in to a registry via `wslc login`, feeding the password through stdin.</summary>
    Task<CommandResult> LoginRegistryAsync(string server, string username, string password, CancellationToken ct = default);

    /// <summary>Logs out of a registry via `wslc logout`.</summary>
    Task<CommandResult> LogoutRegistryAsync(string server, CancellationToken ct = default);

    /// <summary>Pushes a local image to its registry via `wslc push`.</summary>
    Task<CommandResult> PushImageAsync(string reference, CancellationToken ct = default);

    /// <summary>
    /// Probes whether the engine is authenticated to a registry by pulling a nonexistent tag,
    /// which forces the engine to reach the manifest endpoint. Classification prefers stable,
    /// locale-independent signals (the process exit code, wslc's structured WSLC_E_* error code,
    /// and registry/network protocol tokens) over the CLI's prose, and returns Unknown when the
    /// outcome is ambiguous rather than guessing.
    /// </summary>
    Task<Models.RegistryLoginState> ProbeRegistryLoginAsync(string host, string repository, CancellationToken ct = default);

    // Volumes
    Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default);
    Task<CommandResult> CreateVolumeAsync(string name, CancellationToken ct = default);
    Task<CommandResult> RemoveVolumeAsync(string name, CancellationToken ct = default);
    Task<CommandResult> PruneVolumesAsync(CancellationToken ct = default);
    Task<CommandResult> InspectVolumeAsync(string name, CancellationToken ct = default);

    // Networks
    Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default);
    Task<CommandResult> CreateNetworkAsync(string name, CancellationToken ct = default);
    Task<CommandResult> RemoveNetworkAsync(string name, CancellationToken ct = default);
    Task<CommandResult> PruneNetworksAsync(CancellationToken ct = default);
    Task<CommandResult> InspectNetworkAsync(string name, CancellationToken ct = default);
}
