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
/// Reads cluster/resource state and performs single-object actions via <c>k3s kubectl</c>.
/// Covers status probes, list queries for each resource kind, apply, and the
/// delete/scale/restart/cron/yaml/describe/logs operations.
/// </summary>
public sealed class K8sResourceClient(WslRootShell shell, ILogger<K8sResourceClient> logger)
{
    // ---- Status ---------------------------------------------------------

    public async Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var script = K8sStatusProtocol.BuildProbeScript(
                K8sStatusProtocol.NodesMarker, "k3s kubectl get nodes -o json");

            var r = await shell.RunAsync(script, ct).ConfigureAwait(false);
            var output = r.StandardOutput;

            if (!r.Success && string.IsNullOrWhiteSpace(output))
            {
                return new ClusterStatus { State = ClusterState.Unknown, Message = r.ErrorText };
            }

            if (K8sStatusProtocol.Contains(output, K8sStatusProtocol.StateNotInstalled))
            {
                return new ClusterStatus { State = ClusterState.NotInstalled, Distro = shell.DistroLabel };
            }

            if (K8sStatusProtocol.Contains(output, K8sStatusProtocol.StateStopped))
            {
                return new ClusterStatus
                {
                    State = ClusterState.Stopped,
                    Distro = shell.DistroLabel,
                    Message = "k3s is installed but not running.",
                };
            }

            // Running: parse the node JSON that followed the nodes marker.
            var nodeJson = K8sStatusProtocol.SectionAfter(output, K8sStatusProtocol.NodesMarker);
            var node = K8sParser.Nodes(nodeJson).FirstOrDefault();

