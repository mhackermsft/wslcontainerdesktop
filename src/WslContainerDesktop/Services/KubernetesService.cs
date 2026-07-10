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
using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Manages a single-node k3s Kubernetes cluster inside a WSL distro. All privileged
/// operations run via `wsl.exe -u root`, which does not require a Linux password. k3s
/// bundles kubectl (`k3s kubectl`), so no external kubectl or kubeconfig is needed.
/// </summary>
public sealed class KubernetesService : IKubernetesService
{
    private readonly ISettingsService _settings;

    public KubernetesService(ISettingsService settings)
    {
        _settings = settings;
    }

    private string? Distro => string.IsNullOrWhiteSpace(_settings.WslDistro) ? null : _settings.WslDistro;

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

            var r = await RunRootAsync(script, ct).ConfigureAwait(false);
            var output = r.StandardOutput;

            if (!r.Success && string.IsNullOrWhiteSpace(output))
            {
                return new ClusterStatus { State = ClusterState.Unknown, Message = r.ErrorText };
            }

            if (output.Contains("@@STATE=notinstalled", StringComparison.Ordinal))
            {
                return new ClusterStatus { State = ClusterState.NotInstalled, Distro = Distro ?? "default" };
            }

            if (output.Contains("@@STATE=stopped", StringComparison.Ordinal))
            {
                return new ClusterStatus
                {
                    State = ClusterState.Stopped,
                    Distro = Distro ?? "default",
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
                Distro = Distro ?? "default",
                NodeName = node?.Name ?? "-",
                KubernetesVersion = node?.Version ?? "-",
            };
        }
        catch (Exception ex)
        {
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

            var r = await RunRootAsync(script, ct).ConfigureAwait(false);
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
        catch
        {
            return new K8sFooterStatus { State = ClusterState.Unknown };
        }
    }

    // ---- Install / uninstall (streaming) --------------------------------

    public Task<CommandResult> InstallAsync(Action<string> onOutput, CancellationToken ct = default) =>
        RunRootStreamingAsync("curl -sfL https://get.k3s.io | INSTALL_K3S_SKIP_SELINUX_RPM=true sh -", onOutput, ct);

    public Task<CommandResult> UpgradeAsync(string? version, Action<string> onOutput, CancellationToken ct = default)
    {
        // Re-running the install script performs an in-place upgrade: it swaps the k3s
        // binary and restarts the service, preserving cluster data and workloads. Omitting
        // INSTALL_K3S_VERSION tracks the latest stable channel; setting it pins a version.
        var versionEnv = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : $"INSTALL_K3S_VERSION={ShellEscape(version)} ";
        return RunRootStreamingAsync(
            $"curl -sfL https://get.k3s.io | {versionEnv}INSTALL_K3S_SKIP_SELINUX_RPM=true sh -",
            onOutput, ct);
    }

    public async Task<string?> GetInstalledVersionAsync(CancellationToken ct = default)
    {
        var r = await RunRootAsync("k3s --version 2>/dev/null | head -n1", ct).ConfigureAwait(false);
        if (!r.Success)
        {
            return null;
        }

        // Line looks like: "k3s version v1.36.2+k3s1 (01b6f04a)"
        var match = Regex.Match(r.StandardOutput, @"v\d+\.\d+\.\d+\+k3s\d+");
        return match.Success ? match.Value : null;
    }

    public async Task<string?> GetLatestStableVersionAsync(CancellationToken ct = default) =>
        await GetChannelVersionAsync("stable", ct).ConfigureAwait(false);

    public async Task<string?> GetChannelVersionAsync(string channel, CancellationToken ct = default)
    {
        // The channel server 302-redirects to the GitHub release for the channel's current tag.
        var r = await RunRootAsync(
            $"curl -s -o /dev/null -w '%{{redirect_url}}' https://update.k3s.io/v1-release/channels/{ShellEscape(channel)}", ct)
            .ConfigureAwait(false);
        if (!r.Success)
        {
            return null;
        }

        var match = Regex.Match(r.StandardOutput, @"v\d+\.\d+\.\d+\+k3s\d+");
        return match.Success ? match.Value : null;
    }

    public Task<CommandResult> UninstallAsync(Action<string> onOutput, CancellationToken ct = default) =>
        RunRootStreamingAsync(
            "if [ -f /usr/local/bin/k3s-uninstall.sh ]; then /usr/local/bin/k3s-uninstall.sh; else echo 'k3s already removed'; fi",
            onOutput, ct);

    public Task<CommandResult> StartAsync(CancellationToken ct = default) =>
        RunRootAsync("systemctl start k3s", ct);

