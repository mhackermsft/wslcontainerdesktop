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
public sealed class K8sResourceClient
{
    private readonly WslRootShell _shell;
    private readonly ILogger<K8sResourceClient> _logger;

    public K8sResourceClient(WslRootShell shell, ILogger<K8sResourceClient> logger)
    {
        _shell = shell;
        _logger = logger;
    }

    // ---- Status ---------------------------------------------------------

    public async Task<ClusterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            // Do the whole status probe in ONE wsl.exe invocation. Each separate wsl.exe
            // call pays cold-start/distro-attach overhead, so chaining install-check +
            // service-state + node JSON in a single shell script dramatically speeds up the
            // first load of the Kubernetes page.
            const string script =
                "if [ ! -f /usr/local/bin/k3s-uninstall.sh ]; then echo '@@STATE=notinstalled'; exit 0; fi; " +
                "a=$(systemctl is-active k3s 2>/dev/null || true); " +
                "if [ \"$a\" != active ]; then echo '@@STATE=stopped'; exit 0; fi; " +
                "echo '@@STATE=running'; echo '@@NODES'; k3s kubectl get nodes -o json 2>/dev/null";

            var r = await _shell.RunAsync(script, ct).ConfigureAwait(false);
            var output = r.StandardOutput;

            if (!r.Success && string.IsNullOrWhiteSpace(output))
            {
                return new ClusterStatus { State = ClusterState.Unknown, Message = r.ErrorText };
            }

            if (output.Contains("@@STATE=notinstalled", StringComparison.Ordinal))
            {
                return new ClusterStatus { State = ClusterState.NotInstalled, Distro = _shell.DistroLabel };
            }

            if (output.Contains("@@STATE=stopped", StringComparison.Ordinal))
            {
                return new ClusterStatus
                {
                    State = ClusterState.Stopped,
                    Distro = _shell.DistroLabel,
                    Message = "k3s is installed but not running.",
                };
            }

            // Running: parse the node JSON that followed the @@NODES marker.
            var idx = output.IndexOf("@@NODES", StringComparison.Ordinal);
            var node = idx >= 0
                ? K8sParser.Nodes(output[(idx + "@@NODES".Length)..]).FirstOrDefault()
                : null;

