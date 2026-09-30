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

/// <summary>
/// Facade for managing a single-node k3s Kubernetes cluster inside a WSL distro. All privileged
/// operations run via <c>wsl.exe -u root</c> (no Linux password required); k3s bundles kubectl.
/// The actual work is delegated to focused collaborators - <see cref="K8sInstaller"/> (lifecycle
/// and versions), <see cref="K8sResourceClient"/> (status, queries, single-object actions), and
/// <see cref="PortForwardManager"/> (port-forward sessions) - over a shared <see cref="WslRootShell"/>.
/// </summary>
public sealed class KubernetesService : IKubernetesService
{
    private readonly K8sInstaller _installer;
    private readonly K8sResourceClient _resources;
    private readonly PortForwardManager _portForwards;

    /// <summary>Creates the k3s collaborators around the configured WSL distro and shared root shell.</summary>
    public KubernetesService(ISettingsService settings, ILoggerFactory loggerFactory)
    {
        var shell = new WslRootShell(settings);
        _installer = new K8sInstaller(shell);
        _resources = new K8sResourceClient(shell, loggerFactory.CreateLogger<K8sResourceClient>());
        _portForwards = new PortForwardManager(shell);
    }

    // ---- Status ----
    /// <inheritdoc/>
    public Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default) => _resources.GetStatusAsync(ct);
    /// <inheritdoc/>
    public Task<K8sFooterStatus> GetFooterStatusAsync(CancellationToken ct = default) => _resources.GetFooterStatusAsync(ct);

    // ---- Install / lifecycle ----
    /// <inheritdoc/>
    public Task<K3sInstallResult> InstallAsync(string? expectedInstallerHash, Action<string> onOutput, CancellationToken ct = default) =>
        _installer.InstallAsync(expectedInstallerHash, onOutput, ct);

    /// <inheritdoc/>
    public Task<K3sInstallResult> UpgradeAsync(string? version, string? expectedInstallerHash, Action<string> onOutput, CancellationToken ct = default) =>
        _installer.UpgradeAsync(version, expectedInstallerHash, onOutput, ct);

    /// <inheritdoc/>
    public Task<string?> GetInstalledVersionAsync(CancellationToken ct = default) => _installer.GetInstalledVersionAsync(ct);
    /// <inheritdoc/>
    public Task<string?> GetLatestStableVersionAsync(CancellationToken ct = default) => _installer.GetLatestStableVersionAsync(ct);
    /// <inheritdoc/>
    public Task<string?> GetChannelVersionAsync(string channel, CancellationToken ct = default) => _installer.GetChannelVersionAsync(channel, ct);
    /// <inheritdoc/>
    public Task<CommandResult> UninstallAsync(Action<string> onOutput, CancellationToken ct = default) => _installer.UninstallAsync(onOutput, ct);
    /// <inheritdoc/>
    public Task<CommandResult> StartAsync(CancellationToken ct = default) => _installer.StartAsync(ct);
    /// <inheritdoc/>
    public Task<CommandResult> StopAsync(CancellationToken ct = default) => _installer.StopAsync(ct);

    // ---- Resource list queries ----
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sNode>> GetNodesAsync(CancellationToken ct = default) => _resources.GetNodesAsync(ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sPod>> GetPodsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetPodsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sDeployment>> GetDeploymentsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetDeploymentsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sService>> GetServicesAsync(string? ns = null, CancellationToken ct = default) => _resources.GetServicesAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sIngress>> GetIngressesAsync(string? ns = null, CancellationToken ct = default) => _resources.GetIngressesAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sPvc>> GetPvcsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetPvcsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sConfigMap>> GetConfigMapsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetConfigMapsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sSecret>> GetSecretsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetSecretsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sJob>> GetJobsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetJobsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<K8sCronJob>> GetCronJobsAsync(string? ns = null, CancellationToken ct = default) => _resources.GetCronJobsAsync(ns, ct);
    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetNamespacesAsync(CancellationToken ct = default) => _resources.GetNamespacesAsync(ct);
    /// <inheritdoc/>
    public Task<CommandResult> ApplyManifestAsync(string yaml, CancellationToken ct = default) => _resources.ApplyManifestAsync(yaml, ct);

    // ---- Single-object actions ----
    /// <inheritdoc/>
    public Task<CommandResult> DeleteResourceAsync(string kind, string ns, string name, CancellationToken ct = default) => _resources.DeleteResourceAsync(kind, ns, name, ct);
    /// <inheritdoc/>
    public Task<CommandResult> ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct = default) => _resources.ScaleDeploymentAsync(ns, name, replicas, ct);
    /// <inheritdoc/>
    public Task<CommandResult> RestartDeploymentAsync(string ns, string name, CancellationToken ct = default) => _resources.RestartDeploymentAsync(ns, name, ct);
    /// <inheritdoc/>
    public Task<CommandResult> SetCronJobSuspendAsync(string ns, string name, bool suspend, CancellationToken ct = default) => _resources.SetCronJobSuspendAsync(ns, name, suspend, ct);
    /// <inheritdoc/>
    public Task<CommandResult> TriggerCronJobAsync(string ns, string name, CancellationToken ct = default) => _resources.TriggerCronJobAsync(ns, name, ct);
    /// <inheritdoc/>
    public Task<CommandResult> GetResourceYamlAsync(string kind, string ns, string name, CancellationToken ct = default) => _resources.GetResourceYamlAsync(kind, ns, name, ct);
    /// <inheritdoc/>
    public Task<CommandResult> DescribeResourceAsync(string kind, string ns, string name, CancellationToken ct = default) => _resources.DescribeResourceAsync(kind, ns, name, ct);
    /// <inheritdoc/>
    public Task<CommandResult> GetPodLogsAsync(string ns, string name, int tailLines, CancellationToken ct = default) => _resources.GetPodLogsAsync(ns, name, tailLines, ct);

    // ---- Port forwarding ----
    /// <inheritdoc/>
    public bool StartPortForward(PortForward forward) => _portForwards.StartPortForward(forward);
    /// <inheritdoc/>
    public void StopPortForward(string id) => _portForwards.StopPortForward(id);
    /// <inheritdoc/>
    public void StopAllPortForwards() => _portForwards.StopAllPortForwards();
}