    public Task<CommandResult> StopAsync(CancellationToken ct = default) =>
        RunRootAsync("systemctl stop k3s", ct);

    // ---- Resources ------------------------------------------------------

    /// <summary>Returns the `-n ns` or `--all-namespaces` selector for a query.</summary>
    private static string NsSelector(string? ns) =>
        string.IsNullOrWhiteSpace(ns) || ns == "All namespaces"
            ? "--all-namespaces"
            : $"-n {ShellEscape(ns)}";

    private static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public async Task<IReadOnlyList<K8sNode>> GetNodesAsync(CancellationToken ct = default)
    {
        var r = await RunRootAsync("k3s kubectl get nodes -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Nodes(r.StandardOutput) : Array.Empty<K8sNode>();
    }

    public async Task<IReadOnlyList<K8sPod>> GetPodsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get pods {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Pods(r.StandardOutput) : Array.Empty<K8sPod>();
    }

    public async Task<IReadOnlyList<K8sDeployment>> GetDeploymentsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get deployments {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Deployments(r.StandardOutput) : Array.Empty<K8sDeployment>();
    }

    public async Task<IReadOnlyList<K8sService>> GetServicesAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get services {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Services(r.StandardOutput) : Array.Empty<K8sService>();
    }

    public async Task<IReadOnlyList<K8sIngress>> GetIngressesAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get ingress {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Ingresses(r.StandardOutput) : Array.Empty<K8sIngress>();
    }

    public async Task<IReadOnlyList<K8sPvc>> GetPvcsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get pvc {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Pvcs(r.StandardOutput) : Array.Empty<K8sPvc>();
    }

    public async Task<IReadOnlyList<K8sConfigMap>> GetConfigMapsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get configmaps {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.ConfigMaps(r.StandardOutput) : Array.Empty<K8sConfigMap>();
    }

    public async Task<IReadOnlyList<K8sSecret>> GetSecretsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get secrets {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Secrets(r.StandardOutput) : Array.Empty<K8sSecret>();
    }

    public async Task<IReadOnlyList<K8sJob>> GetJobsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get jobs {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Jobs(r.StandardOutput) : Array.Empty<K8sJob>();
    }

    public async Task<IReadOnlyList<K8sCronJob>> GetCronJobsAsync(string? ns = null, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get cronjobs {NsSelector(ns)} -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.CronJobs(r.StandardOutput) : Array.Empty<K8sCronJob>();
    }

    public async Task<IReadOnlyList<string>> GetNamespacesAsync(CancellationToken ct = default)
    {
        var r = await RunRootAsync("k3s kubectl get namespaces -o json", ct).ConfigureAwait(false);
        return r.Success ? K8sParser.Namespaces(r.StandardOutput) : Array.Empty<string>();
    }

    public Task<CommandResult> ApplyManifestAsync(string yaml, CancellationToken ct = default) =>
        RunRootWithStdinAsync("k3s kubectl apply -f -", yaml, ct);

    // ---- Single-object actions ------------------------------------------

    /// <summary>Returns the `-n ns` argument for a namespaced object, or empty for cluster-scoped.</summary>
    private static string NsArg(string ns) =>
        string.IsNullOrWhiteSpace(ns) ? string.Empty : $" -n {ShellEscape(ns)}";

    public Task<CommandResult> DeleteResourceAsync(string kind, string ns, string name, CancellationToken ct = default) =>
        RunRootAsync($"k3s kubectl delete {kind} {ShellEscape(name)}{NsArg(ns)}", ct);

    public Task<CommandResult> ScaleDeploymentAsync(string ns, string name, int replicas, CancellationToken ct = default) =>
        RunRootAsync($"k3s kubectl scale deployment {ShellEscape(name)}{NsArg(ns)} --replicas={replicas}", ct);

    public Task<CommandResult> RestartDeploymentAsync(string ns, string name, CancellationToken ct = default) =>
        RunRootAsync($"k3s kubectl rollout restart deployment {ShellEscape(name)}{NsArg(ns)}", ct);

    public Task<CommandResult> SetCronJobSuspendAsync(string ns, string name, bool suspend, CancellationToken ct = default)
    {
        var patch = suspend ? "{\"spec\":{\"suspend\":true}}" : "{\"spec\":{\"suspend\":false}}";
        return RunRootAsync($"k3s kubectl patch cronjob {ShellEscape(name)}{NsArg(ns)} -p {ShellEscape(patch)}", ct);
    }

    public Task<CommandResult> TriggerCronJobAsync(string ns, string name, CancellationToken ct = default)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var jobName = $"{name}-manual-{stamp}";
        return RunRootAsync(
            $"k3s kubectl create job {ShellEscape(jobName)} --from=cronjob/{ShellEscape(name)}{NsArg(ns)}", ct);
    }