            return new ClusterStatus
            {
                State = ClusterState.Running,
                Distro = _shell.DistroLabel,
                NodeName = node?.Name ?? "-",
                KubernetesVersion = node?.Version ?? "-",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kubernetes cluster status probe failed.");
            return new ClusterStatus { State = ClusterState.Unknown, Message = ex.Message };
        }
    }

    public async Task<K8sFooterStatus> GetFooterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            // Single-call lightweight probe for the nav footer indicator: install state and,
            // when running, pod counts. Kept separate from GetStatusAsync so the shared
            // StatusMonitor can poll it cheaply on its cadence (and warm the WSL distro early).
            const string script =
                "if [ ! -f /usr/local/bin/k3s-uninstall.sh ]; then echo '@@STATE=notinstalled'; exit 0; fi; " +
                "a=$(systemctl is-active k3s 2>/dev/null || true); " +
                "if [ \"$a\" != active ]; then echo '@@STATE=stopped'; exit 0; fi; " +
                "echo '@@STATE=running'; echo '@@PODS'; k3s kubectl get pods -A -o json 2>/dev/null";

            var r = await _shell.RunAsync(script, ct).ConfigureAwait(false);
            var output = r.StandardOutput;

            if (output.Contains("@@STATE=notinstalled", StringComparison.Ordinal))
            {
                return new K8sFooterStatus { State = ClusterState.NotInstalled };
            }

            if (output.Contains("@@STATE=stopped", StringComparison.Ordinal))
            {
                return new K8sFooterStatus { State = ClusterState.Stopped };
            }

            if (!output.Contains("@@STATE=running", StringComparison.Ordinal))
            {
                return new K8sFooterStatus { State = ClusterState.Unknown };
            }

            var idx = output.IndexOf("@@PODS", StringComparison.Ordinal);
            var pods = idx >= 0
                ? K8sParser.Pods(output[(idx + "@@PODS".Length)..])
                : new List<K8sPod>();

            return new K8sFooterStatus
            {
                State = ClusterState.Running,
                PodsRunning = pods.Count(p => p.IsRunning),
                PodsTotal = pods.Count,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kubernetes footer status probe failed.");
            return new K8sFooterStatus { State = ClusterState.Unknown };
        }
    }

    // ---- Resource list queries ------------------------------------------

    public async Task<IReadOnlyList<K8sNode>> GetNodesAsync(CancellationToken ct = default)
    {
        var r = await _shell.RunAsync("k3s kubectl get nodes -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Nodes(r.StandardOutput) : Array.Empty<K8sNode>();
    }

    public async Task<IReadOnlyList<K8sPod>> GetPodsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get pods {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Pods(r.StandardOutput) : Array.Empty<K8sPod>();
    }

    public async Task<IReadOnlyList<K8sDeployment>> GetDeploymentsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get deployments {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Deployments(r.StandardOutput) : Array.Empty<K8sDeployment>();
    }

    public async Task<IReadOnlyList<K8sService>> GetServicesAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get services {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Services(r.StandardOutput) : Array.Empty<K8sService>();
    }

    public async Task<IReadOnlyList<K8sIngress>> GetIngressesAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get ingress {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Ingresses(r.StandardOutput) : Array.Empty<K8sIngress>();
    }

    public async Task<IReadOnlyList<K8sPvc>> GetPvcsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get pvc {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Pvcs(r.StandardOutput) : Array.Empty<K8sPvc>();
    }

    public async Task<IReadOnlyList<K8sConfigMap>> GetConfigMapsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get configmaps {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.ConfigMaps(r.StandardOutput) : Array.Empty<K8sConfigMap>();
    }

    public async Task<IReadOnlyList<K8sSecret>> GetSecretsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get secrets {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Secrets(r.StandardOutput) : Array.Empty<K8sSecret>();
    }

    public async Task<IReadOnlyList<K8sJob>> GetJobsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get jobs {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Jobs(r.StandardOutput) : Array.Empty<K8sJob>();
    }

    public async Task<IReadOnlyList<K8sCronJob>> GetCronJobsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get cronjobs {WslRootShell.NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.CronJobs(r.StandardOutput) : Array.Empty<K8sCronJob>();
    }

    public async Task<IReadOnlyList<string>> GetNamespacesAsync(CancellationToken ct = default)
    {
        var r = await _shell.RunAsync("k3s kubectl get namespaces -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Namespaces(r.StandardOutput) : Array.Empty<string>();
    }

    public Task<CommandResult> ApplyManifestAsync(string yaml, CancellationToken ct = default) =>
        _shell.RunWithStdinAsync("k3s kubectl apply -f -", yaml, ct);

    // ---- Single-object actions ------------------------------------------

    public Task<CommandResult> DeleteResourceAsync(string kind, string ns, string name, CancellationToken ct = default) =>
        _shell.RunAsync($"k3s kubectl delete {WslRootShell.SafeKind(kind)} {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);

    public Task<CommandResult> ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct = default) =>
        _shell.RunAsync($"k3s kubectl scale deployment {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} --replicas={replicas}", ct);

    public Task<CommandResult> RestartDeploymentAsync(string ns, string name, CancellationToken ct = default) =>
        _shell.RunAsync($"k3s kubectl rollout restart deployment {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);

    public Task<CommandResult> SetCronJobSuspendAsync(string ns, string name, bool suspend, CancellationToken ct = default)
    {
        var patch = suspend ? "{\"spec\":{\"suspend\":true}}" : "{\"spec\":{\"suspend\":false}}";
        return _shell.RunAsync($"k3s kubectl patch cronjob {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} -p {WslRootShell.ShellEscape(patch)}", ct);
    }

    public Task<CommandResult> TriggerCronJobAsync(string ns, string name, CancellationToken ct = default)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jobName = $"{name}-manual-{stamp}";
        return _shell.RunAsync(
            $"k3s kubectl create job {WslRootShell.ShellEscape(jobName)} --from=cronjob/{WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);
    }

    public async Task<CommandResult> GetResourceYamlAsync(string kind, string ns, string name, CancellationToken ct = default)
    {
        var r = await _shell.RunAsync($"k3s kubectl get {WslRootShell.SafeKind(kind)} {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} -o yaml", ct)
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
        _shell.RunAsync($"k3s kubectl describe {WslRootShell.SafeKind(kind)} {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)}", ct);

    public Task<CommandResult> GetPodLogsAsync(string ns, string name, int tailLines, CancellationToken ct = default) =>
        _shell.RunAsync($"k3s kubectl logs {WslRootShell.ShellEscape(name)}{WslRootShell.NsArg(ns)} --all-containers=true --tail={tailLines}", ct);
}