            return new ClusterStatus
            {
                State = ClusterState.Running,
                Distro = shell.DistroLabel,
                NodeName = node?.Name ?? "-",
                KubernetesVersion = node?.Version ?? "-",
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kubernetes cluster status probe failed.");
            return new ClusterStatus { State = ClusterState.Unknown, Message = ex.Message };
        }
    }

    public async Task<K8sFooterStatus> GetFooterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            // Kept separate from GetStatusAsync so the shared StatusMonitor can poll it cheaply on
            // its cadence (and warm the WSL distro early).
            var script = K8sStatusProtocol.BuildProbeScript(
                K8sStatusProtocol.PodsMarker, "k3s kubectl get pods -A -o json");

            var r = await shell.RunAsync(script, ct).ConfigureAwait(false);
            var output = r.StandardOutput;

            if (K8sStatusProtocol.Contains(output, K8sStatusProtocol.StateNotInstalled))
            {
                return new K8sFooterStatus { State = ClusterState.NotInstalled };
            }

            if (K8sStatusProtocol.Contains(output, K8sStatusProtocol.StateStopped))
            {
                return new K8sFooterStatus { State = ClusterState.Stopped };
            }

            if (!K8sStatusProtocol.Contains(output, K8sStatusProtocol.StateRunning))
            {
                return new K8sFooterStatus { State = ClusterState.Unknown };
            }

            var podJson = K8sStatusProtocol.SectionAfter(output, K8sStatusProtocol.PodsMarker);
            var pods = K8sParser.Pods(podJson);

            return new K8sFooterStatus
            {
                State = ClusterState.Running,
                PodsRunning = pods.Count(p => p.IsRunning),
                PodsTotal = pods.Count,
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Kubernetes footer status probe failed.");
            return new K8sFooterStatus { State = ClusterState.Unknown };
        }
    }

    // ---- Resource list queries ------------------------------------------

    public async Task<IReadOnlyList<K8sNode>> GetNodesAsync(CancellationToken ct = default)
    {
        var r = await shell.RunAsync("k3s kubectl get nodes -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Nodes(r.StandardOutput) : Array.Empty<K8sNode>();
    }

    public async Task<IReadOnlyList<K8sPod>> GetPodsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get pods {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Pods(r.StandardOutput) : Array.Empty<K8sPod>();
    }

    public async Task<IReadOnlyList<K8sDeployment>> GetDeploymentsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get deployments {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Deployments(r.StandardOutput) : Array.Empty<K8sDeployment>();
    }

    public async Task<IReadOnlyList<K8sService>> GetServicesAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get services {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Services(r.StandardOutput) : Array.Empty<K8sService>();
    }

    public async Task<IReadOnlyList<K8sIngress>> GetIngressesAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get ingress {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Ingresses(r.StandardOutput) : Array.Empty<K8sIngress>();
    }

    public async Task<IReadOnlyList<K8sPvc>> GetPvcsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get pvc {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Pvcs(r.StandardOutput) : Array.Empty<K8sPvc>();
    }

    public async Task<IReadOnlyList<K8sConfigMap>> GetConfigMapsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get configmaps {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.ConfigMaps(r.StandardOutput) : Array.Empty<K8sConfigMap>();
    }

    public async Task<IReadOnlyList<K8sSecret>> GetSecretsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get secrets {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Secrets(r.StandardOutput) : Array.Empty<K8sSecret>();
    }

    public async Task<IReadOnlyList<K8sJob>> GetJobsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get jobs {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Jobs(r.StandardOutput) : Array.Empty<K8sJob>();
    }

    public async Task<IReadOnlyList<K8sCronJob>> GetCronJobsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get cronjobs {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.CronJobs(r.StandardOutput) : Array.Empty<K8sCronJob>();
    }

    public async Task<IReadOnlyList<string>> GetNamespacesAsync(CancellationToken ct = default)
    {
        var r = await shell.RunAsync("k3s kubectl get namespaces -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Namespaces(r.StandardOutput) : Array.Empty<string>();
    }

    public Task<CommandResult> ApplyManifestAsync(string yaml, CancellationToken ct = default) =>
        shell.RunWithStdinAsync("k3s kubectl apply -f -", yaml, ct);

    // ---- Single-object actions ------------------------------------------

    public Task<CommandResult> DeleteResourceAsync(string kind, string ns, string name, CancellationToken ct = default) =>
        shell.RunAsync($"k3s kubectl delete {WslRootShell.SafeKind(kind)} {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);

    public Task<CommandResult> ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct = default) =>
        shell.RunAsync($"k3s kubectl scale deployment {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} --replicas={replicas}", ct);

    public Task<CommandResult> RestartDeploymentAsync(string ns, string name, CancellationToken ct = default) =>
        shell.RunAsync($"k3s kubectl rollout restart deployment {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);

    public Task<CommandResult> SetCronJobSuspendAsync(string ns, string name, bool suspend, CancellationToken ct = default)
    {
        var patch = suspend ? "{\"spec\":{\"suspend\":true}}" : "{\"spec\":{\"suspend\":false}}";
        return shell.RunAsync($"k3s kubectl patch cronjob {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} -p {WslRootShell.ShellEscape(patch)}", ct);
    }

    public Task<CommandResult> TriggerCronJobAsync(string ns, string name, CancellationToken ct = default)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jobName = $"{name}-manual-{stamp}";
        return shell.RunAsync(
            $"k3s kubectl create job {WslRootShell.ShellEscape(jobName)} --from=cronjob/{WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);
    }

    public async Task<CommandResult> GetResourceYamlAsync(string kind, string ns, string name, CancellationToken ct = default)
    {
        var r = await shell.RunAsync($"k3s kubectl get {WslRootShell.SafeKind(kind)} {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} -o yaml", ct)
            .ConfigureAwait(false);

        if (!r.Success)
        {
            return r;
        }

        // Return a manifest that is safe to `kubectl apply` again: drop the read-only
        // status block and server-managed metadata that otherwise triggers strict-decode
        // or optimistic-concurrency errors when the edited YAML is re-applied.
        return new CommandResult
        {
            ExitCode = r.ExitCode,
            StandardOutput = K8sManifestSanitizer.Sanitize(r.StandardOutput),
            StandardError = r.StandardError,
        };
    }

    public Task<CommandResult> DescribeResourceAsync(string kind, string ns, string name, CancellationToken ct = default) =>
        shell.RunAsync($"k3s kubectl describe {WslRootShell.SafeKind(kind)} {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);

    public Task<CommandResult> GetPodLogsAsync(string ns, string name, int tailLines, CancellationToken ct = default) =>
        shell.RunAsync($"k3s kubectl logs {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} --all-containers=true --tail={tailLines}", ct);
}

