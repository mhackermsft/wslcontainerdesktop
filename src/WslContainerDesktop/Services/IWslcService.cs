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

/// <summary>Outcome of probing how a host path bind-mounts inside the wslc VM.</summary>
public enum BindMountProbeResult
{
    /// <summary>The source mounted as a regular file (the healthy case).</summary>
    MountsAsFile,

    /// <summary>The source mounted as a directory — runc pre-created a missing bind source; the
    /// staged path is poisoned and must be re-staged fresh.</summary>
    MountsAsDirectory,

    /// <summary>The probe container could not run (e.g. the probe image is unavailable), so the mount
    /// state is unknown. Callers should fail open and let the real run surface any genuine error.</summary>
    ProbeUnavailable,
}

/// <summary>
/// Defines the high-level container engine API used by view models and supervisors instead of calling <c>wslc.exe</c> directly.
/// </summary>
public interface IWslcService
{
    // Engine
    /// <summary>
    /// Gets version information for callers that should not run commands directly.
    /// </summary>
    Task<CommandResult> GetVersionAsync(CancellationToken ct = default);
    /// <summary>
    /// Gets whether engine available for the current app or engine state.
    /// </summary>
    Task<bool> IsEngineAvailableAsync(CancellationToken ct = default);
    /// <summary>
    /// Gets system information for callers that should not run commands directly.
    /// </summary>
    Task<WslcSystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
    /// <summary>
    /// Gets event information for callers that should not run commands directly.
    /// </summary>
    Task<IReadOnlyList<EngineEvent>> GetEventsAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken ct = default);
    /// <summary>
    /// Opens settings for the user.
    /// </summary>
    Task<CommandResult> OpenSettingsAsync(CancellationToken ct = default);

    /// <summary>Terminates the current wslc session, releasing leaked bind-mount slots and stopping
    /// all running containers. See the implementation for the wslc volume-limit rationale.</summary>
    Task<CommandResult> RestartSessionAsync(CancellationToken ct = default);

    /// <summary>Verifies how a host path bind-mounts inside the wslc VM, to detect the config/secret
    /// bind race where runc pre-creates a missing source as a directory. Distinguishes a clean file
    /// mount from a raced directory mount from a probe that could not run at all (fail-open).</summary>
    Task<BindMountProbeResult> VerifyBindMountAsync(string hostSource, CancellationToken ct = default);

    // Containers
    /// <summary>
    /// Lists container service resources used by WSL Container Desktop.
    /// </summary>
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(bool all = true, CancellationToken ct = default, bool includeSize = false);
    /// <summary>
    /// Starts container work requested by the UI or a background supervisor.
    /// </summary>
    Task<CommandResult> StartContainerAsync(string id, CancellationToken ct = default, bool explicitStart = true);
    /// <summary>
    /// Stops container work requested by the UI or a background supervisor.
    /// </summary>
    Task<CommandResult> StopContainerAsync(string id, CancellationToken ct = default);

    /// <summary>Stops a container, waiting <paramref name="timeSeconds"/> before SIGKILL and optionally sending <paramref name="signal"/> first (compose <c>stop_grace_period</c>/<c>stop_signal</c>).</summary>
    Task<CommandResult> StopContainerAsync(string id, int? timeSeconds, string? signal, CancellationToken ct = default);
    /// <summary>
    /// Restarts container through the service layer.
    /// </summary>
    Task<CommandResult> RestartContainerAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// Restarts container through the service layer.
    /// </summary>
    Task<CommandResult> RestartContainerAsync(string id, int? timeSeconds, string? signal, CancellationToken ct = default);
    /// <summary>
    /// Force-stops container through the service layer.
    /// </summary>
    Task<CommandResult> KillContainerAsync(string id, CancellationToken ct = default);
    /// <summary>Removes a container. When <paramref name="removeAnonymousVolumes"/> is true and the
    /// engine advertises <c>remove --volumes</c>, anonymous volumes created for the container are
    /// deleted with it. Named volumes are never removed by this flag.</summary>
    Task<CommandResult> RemoveContainerAsync(string id, bool force = true, CancellationToken ct = default,
        bool removeAnonymousVolumes = false);
    /// <summary>
    /// Prunes container service resources used by WSL Container Desktop.
    /// </summary>
    Task<CommandResult> PruneContainersAsync(CancellationToken ct = default);
    /// <summary>
    /// Runs container through the service layer.
    /// </summary>
    Task<CommandResult> RunContainerAsync(RunContainerOptions options, CancellationToken ct = default,
        /// <summary>
        /// Gets maximum stop version for other services or view models.
        /// </summary>
        long maximumStopVersion = long.MaxValue);
    /// <summary>
    /// Creates container values used by the service layer.
    /// </summary>
    Task<CommandResult> CreateContainerAsync(RunContainerOptions options, CancellationToken ct = default);
    /// <summary>
    /// Gets log information for callers that should not run commands directly.
    /// </summary>
    Task<CommandResult> GetLogsAsync(string id, int tail = 500, CancellationToken ct = default, bool details = false, bool timestamps = false,
        DateTimeOffset? since = null, DateTimeOffset? until = null);
    /// <summary>
    /// Inspects container through the service layer.
    /// </summary>
    Task<CommandResult> InspectContainerAsync(string id, CancellationToken ct = default, bool includeSize = false);
    /// <summary>
    /// Lists files resources for callers that should not invoke <c>wslc</c> directly.
    /// </summary>
    Task<CommandResult> ListFilesAsync(string id, string path, CancellationToken ct = default);
    /// <summary>
    /// Reads text file through the service layer.
    /// </summary>
    Task<CommandResult> ReadTextFileAsync(string id, string path, int maxBytes = 65_536, CancellationToken ct = default);
    /// <summary>
    /// Copies from container through the service layer.
    /// </summary>
    Task<CommandResult> CopyFromContainerAsync(string id, string containerPath, string hostPath, CancellationToken ct = default,
        bool followSymlinks = false);
    /// <summary>
    /// Copies to container through the service layer.
    /// </summary>
    Task<CommandResult> CopyToContainerAsync(string id, string hostPath, string containerPath, CancellationToken ct = default);
    /// <summary>
    /// Removes path from persisted state or the engine.
    /// </summary>
    Task<CommandResult> DeletePathAsync(string id, string path, CancellationToken ct = default);
    /// <summary>
    /// Renames a file or directory inside a container through the service layer.
    /// </summary>
    Task<CommandResult> RenamePathAsync(string id, string oldPath, string newPath, CancellationToken ct = default);
    /// <summary>
    /// Creates directory values used by the service layer.
    /// </summary>
    Task<CommandResult> CreateDirectoryAsync(string id, string path, CancellationToken ct = default);
    /// <summary>
    /// Gets statistics information for callers that should not run commands directly.
    /// </summary>
    Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken ct = default);
    /// <summary>
    /// Gets statistics information for callers that should not run commands directly.
    /// </summary>
    Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// Opens terminal for the user.
    /// </summary>
    void OpenTerminal(string id);
    /// <summary>
    /// Starts following container logs through the service layer.
    /// </summary>
    void FollowLogs(string id, bool details = false, bool timestamps = false);
    /// <summary>
    /// Attaches an interactive session to a running container.
    /// </summary>
    void AttachContainer(string id);

    /// <summary>Runs a shell command inside a container (`wslc exec &lt;id&gt; sh -c &lt;command&gt;`) and returns its result.</summary>
    Task<CommandResult> ExecAsync(string id, string command, CancellationToken ct = default);
    /// <summary>
    /// Executes an app-owned health command inside a container.
    /// </summary>
    Task<CommandResult> ExecHealthAsync(string id, NativeHealthOptions health, CancellationToken ct = default);

    /// <summary>
    /// Computes the filesystem changes of a container relative to its base image (a `docker diff`
    /// equivalent). <paramref name="image"/> is the image reference/id used to build the baseline.
    /// </summary>
    Task<IReadOnlyList<ContainerFsChange>> DiffContainerAsync(string id, string image, CancellationToken ct = default);

    /// <summary>Detects GPU passthrough for a running container (checks /dev/dxg), and the GPU name if available.</summary>
    Task<(bool HasGpu, string? GpuName)> GetGpuInfoAsync(string id, CancellationToken ct = default);

    // Images
    /// <summary>
    /// Lists image service resources used by WSL Container Desktop.
    /// </summary>
    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default, bool showAll = false);
    /// <summary>
    /// Pulls image through the service layer.
    /// </summary>
    Task<CommandResult> PullImageAsync(string reference, CancellationToken ct = default, bool allTags = false);

    /// <summary>Pulls an image, reporting each output line (e.g. per-layer status) as it arrives on a reader thread.</summary>
    Task<CommandResult> PullImageAsync(string reference, Action<string> onLine, CancellationToken ct = default, bool allTags = false);
    /// <summary>
    /// Removes image from persisted state or the engine.
    /// </summary>
    Task<CommandResult> RemoveImageAsync(string id, bool force = true, CancellationToken ct = default);
    /// <summary>
    /// Tags image through the service layer.
    /// </summary>
    Task<CommandResult> TagImageAsync(string source, string target, CancellationToken ct = default);
    /// <summary>
    /// Prunes image service resources used by WSL Container Desktop.
    /// </summary>
    Task<CommandResult> PruneImagesAsync(CancellationToken ct = default);
    /// <summary>
    /// Inspects image through the service layer.
    /// </summary>
    Task<CommandResult> InspectImageAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// Gets image repository digest information for callers that should not run commands directly.
    /// </summary>
    Task<IReadOnlyList<string>> GetImageRepoDigestsAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// Saves image data so it is available after the app restarts.
    /// </summary>
    Task<CommandResult> SaveImagesAsync(IReadOnlyList<string> references, string outputPath, CancellationToken ct = default);
    /// <summary>
    /// Loads image data from the app's persisted state or an external tool.
    /// </summary>
    Task<CommandResult> LoadImageAsync(string inputPath, CancellationToken ct = default);
    /// <summary>
    /// Imports image through the service layer.
    /// </summary>
    Task<CommandResult> ImportImageAsync(string inputPath, string? imageReference = null, CancellationToken ct = default);
    /// <summary>
    /// Exports container through the service layer.
    /// </summary>
    Task<CommandResult> ExportContainerAsync(string id, string outputPath, CancellationToken ct = default);
    /// <summary>
    /// Builds image values without exposing command-line details to callers.
    /// </summary>
    Task<CommandResult> BuildImageAsync(
        string contextPath,
        string tag,
        string? dockerfile,
        IReadOnlyList<string>? buildArgs = null,
        string? target = null,
        IReadOnlyDictionary<string, string>? labels = null,
        bool noCache = false,
        bool pull = false,
        CancellationToken ct = default);

    // Registries
    /// <summary>Logs in to a registry via `wslc login`, feeding the password through stdin.</summary>
    Task<CommandResult> LoginRegistryAsync(string server, string username, string password, CancellationToken ct = default);

    /// <summary>Logs out of a registry via `wslc logout`.</summary>
    Task<CommandResult> LogoutRegistryAsync(string server, CancellationToken ct = default);

    /// <summary>Pushes a local image to its registry via `wslc push`.</summary>
    Task<CommandResult> PushImageAsync(string reference, CancellationToken ct = default, bool allTags = false);

    /// <summary>
    /// Probes whether the engine is authenticated to a registry by pulling a nonexistent tag,
    /// which forces the engine to reach the manifest endpoint. Classification prefers stable,
    /// locale-independent signals (the process exit code, wslc's structured WSLC_E_* error code,
    /// and registry/network protocol tokens) over the CLI's prose, and returns Unknown when the
    /// outcome is ambiguous rather than guessing.
    /// </summary>
    Task<Models.RegistryLoginState> ProbeRegistryLoginAsync(string host, string repository, CancellationToken ct = default);

    // Volumes
    /// <summary>
    /// Lists volume service resources used by WSL Container Desktop.
    /// </summary>
    Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct = default);
    /// <summary>
    /// Creates volume values used by the service layer.
    /// </summary>
    Task<CommandResult> CreateVolumeAsync(
        string name,
        string? driver = null,
        IReadOnlyList<string>? driverOpts = null,
        IReadOnlyDictionary<string, string>? labels = null,
        CancellationToken ct = default);
    /// <summary>
    /// Removes volume from persisted state or the engine.
    /// </summary>
    Task<CommandResult> RemoveVolumeAsync(string name, CancellationToken ct = default);
    /// <summary>
    /// Prunes volume service resources used by WSL Container Desktop.
    /// </summary>
    Task<CommandResult> PruneVolumesAsync(CancellationToken ct = default);
    /// <summary>
    /// Inspects volume through the service layer.
    /// </summary>
    Task<CommandResult> InspectVolumeAsync(string name, CancellationToken ct = default);

    // Networks
    /// <summary>
    /// Lists network service resources used by WSL Container Desktop.
    /// </summary>
    Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default);
    /// <summary>
    /// Creates network values used by the service layer.
    /// </summary>
    Task<CommandResult> CreateNetworkAsync(
        string name,
        string? driver = null,
        IReadOnlyList<string>? driverOpts = null,
        IReadOnlyDictionary<string, string>? labels = null,
        CancellationToken ct = default,
        bool internalNetwork = false);
    /// <summary>
    /// Creates network values used by the service layer.
    /// </summary>
    Task<CommandResult> CreateNetworkAsync(
        string name,
        string? driver,
        IReadOnlyList<string>? driverOpts,
        IReadOnlyDictionary<string, string>? labels,
        string? subnet,
        string? gateway,
        string? ipRange,
        CancellationToken ct = default,
        bool internalNetwork = false);
    /// <summary>
    /// Removes network from persisted state or the engine.
    /// </summary>
    Task<CommandResult> RemoveNetworkAsync(string name, CancellationToken ct = default);
    /// <summary>
    /// Prunes network service resources used by WSL Container Desktop.
    /// </summary>
    Task<CommandResult> PruneNetworksAsync(CancellationToken ct = default);
    /// <summary>
    /// Inspects network through the service layer.
    /// </summary>
    Task<CommandResult> InspectNetworkAsync(string name, CancellationToken ct = default);
    /// <summary>
    /// Connects network through the service layer.
    /// </summary>
    Task<CommandResult> ConnectNetworkAsync(NetworkAttachment endpoint, string containerId, CancellationToken ct = default);
    /// <summary>
    /// Disconnects network through the service layer.
    /// </summary>
    Task<CommandResult> DisconnectNetworkAsync(string network, string containerId, CancellationToken ct = default);
}
