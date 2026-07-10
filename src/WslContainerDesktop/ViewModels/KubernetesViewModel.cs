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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class KubernetesViewModel : ObservableObject
{
    private readonly IKubernetesService _k8s;
    private readonly DialogService _dialogs;
    private readonly StatusMonitor _monitor;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _pollCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private ClusterState _state = ClusterState.Unknown;

    [ObservableProperty]
    private string _statusMessage = "Checking cluster status…";

    [ObservableProperty]
    private string _nodeName = "-";

    [ObservableProperty]
    private string _kubernetesVersion = "-";

    [ObservableProperty]
    private string _distro = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _working;

    [ObservableProperty]
    private string _operationLog = string.Empty;

    [ObservableProperty]
    private bool _showOperationLog;

    // ---- Sub-navigation + namespace filter ----
    [ObservableProperty]
    private string _selectedSection = "Dashboard";

    [ObservableProperty]
    private string _selectedNamespace = "All namespaces";

    public ObservableCollection<string> Namespaces { get; } = new();

    // ---- Resource collections ----
    public ObservableCollection<K8sNode> Nodes { get; } = new();
    public ObservableCollection<K8sPod> Pods { get; } = new();
    public ObservableCollection<K8sDeployment> Deployments { get; } = new();
    public ObservableCollection<K8sService> Services { get; } = new();
    public ObservableCollection<K8sIngress> Ingresses { get; } = new();
    public ObservableCollection<K8sPvc> Pvcs { get; } = new();
    public ObservableCollection<K8sConfigMap> ConfigMaps { get; } = new();
    public ObservableCollection<K8sSecret> Secrets { get; } = new();
    public ObservableCollection<K8sJob> Jobs { get; } = new();
    public ObservableCollection<K8sCronJob> CronJobs { get; } = new();

    /// <summary>Active port-forward sessions managed by the app.</summary>
    public ObservableCollection<PortForward> PortForwards { get; } = new();

    // ---- Dashboard metric counts ----
    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private int _nodeActiveCount;

    [ObservableProperty]
    private int _deploymentCount;

    [ObservableProperty]
    private int _deploymentActiveCount;

    [ObservableProperty]
    private int _podCount;

    [ObservableProperty]
    private int _serviceCount;

    [ObservableProperty]
    private int _ingressCount;

    [ObservableProperty]
    private int _pvcCount;

    [ObservableProperty]
    private int _configMapCount;

    [ObservableProperty]
    private int _secretCount;

    [ObservableProperty]
    private int _jobCount;

    [ObservableProperty]
    private int _cronJobCount;

    public bool IsNotInstalled => !Working && State == ClusterState.NotInstalled;
    public bool IsInstalled => !Working && State is ClusterState.Stopped or ClusterState.Running;
    public bool IsRunning => !Working && State == ClusterState.Running;
    public bool IsStopped => !Working && State == ClusterState.Stopped;
    public bool IsBusy => Working;

    public event Action? OperationLogUpdated;

    public KubernetesViewModel(IKubernetesService k8s, DialogService dialogs, StatusMonitor monitor)
    {
        _k8s = k8s;
        _dialogs = dialogs;
        _monitor = monitor;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Seed the default namespace option so the ComboBox shows a selection immediately.
        Namespaces.Add("All namespaces");
    }

    partial void OnSelectedNamespaceChanged(string value)
    {
        // Re-poll immediately so namespaced views reflect the new filter.
        if (State == ClusterState.Running)
        {
            _ = PollOnceAsync(CancellationToken.None);
        }
    }

    public async Task InitializeAsync()
    {
        // Seed from the shared monitor's cached snapshot so the correct view (install hero,
        // stopped card, or the running sub-nav) appears instantly instead of after the full
        // status probe. The authoritative GetStatusAsync below then fills in node/version.
        var cached = _monitor.LatestK8s;
        if (cached is not null && State == ClusterState.Unknown)
        {
            State = cached.State;
            StatusMessage = cached.Summary;
            if (cached.State == ClusterState.Running)
            {
                StartPolling();
            }
        }

        await RefreshStatusAsync();
        if (State == ClusterState.Running)
        {
            StartPolling();
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        var status = await _k8s.GetStatusAsync();
        Apply(status);
    }

    private void Apply(ClusterStatus status)
    {
        State = status.State;
        Distro = status.Distro;
        NodeName = status.NodeName;
        KubernetesVersion = status.KubernetesVersion;
        StatusMessage = status.State switch
        {
            ClusterState.NotInstalled => "Kubernetes (k3s) is not installed.",
            ClusterState.Stopped => "Cluster is installed but stopped.",
            ClusterState.Running => $"Cluster running · node {status.NodeName} · {status.KubernetesVersion}",
            ClusterState.Unknown => string.IsNullOrEmpty(status.Message) ? "Unable to determine status." : status.Message,
            _ => status.Message,
        };
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Install Kubernetes",
            "This installs k3s (a lightweight single-node Kubernetes) into your WSL distro. " +
            "It runs as a systemd service and can be uninstalled later. Continue?",
            "Install");
        if (!ok)
        {
            return;
        }

        Working = true;
        ShowOperationLog = true;
        OperationLog = string.Empty;
        StatusMessage = "Installing k3s… this can take a few minutes.";

        try
        {
            var result = await _k8s.InstallAsync(AppendLog);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Install failed", result.ErrorText);
            }
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
            if (State == ClusterState.Running)
            {
                StartPolling();
            }
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Uninstall Kubernetes",
            "This stops and completely removes k3s and all cluster data from your WSL distro. " +
            "Running workloads will be destroyed. This cannot be undone. Continue?",
            "Uninstall");
        if (!ok)
        {
            return;
        }

        StopPolling();
        ClearPortForwards();
        Working = true;
        ShowOperationLog = true;
        OperationLog = string.Empty;
        StatusMessage = "Uninstalling k3s and cleaning up…";

        try
        {
            var result = await _k8s.UninstallAsync(AppendLog);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Uninstall failed", result.ErrorText);
            }

            Nodes.Clear();
            Pods.Clear();
            Deployments.Clear();
            Services.Clear();
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async Task UpgradeAsync()
    {
        // Detect current + latest versions to inform the dialog.
        var currentTask = _k8s.GetInstalledVersionAsync();
        var latestTask = _k8s.GetLatestStableVersionAsync();
        await Task.WhenAll(currentTask, latestTask);

        var currentStr = currentTask.Result ?? (KubernetesVersion == "-" ? "unknown" : KubernetesVersion);
        var latest = latestTask.Result;

        var dialog = new UpgradeK3sDialog(currentStr, latest);
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        // Resolve the concrete target tag: either the pinned version or the latest stable.
        // installVersion is what we hand to the install script: null == track latest stable.
        var targetStr = dialog.TargetVersion ?? latest;
        var installVersion = dialog.TargetVersion;

        // Enforce the Kubernetes version-skew policy: k3s upgrades must not skip an
        // intermediate minor version (https://docs.k3s.io/upgrades/manual). If the target
        // jumps more than one minor ahead, step to the next minor's latest patch instead.
        if (K3sVersion.TryParse(currentStr, out var cur) &&
            K3sVersion.TryParse(targetStr, out var tgt))
        {
            if (tgt < cur)
            {
                await _dialogs.ShowMessageAsync(
                    "Downgrade not supported",
                    $"The selected version {targetStr} is older than the installed version {cur.Original}. " +
                    "k3s does not support downgrades; pick the same or a newer version.");
                return;
            }

            if (tgt.Major == cur.Major && tgt.Minor > cur.Minor + 1)
            {
                var nextChannel = $"v{cur.Major}.{cur.Minor + 1}";
                var stepVersion = await _k8s.GetChannelVersionAsync(nextChannel);
                if (stepVersion is null)
                {
                    await _dialogs.ShowMessageAsync(
                        "Cannot determine next version",
                        $"Upgrading from {cur.Original} to {targetStr} would skip intermediate minor versions, " +
                        $"which is not supported. Could not resolve the {nextChannel} channel to step through. " +
                        "Check your network and try again.");
                    return;
                }

                var proceed = await _dialogs.ShowConfirmAsync(
                    "Upgrade one minor version at a time",
                    $"You're on {cur.Original}. Upgrading straight to {targetStr} would skip intermediate minor " +
                    $"versions (v{cur.Major}.{cur.Minor + 1} … v{tgt.Major}.{tgt.Minor - 1}), which the Kubernetes " +
                    $"version-skew policy does not allow.\n\n" +
                    $"Upgrade to {stepVersion} first instead? You can repeat the upgrade afterwards to continue toward {targetStr}.",
                    "Upgrade to next minor");
                if (!proceed)
                {
                    return;
                }

                installVersion = stepVersion;
            }
        }

        StopPolling();
        Working = true;
        ShowOperationLog = true;
        OperationLog = string.Empty;
        StatusMessage = installVersion is null
            ? "Upgrading k3s to the latest stable release…"
            : $"Installing k3s {installVersion}…";

        try
        {
            var upgrade = await _k8s.UpgradeAsync(installVersion, AppendLog);
            if (!upgrade.Success)
            {
                await _dialogs.ShowMessageAsync("Upgrade failed", upgrade.ErrorText);
            }
            else
            {
                AppendLog(string.Empty);
                AppendLog("Upgrade complete.");
            }
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
            if (State == ClusterState.Running)
            {
                StartPolling();
            }
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        Working = true;
        StatusMessage = "Starting cluster…";
        try
        {
            var result = await _k8s.StartAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Failed to start", result.ErrorText);
            }

            // Give k3s a moment to bring the node up.
            await Task.Delay(3000);
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
            if (State == ClusterState.Running)
            {
                StartPolling();
            }
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        StopPolling();
        Working = true;
        StatusMessage = "Stopping cluster…";
        try
        {
            var result = await _k8s.StopAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Failed to stop", result.ErrorText);
            }

            Nodes.Clear();
            Pods.Clear();
            Deployments.Clear();
            Services.Clear();
            ClearPortForwards();
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
        }
    }

    // ---- Apply YAML ----------------------------------------------------

    [RelayCommand]
    private async Task ApplyYamlAsync()
    {
        var dialog = new ApplyYamlDialog();
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        var applied = await _k8s.ApplyManifestAsync(dialog.Yaml);
        if (applied.Success)
        {
            var summary = string.IsNullOrWhiteSpace(applied.StandardOutput)
                ? "Manifest applied."
                : applied.StandardOutput.Trim();
            await _dialogs.ShowMessageAsync("Manifest applied", summary);

            // Refresh immediately so the new objects show up without waiting for the next poll.
            if (State == ClusterState.Running)
            {
                await PollOnceAsync(CancellationToken.None);
            }
        }
        else
        {
            await _dialogs.ShowMessageAsync("Apply failed", applied.ErrorText);
        }
    }

    // ---- Resource row actions ------------------------------------------

    public async Task DeleteResourceAsync(K8sResourceRef reference)
    {
        var ok = await _dialogs.ShowConfirmAsync(
            $"Delete {reference.DisplayKind}",
            $"Delete {reference.DisplayKind.ToLowerInvariant()} \"{reference.Name}\"? This cannot be undone.",
            "Delete");
        if (!ok)
        {
            return;
        }

        var result = await _k8s.DeleteResourceAsync(reference.Kind, reference.Namespace, reference.Name);
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Delete failed", result.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    public async Task ScaleDeploymentAsync(K8sResourceRef reference)
    {
        var dialog = new SimpleInputDialog($"Scale {reference.Name}", "Desired replicas", "e.g. 3");
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            !int.TryParse(dialog.Value.Trim(), out var replicas) || replicas < 0)
        {
            return;
        }

        var scaled = await _k8s.ScaleDeploymentAsync(reference.Namespace, reference.Name, replicas);
        if (!scaled.Success)
        {
            await _dialogs.ShowMessageAsync("Scale failed", scaled.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    public async Task RestartDeploymentAsync(K8sResourceRef reference)
    {
        var restarted = await _k8s.RestartDeploymentAsync(reference.Namespace, reference.Name);
        if (!restarted.Success)
        {
            await _dialogs.ShowMessageAsync("Restart failed", restarted.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    public async Task SetCronSuspendAsync(K8sResourceRef reference, bool suspend)
    {
        var result = await _k8s.SetCronJobSuspendAsync(reference.Namespace, reference.Name, suspend);
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Failed", result.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    public async Task TriggerCronAsync(K8sResourceRef reference)
    {
        var result = await _k8s.TriggerCronJobAsync(reference.Namespace, reference.Name);
        if (result.Success)
        {
            await _dialogs.ShowMessageAsync(
                "Job started",
                string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? "A one-off job was created from this cronjob."
                    : result.StandardOutput.Trim());
            await PollOnceAsync(CancellationToken.None);
        }
        else
        {
            await _dialogs.ShowMessageAsync("Trigger failed", result.ErrorText);
        }
    }

    // ---- Port forwarding -----------------------------------------------

    [RelayCommand]
    private async Task AddPortForwardAsync()
    {
        var pods = Pods.Select(p => (p.Namespace, p.Name)).ToList();
        var services = Services.Select(s => (s.Namespace, s.Name)).ToList();

        var dialog = new PortForwardDialog(pods, services);
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || dialog.Result is null)
        {
            return;
        }

        var forward = dialog.Result;
        if (PortForwards.Any(f => f.LocalPort == forward.LocalPort))
        {
            await _dialogs.ShowMessageAsync("Port in use",
                $"Local port {forward.LocalPort} is already being forwarded.");
            return;
        }

        if (_k8s.StartPortForward(forward))
        {
            PortForwards.Add(forward);
        }
        else
        {
            await _dialogs.ShowMessageAsync("Port forward failed",
                "Could not start the port-forward. Check that the target and ports are valid.");
        }
    }

    [RelayCommand]
    private void StopPortForward(PortForward? forward)
    {
        if (forward is null)
        {
            return;
        }

        _k8s.StopPortForward(forward.Id);
        PortForwards.Remove(forward);
    }

    [RelayCommand]
    private void OpenPortForward(PortForward? forward)
    {
        if (forward is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = forward.LocalUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore browser launch failures
        }
    }

    private void ClearPortForwards()
    {
        _k8s.StopAllPortForwards();
        PortForwards.Clear();
    }

    private void AppendLog(string line)
    {
        _dispatcher.TryEnqueue(() =>
        {
            OperationLog += line + "\n";
            OperationLogUpdated?.Invoke();
        });
    }

    // ---- Resource polling ----------------------------------------------

    public void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            // Load namespaces once up front.
            try
            {
                var nsList = await _k8s.GetNamespacesAsync(token).ConfigureAwait(false);
                _dispatcher.TryEnqueue(() =>
                {
                    var current = SelectedNamespace;
                    Namespaces.Clear();
                    Namespaces.Add("All namespaces");
                    foreach (var n in nsList)
                    {
                        Namespaces.Add(n);
                    }

                    // Preserve selection (defaults to "All namespaces").
                    SelectedNamespace = Namespaces.Contains(current) ? current : "All namespaces";
                });
            }
            catch
            {
                // ignore
            }

            while (!token.IsCancellationRequested)
            {
                await PollOnceAsync(token).ConfigureAwait(false);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        try
        {
            var ns = SelectedNamespace;

            // Run all resource queries concurrently to cut wall-clock time.
            var nodesT = _k8s.GetNodesAsync(token);
            var depsT = _k8s.GetDeploymentsAsync(ns, token);
            var podsT = _k8s.GetPodsAsync(ns, token);
            var svcsT = _k8s.GetServicesAsync(ns, token);
            var ingsT = _k8s.GetIngressesAsync(ns, token);
            var pvcsT = _k8s.GetPvcsAsync(ns, token);
            var cmsT = _k8s.GetConfigMapsAsync(ns, token);
            var secsT = _k8s.GetSecretsAsync(ns, token);
            var jobsT = _k8s.GetJobsAsync(ns, token);
            var cronsT = _k8s.GetCronJobsAsync(ns, token);

            await Task.WhenAll(nodesT, depsT, podsT, svcsT, ingsT, pvcsT, cmsT, secsT, jobsT, cronsT)
                .ConfigureAwait(false);

            var nodes = nodesT.Result;
            var deps = depsT.Result;
            var pods = podsT.Result;
            var svcs = svcsT.Result;
            var ings = ingsT.Result;
            var pvcs = pvcsT.Result;
            var cms = cmsT.Result;
            var secs = secsT.Result;
            var jobs = jobsT.Result;
            var crons = cronsT.Result;

            _dispatcher.TryEnqueue(() =>
            {
                Sync(Nodes, nodes);
                Sync(Deployments, deps);
                Sync(Pods, pods);
                Sync(Services, svcs);
                Sync(Ingresses, ings);
                Sync(Pvcs, pvcs);
                Sync(ConfigMaps, cms);
                Sync(Secrets, secs);
                Sync(Jobs, jobs);
                Sync(CronJobs, crons);

                NodeCount = nodes.Count;
                NodeActiveCount = nodes.Count(n => n.IsReady);
                DeploymentCount = deps.Count;
                DeploymentActiveCount = deps.Count(d => d.IsHealthy);
                PodCount = pods.Count;
                ServiceCount = svcs.Count;
                IngressCount = ings.Count;
                PvcCount = pvcs.Count;
                ConfigMapCount = cms.Count;
                SecretCount = secs.Count;
                JobCount = jobs.Count;
                CronJobCount = crons.Count;
            });
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch
        {
            // ignore transient errors
        }
    }

    public void StopPolling()
    {
        try
        {
            _pollCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _pollCts?.Dispose();
        _pollCts = null;
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