    public async Task<CommandResult> GetResourceYamlAsync(string kind, string ns, string name, CancellationToken ct = default)
    {
        var r = await RunRootAsync($"k3s kubectl get {kind} {ShellEscape(name)}{NsArg(ns)} -o yaml", ct)
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
            StandardOutput = SanitizeManifest(r.StandardOutput),
            StandardError = r.StandardError,
        };
    }

    /// <summary>
    /// Strips fields that make a live `kubectl get -o yaml` manifest un-appliable:
    /// the top-level <c>status:</c> block, the <c>metadata.managedFields</c> block, and
    /// server-managed identity/version keys (resourceVersion/uid/creationTimestamp/generation/selfLink).
    /// </summary>
    private static string SanitizeManifest(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var skipStatus = false;
        var managedIndent = -1;

        foreach (var line in lines)
        {
            // status: is emitted last at column 0 — drop it and everything after.
            if (line.StartsWith("status:", StringComparison.Ordinal))
            {
                skipStatus = true;
            }

            if (skipStatus)
            {
                continue;
            }

            // Skip a nested managedFields: block by indentation.
            if (managedIndent >= 0)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;
                if (indent > managedIndent)
                {
                    continue;
                }

                managedIndent = -1;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("managedFields:", StringComparison.Ordinal))
            {
                managedIndent = line.Length - trimmed.Length;
                continue;
            }

            if (Regex.IsMatch(line, @"^\s+(resourceVersion|uid|creationTimestamp|generation|selfLink):"))
            {
                continue;
            }

            sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    public Task<CommandResult> DescribeResourceAsync(string kind, string ns, string name, CancellationToken ct = default) =>
        RunRootAsync($"k3s kubectl describe {kind} {ShellEscape(name)}{NsArg(ns)}", ct);

    public Task<CommandResult> GetPodLogsAsync(string ns, string name, int tailLines, CancellationToken ct = default) =>
        RunRootAsync($"k3s kubectl logs {ShellEscape(name)}{NsArg(ns)} --all-containers=true --tail={tailLines}", ct);

    // ---- Port forwarding ------------------------------------------------

    private readonly Dictionary<string, Process> _portForwards = new();
    private readonly object _pfLock = new();

    public bool StartPortForward(PortForward forward)
    {
        var cmd = $"k3s kubectl port-forward --address 127.0.0.1 -n {ShellEscape(forward.Namespace)} " +
                  $"{forward.TargetRef} {forward.LocalPort}:{forward.RemotePort}";

        var psi = BaseStartInfo(cmd);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }

            // Drain output so the pipe doesn't fill and block the forward.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };

            lock (_pfLock)
            {
                _portForwards[forward.Id] = process;
            }

            return true;
        }
        catch
        {
            process.Dispose();
            return false;
        }
    }

    public void StopPortForward(string id)
    {
        Process? process;
        lock (_pfLock)
        {
            if (!_portForwards.TryGetValue(id, out process))
            {
                return;
            }

            _portForwards.Remove(id);
        }

        KillProcessTree(process);
    }

    public void StopAllPortForwards()
    {
        List<Process> all;
        lock (_pfLock)
        {
            all = _portForwards.Values.ToList();
            _portForwards.Clear();
        }

        foreach (var p in all)
        {
            KillProcessTree(p);
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            process.Dispose();
        }
    }

    // ---- Process plumbing ----------------------------------------------

    private ProcessStartInfo BaseStartInfo(string bashCommand)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Force wsl.exe to emit UTF-8 rather than UTF-16LE.
        psi.Environment["WSL_UTF8"] = "1";

        if (Distro is not null)
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(Distro);
        }

        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("root");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(bashCommand);

        return psi;
    }

    private async Task<CommandResult> RunRootAsync(string bashCommand, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BaseStartInfo(bashCommand) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult { ExitCode = -1, StandardError = $"Could not launch wsl.exe. {ex.Message}" };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }

    private async Task<CommandResult> RunRootWithStdinAsync(string bashCommand, string stdin, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BaseStartInfo(bashCommand) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult { ExitCode = -1, StandardError = $"Could not launch wsl.exe. {ex.Message}" };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }

    private async Task<CommandResult> RunRootStreamingAsync(string bashCommand, Action<string> onOutput, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BaseStartInfo(bashCommand) };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
                onOutput(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult { ExitCode = -1, StandardError = $"Could not launch wsl.exe. {ex.Message}" };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
        };
    }
}
