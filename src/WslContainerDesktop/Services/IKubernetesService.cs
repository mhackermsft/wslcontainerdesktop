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

public interface IKubernetesService
{
    /// <summary>Determines whether k3s is installed and whether it is running.</summary>
    Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Lightweight single-call probe (state + pod counts) for the nav footer indicator.</summary>
    Task<K8sFooterStatus> GetFooterStatusAsync(CancellationToken ct = default);

    /// <summary>Installs k3s in the WSL distro. Streams progress lines via <paramref name="onOutput"/>.</summary>
    Task<CommandResult> InstallAsync(Action<string> onOutput, CancellationToken ct = default);

    /// <summary>
    /// Upgrades (or downgrades) k3s in place by re-running the install script. A null or empty
    /// <paramref name="version"/> tracks the latest stable channel; otherwise pins an exact tag
    /// (e.g. "v1.36.2+k3s1"). Cluster data and workloads are preserved.
    /// </summary>
    Task<CommandResult> UpgradeAsync(string? version, Action<string> onOutput, CancellationToken ct = default);

    /// <summary>Returns the installed k3s version tag (e.g. "v1.36.2+k3s1"), or null if unknown.</summary>
    Task<string?> GetInstalledVersionAsync(CancellationToken ct = default);

    /// <summary>Returns the latest stable k3s version tag from the update channel, or null if unavailable.</summary>
    Task<string?> GetLatestStableVersionAsync(CancellationToken ct = default);

    /// <summary>Resolves a release channel (e.g. "stable" or "v1.32") to its current version tag, or null.</summary>
    Task<string?> GetChannelVersionAsync(string channel, CancellationToken ct = default);

    /// <summary>Uninstalls k3s and cleans up. Streams progress lines via <paramref name="onOutput"/>.</summary>
    Task<CommandResult> UninstallAsync(Action<string> onOutput, CancellationToken ct = default);

    Task<CommandResult> StartAsync(CancellationToken ct = default);
    Task<CommandResult> StopAsync(CancellationToken ct = default);

    Task<IReadOnlyList<K8sNode>> GetNodesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<K8sPod>> GetPodsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sDeployment>> GetDeploymentsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sService>> GetServicesAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sIngress>> GetIngressesAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sPvc>> GetPvcsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sConfigMap>> GetConfigMapsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sSecret>> GetSecretsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sJob>> GetJobsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<K8sCronJob>> GetCronJobsAsync(string? ns = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetNamespacesAsync(CancellationToken ct = default);

    /// <summary>Applies a Kubernetes YAML manifest (kubectl apply -f -).</summary>
    Task<CommandResult> ApplyManifestAsync(string yaml, CancellationToken ct = default);

    // ---- Single-object actions ----
    /// <summary>Deletes a resource. Pass an empty namespace for cluster-scoped kinds.</summary>
    Task<CommandResult> DeleteResourceAsync(string kind, string ns, string name, CancellationToken ct = default);

    /// <summary>Scales a deployment to the given replica count.</summary>
    Task<CommandResult> ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct = default);

    /// <summary>Triggers a rolling restart of a deployment.</summary>
    Task<CommandResult> RestartDeploymentAsync(string ns, string name, CancellationToken ct = default);

    /// <summary>Suspends or resumes a cronjob.</summary>
    Task<CommandResult> SetCronJobSuspendAsync(string ns, string name, bool suspend, CancellationToken ct = default);

    /// <summary>Manually triggers a cronjob by creating a one-off Job from it.</summary>
    Task<CommandResult> TriggerCronJobAsync(string ns, string name, CancellationToken ct = default);

    /// <summary>Returns the object's live manifest as YAML (kubectl get -o yaml).</summary>
    Task<CommandResult> GetResourceYamlAsync(string kind, string ns, string name, CancellationToken ct = default);

    /// <summary>Returns `kubectl describe` output for the object.</summary>
    Task<CommandResult> DescribeResourceAsync(string kind, string ns, string name, CancellationToken ct = default);

    /// <summary>Returns recent logs for a pod (all containers).</summary>
    Task<CommandResult> GetPodLogsAsync(string ns, string name, int tailLines, CancellationToken ct = default);

    // ---- Port forwarding ----
    /// <summary>Starts a `kubectl port-forward` session. Returns false if it failed to launch.</summary>
    bool StartPortForward(PortForward forward);

    /// <summary>Stops a running port-forward session by id.</summary>
    void StopPortForward(string id);

    /// <summary>Stops all running port-forward sessions (called on shutdown).</summary>
    void StopAllPortForwards();
}
