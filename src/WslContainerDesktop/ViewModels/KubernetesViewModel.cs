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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>View model for the Kubernetes page, controlling k3s lifecycle, resource lists, YAML apply, and port-forwarding.</summary>
/// <remarks>It uses <see cref="IKubernetesService"/> for all cluster I/O and posts poll results back to the WinUI dispatcher because collections are bound on the UI thread.</remarks>
public partial class KubernetesViewModel : ObservableObject
{
    private readonly IKubernetesService _k8s;
    private readonly DialogService _dialogs;
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly IWindowVisibility _visibility;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _pollCts;
    private K8sStartupWatcher? _startupWatcher;
    private long _pageVisit;
    private long _refreshRevision;

    // Whether the Kubernetes page is open; only the UI thread reads or writes it.
    private bool _pageOpen;

    /// <summary>Generated cluster state that drives the install/start/stop dashboard states.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(IsNoDistribution))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(IsStarting))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private ClusterState _state = ClusterState.Unknown;

    /// <summary>Status text describing the cluster lifecycle state or current operation.</summary>
    [ObservableProperty]
    private string _statusMessage = UiText.Get("Workload_Text_Checking_cluster_status_8c6339", "Checking cluster status…");

    /// <summary>Name of the single k3s node when the cluster is running.</summary>
    [ObservableProperty]
    private string _nodeName = "-";

    /// <summary>Installed Kubernetes/k3s version text.</summary>
    [ObservableProperty]
    private string _kubernetesVersion = "-";

    /// <summary>WSL distribution that hosts the k3s cluster.</summary>
    [ObservableProperty]
    private string _distro = "-";

    /// <summary>Generated flag used while install, upgrade, start, stop, or uninstall is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalled))]
    [NotifyPropertyChangedFor(nameof(IsNoDistribution))]
    [NotifyPropertyChangedFor(nameof(IsInstalled))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    [NotifyPropertyChangedFor(nameof(IsStarting))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _working;

    /// <summary>Installer or uninstaller log text streamed into the operation panel.</summary>
    [ObservableProperty]
    private string _operationLog = string.Empty;

    /// <summary>Generated flag that shows the install/upgrade/uninstall log panel.</summary>
    [ObservableProperty]
    private bool _showOperationLog;

    // ---- Sub-navigation + namespace filter ----
    /// <summary>Selected sub-navigation section inside the Kubernetes page.</summary>
    [ObservableProperty]
    private string _selectedSection = "Dashboard";

    /// <summary>Namespace filter applied to namespaced resource lists.</summary>
    [ObservableProperty]
    private string _selectedNamespace = "All namespaces";

    /// <summary>Namespace options for the filter, including the synthetic all-namespaces option.</summary>
    public ObservableCollection<string> Namespaces { get; } = new();

    /// <summary>Stable namespace values with independently localized labels.</summary>
    public ObservableCollection<K8sNamespaceOption> NamespaceOptions { get; } = new();

    // ---- Resource collections ----
    /// <summary>Node rows returned by the resource poller.</summary>
    public ObservableCollection<K8sNode> Nodes { get; } = new();
    /// <summary>Pod rows returned by the resource poller.</summary>
    public ObservableCollection<K8sPod> Pods { get; } = new();
    /// <summary>Deployment rows returned by the resource poller.</summary>
    public ObservableCollection<K8sDeployment> Deployments { get; } = new();
    /// <summary>Service rows returned by the resource poller.</summary>
    public ObservableCollection<K8sService> Services { get; } = new();
    /// <summary>Ingress rows returned by the resource poller.</summary>
    public ObservableCollection<K8sIngress> Ingresses { get; } = new();
    /// <summary>Persistent-volume-claim rows returned by the resource poller.</summary>
    public ObservableCollection<K8sPvc> Pvcs { get; } = new();
    /// <summary>ConfigMap rows returned by the resource poller.</summary>
    public ObservableCollection<K8sConfigMap> ConfigMaps { get; } = new();
    /// <summary>Secret rows returned by the resource poller.</summary>
    public ObservableCollection<K8sSecret> Secrets { get; } = new();
    /// <summary>Job rows returned by the resource poller.</summary>
    public ObservableCollection<K8sJob> Jobs { get; } = new();
    /// <summary>CronJob rows returned by the resource poller.</summary>
    public ObservableCollection<K8sCronJob> CronJobs { get; } = new();

    /// <summary>Active port-forward sessions managed by the app.</summary>
    public ObservableCollection<PortForward> PortForwards { get; } = new();

    // ---- Dashboard metric counts ----
    /// <summary>Dashboard count of nodes.</summary>
    [ObservableProperty]
    private int _nodeCount;

    /// <summary>Dashboard count of ready nodes.</summary>
    [ObservableProperty]
    private int _nodeActiveCount;

    /// <summary>Dashboard count of deployments.</summary>
    [ObservableProperty]
    private int _deploymentCount;

    /// <summary>Dashboard count of healthy deployments.</summary>
    [ObservableProperty]
    private int _deploymentActiveCount;

    /// <summary>Dashboard count of pods.</summary>
    [ObservableProperty]
    private int _podCount;

    /// <summary>Dashboard count of services.</summary>
    [ObservableProperty]
    private int _serviceCount;

    /// <summary>Dashboard count of ingresses.</summary>
    [ObservableProperty]
    private int _ingressCount;

    /// <summary>Dashboard count of persistent-volume claims.</summary>
    [ObservableProperty]
    private int _pvcCount;

    /// <summary>Dashboard count of ConfigMaps.</summary>
    [ObservableProperty]
    private int _configMapCount;

    /// <summary>Dashboard count of Secrets.</summary>
    [ObservableProperty]
    private int _secretCount;

    /// <summary>Dashboard count of Jobs.</summary>
    [ObservableProperty]
    private int _jobCount;

    /// <summary>Dashboard count of CronJobs.</summary>
    [ObservableProperty]
    private int _cronJobCount;

    /// <summary>True when the install call-to-action should be shown.</summary>
    public bool IsNotInstalled => !Working && State == ClusterState.NotInstalled;
    /// <summary>True when no WSL distribution exists to host k3s, so install is unavailable.</summary>
    public bool IsNoDistribution => !Working && State == ClusterState.NoDistribution;

    /// <summary>Explains why no distribution can host k3s, and what to do.</summary>
    [ObservableProperty]
    private string _noDistributionMessage = string.Empty;

    /// <summary>Headline shown when no distribution can host k3s.</summary>
    [ObservableProperty]
    private string _noDistributionTitle = string.Empty;

    /// <summary>
    /// True when a pinned distribution can't host k3s, so the page offers to go back to WSL's
    /// default distribution.
    /// </summary>
    [ObservableProperty]
    private bool _canUseDefaultDistribution;
    /// <summary>True when installed-cluster actions should be shown.</summary>
    public bool IsInstalled => !Working && State is ClusterState.Stopped or ClusterState.Running or ClusterState.Starting;
    /// <summary>True when resource lists and running-cluster actions should be shown.</summary>
    public bool IsRunning => !Working && State == ClusterState.Running;
    /// <summary>True when the cluster can be started.</summary>
    public bool IsStopped => !Working && State == ClusterState.Stopped;
    /// <summary>True while systemd is still bringing the k3s service up.</summary>
    public bool IsStarting => !Working && State == ClusterState.Starting;
    /// <summary>True while a lifecycle operation is blocking other actions.</summary>
    public bool IsBusy => Working;

    /// <summary>Raised when the operation log changes so the view can scroll to the newest line.</summary>
    public event Action? OperationLogUpdated;

    /// <summary>Creates the Kubernetes page model and seeds the namespace filter.</summary>
    public KubernetesViewModel(IKubernetesService k8s, DialogService dialogs, StatusMonitor monitor, ISettingsService settings, IWindowVisibility visibility)
    {
        _k8s = k8s;
        _dialogs = dialogs;
        _monitor = monitor;
        _settings = settings;
        _visibility = visibility;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Seed the default namespace option so the ComboBox shows a selection immediately.
        Namespaces.Add("All namespaces");
        NamespaceOptions.Add(new("All namespaces"));
    }

    /// <summary>Refreshes display text without changing cluster, namespace or log values.</summary>
    public void RefreshLanguage()
    {
        StatusMessage = UiText.Translate(StatusMessage);
        foreach (var option in NamespaceOptions) option.RefreshLanguage();
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(NoDistributionTitle));
        OnPropertyChanged(nameof(NoDistributionMessage));
    }

    /// <summary>
    /// Runs the k3s installer (fresh install or in-place upgrade) with installer-script
    /// integrity verification. The first ever run trusts and records the script's SHA-256
    /// (trust-on-first-use); later runs verify against that pin and only prompt if the remote
    /// script has changed, so ordinary installs and upgrades add no extra steps.
    /// </summary>
    private async Task<K3sInstallResult> RunInstallerTrustedAsync(bool isUpgrade, string? version)
    {
        var previousPin = _settings.K3sInstallerSha256;

        async Task<K3sInstallResult> InvokeAsync(string? expected) => isUpgrade
            ? await _k8s.UpgradeAsync(version, expected, AppendLog)
            : await _k8s.InstallAsync(expected, AppendLog);

        var result = await InvokeAsync(previousPin);

        if (result.HashMismatch)
        {
            // The remote installer changed since it was last approved. Surface both hashes and
            // let the user decide; a legitimate upstream update is expected to land here.
            var approve = await _dialogs.ShowConfirmAsync(
                UiText.Get("Workload_Text_k3s_installer_script_changed_31ef79", "k3s installer script changed"),
                UiText.Get("Workload_Text_The_installer_downloaded_from_https_get_5dbca2", "The installer downloaded from https://get.k3s.io no longer matches the script you previously approved on this machine.\n\nPreviously approved SHA-256:\n{0}\n\nNewly downloaded SHA-256:\n{1}\n\nThis is normal when the k3s project updates its installer, but only continue if you trust the source. Approve the new script and continue?", previousPin, result.InstallerHash),
                UiText.Get("Workload_Text_Approve_and_continue_e94a79", "Approve and continue"));
            if (!approve)
            {
                AppendLog(string.Empty);
                AppendLog(UiText.Get("Workload_Text_Cancelled_the_changed_installer_script_was_a25d67", "Cancelled: the changed installer script was not approved."));
                return result;
            }

            // Re-run trusting the freshly-downloaded script.
            result = await InvokeAsync(result.InstallerHash);
        }

        // Persist the pin on success (first-use trust, or refreshed after approval).
        if (result.Success && !string.IsNullOrWhiteSpace(result.InstallerHash) &&
            !string.Equals(previousPin, result.InstallerHash, StringComparison.OrdinalIgnoreCase))
        {
            _settings.K3sInstallerSha256 = result.InstallerHash;
            _settings.Save();
        }

        return result;
    }

    partial void OnSelectedNamespaceChanged(string value)
    {
        // Re-poll immediately so namespaced views reflect the new filter.
        if (State == ClusterState.Running)
        {
            _ = PollOnceAsync(CancellationToken.None);
        }
    }

    /// <summary>Initializes cluster state from the shared monitor and starts polling when k3s is running.</summary>
    public async Task InitializeAsync()
    {
        _pageOpen = true;
        ++_pageVisit;

        // Seed from the shared monitor's cached snapshot so the correct view (install hero,
        // stopped card, or the running sub-nav) appears instantly instead of after the full
        // status probe. The authoritative GetStatusAsync below then fills in node/version.
        var cached = _monitor.LatestK8s;
        // The cached snapshot doesn't say why no distribution can host k3s; wait for the full
        // status rather than flash that screen with no explanation.
        if (cached is not null && State == ClusterState.Unknown && cached.State != ClusterState.NoDistribution)
        {
            State = cached.State;
            StatusMessage = cached.Summary;
        }

        await RefreshStatusAsync();
    }

    /// <summary>Refreshes the k3s lifecycle status without polling all resources.</summary>
    [RelayCommand]
    private Task RefreshStatusAsync() => RefreshStatusCoreAsync(discover: true);

    private async Task RefreshStatusCoreAsync(bool discover)
    {
        StopPolling();
        var visit = _pageVisit;
        var revision = ++_refreshRevision;
        var status = discover ? await _k8s.GetStatusAsync() : await _k8s.ObserveStatusAsync();
        if (!_pageOpen || visit != _pageVisit || revision != _refreshRevision)
        {
            return;
        }

        Apply(status);

        if (State == ClusterState.Starting)
        {
            WatchStartup();
        }
        else if (State == ClusterState.Running)
        {
            StartPolling();
        }
    }

    /// <summary>
    /// Re-checks the status while k3s is starting, then shows the running cluster. It checks only while
    /// the page can be seen, backing off to ten seconds, until the page is left or its WSL session ends.
    /// </summary>
    private void WatchStartup()
    {
        if (!_pageOpen)
        {
            return;
        }

        _startupWatcher ??= new K8sStartupWatcher(_visibility, _k8s.ObserveStatusAsync, Apply, StartPolling);
        UiSafe.Run(_startupWatcher.StartAsync);
    }

    /// <summary>Stops using a pinned distribution that can't host k3s and goes back to WSL's default.</summary>
    [RelayCommand]
    private async Task UseDefaultDistributionAsync()
    {
        StopPolling();
        await _k8s.UseDefaultDistributionAsync();
        await RefreshStatusAsync();
    }

    private void Apply(ClusterStatus status)
    {
        State = status.State;
        Distro = status.Distro;
        NodeName = status.NodeName;
        KubernetesVersion = status.KubernetesVersion;
        var pinned = !string.IsNullOrWhiteSpace(_settings.WslDistro);
        var hostDistro = status.Distro is "-" or "" ? null : status.Distro;
        NoDistributionTitle = KubernetesHostCheck.Title(status.HostProblem);
        NoDistributionMessage = KubernetesHostCheck.Explain(status.HostProblem, hostDistro, pinned);
        CanUseDefaultDistribution = pinned && status.State == ClusterState.NoDistribution;
        StatusMessage = status.State switch
        {
            ClusterState.NotInstalled => UiText.Get("Workload_Text_Kubernetes_k3s_is_not_installed_7dc35f", "Kubernetes (k3s) is not installed."),
            ClusterState.NoDistribution => KubernetesHostCheck.Summary(status.HostProblem, hostDistro),
            ClusterState.Stopped => string.IsNullOrEmpty(status.Message) ? UiText.Get("Workload_Text_Cluster_is_installed_but_stopped_4527a7", "Cluster is installed but stopped.") : status.Message,
            ClusterState.Starting => UiText.Get("Workload_Text_k3s_is_starting_WSL_just_started_497de6", "k3s is starting… WSL just started its distribution."),
            ClusterState.Running => UiText.Get("Workload_Text_Cluster_running_node_0_1_4c9575", "Cluster running · node {0} · {1}", status.NodeName, status.KubernetesVersion),
            ClusterState.Unknown => string.IsNullOrEmpty(status.Message) ? UiText.Get("Workload_Text_Unable_to_determine_status_c9aef1", "Unable to determine status.") : status.Message,
            _ => status.Message,
        };
    }

    /// <summary>Installs k3s into WSL after confirmation and installer-script verification.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        // The distro can be removed while the page is open; re-check before offering to install.
        await RefreshStatusAsync();
        if (State == ClusterState.NoDistribution)
        {
            return;
        }

        var host = Distro is "-" or "default" or "" ? null : Distro;
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Install_Kubernetes_0f8576", "Install Kubernetes"),
            UiText.Get("Workload_Text_This_installs_k3s_a_lightweight_single_06d10a", "This installs k3s (a lightweight single-node Kubernetes) into ") +
            (host is null ? UiText.Get("Workload_Text_your_WSL_distribution_f67676", "your WSL distribution") : UiText.Get("Workload_Text_the_WSL_distribution_0_55fe8a", "the WSL distribution \"{0}\"", host)) + ". " +
            UiText.Get("Workload_Text_It_runs_as_a_systemd_service_07d477", "It runs as a systemd service and can be uninstalled later. Continue?"),
            UiText.Get("Workload_Text_Install_569ca4", "Install"));
        if (!ok)
        {
            return;
        }

        Working = true;
        ShowOperationLog = true;
        OperationLog = string.Empty;
        StatusMessage = UiText.Get("Workload_Text_Installing_k3s_this_can_take_a_63291c", "Installing k3s… this can take a few minutes.");

        try
        {
            var result = await RunInstallerTrustedAsync(isUpgrade: false, version: null);
            if (result.HashMismatch)
            {
                // User declined the changed installer; message already logged. Leave the log up.
            }
            else if (result.SystemdMissing)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Kubernetes_needs_systemd_dbce45", "Kubernetes needs systemd"), K8sInstaller.SystemdMissingMessage);
            }
            else if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Install_failed_33b471", "Install failed"), result.Result.ErrorText);
            }
            else
            {
                // Hide the op-log on success so it doesn't overlap the running view.
                ShowOperationLog = false;
            }
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
        }
    }

    /// <summary>Uninstalls k3s and clears cluster data after confirmation.</summary>
    [RelayCommand]
    private async Task UninstallAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Uninstall_Kubernetes_70f934", "Uninstall Kubernetes"),
            UiText.Get("Workload_Text_This_stops_and_completely_removes_k3s_0dc606", "This stops and completely removes k3s and all cluster data from your WSL distro. Running workloads will be destroyed. This cannot be undone. Continue?"),
            UiText.Get("Workload_Text_Uninstall_fe1995", "Uninstall"));
        if (!ok)
        {
            return;
        }

        StopPolling();
        ClearPortForwards();
        Working = true;
        ShowOperationLog = true;
        OperationLog = string.Empty;
        StatusMessage = UiText.Get("Workload_Text_Uninstalling_k3s_and_cleaning_up_187610", "Uninstalling k3s and cleaning up…");

        try
        {
            var result = await _k8s.UninstallAsync(AppendLog);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Uninstall_failed_ecbcb5", "Uninstall failed"), result.ErrorText);
            }
            else
            {
                // Hide the op-log on success so it doesn't overlap the not-installed view.
                ShowOperationLog = false;

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

    /// <summary>Upgrades k3s while respecting installer trust and Kubernetes version-skew rules.</summary>
    [RelayCommand]
    private async Task UpgradeAsync()
    {
        // Detect current + latest versions to inform the dialog.
        var currentTask = _k8s.GetInstalledVersionAsync();
        var latestTask = _k8s.GetLatestStableVersionAsync();
        await Task.WhenAll(currentTask, latestTask);

        var currentStr = await currentTask ?? (KubernetesVersion == "-" ? UiText.Get("Workload_Text_unknown_b23a6a", "unknown") : KubernetesVersion);
        var latest = await latestTask;

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
                    UiText.Get("Workload_Text_Downgrade_not_supported_0748a2", "Downgrade not supported"),
                    UiText.Get("Workload_Text_The_selected_version_0_is_older_a76984", "The selected version {0} is older than the installed version {1}. k3s does not support downgrades; pick the same or a newer version.", targetStr, cur.Original));
                return;
            }

            if (tgt.Major == cur.Major && tgt.Minor > cur.Minor + 1)
            {
                var nextChannel = $"v{cur.Major}.{cur.Minor + 1}";
                var stepVersion = await _k8s.GetChannelVersionAsync(nextChannel);
                if (stepVersion is null)
                {
                    await _dialogs.ShowMessageAsync(
                        UiText.Get("Workload_Text_Cannot_determine_next_version_4cd9a9", "Cannot determine next version"),
                        UiText.Get("Workload_Text_Upgrading_from_0_to_1_would_b0814e", "Upgrading from {0} to {1} would skip intermediate minor versions, which is not supported. Could not resolve the {2} channel to step through. Check your network and try again.", cur.Original, targetStr, nextChannel));
                    return;
                }

                var proceed = await _dialogs.ShowConfirmAsync(
                    UiText.Get("Workload_Text_Upgrade_one_minor_version_at_a_3f0567", "Upgrade one minor version at a time"),
                    UiText.Get("Workload_Text_You_re_on_0_Upgrading_straight_9b7945", "You're on {0}. Upgrading straight to {1} would skip intermediate minor versions (v{2}.{3} … v{4}.{5}), which the Kubernetes version-skew policy does not allow.\n\nUpgrade to {6} first instead? You can repeat the upgrade afterwards to continue toward {7}.", cur.Original, targetStr, cur.Major, cur.Minor + 1, tgt.Major, tgt.Minor - 1, stepVersion, targetStr),
                    UiText.Get("Workload_Text_Upgrade_to_next_minor_8477ce", "Upgrade to next minor"));
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
            ? UiText.Get("Workload_Text_Upgrading_k3s_to_the_latest_stable_473d09", "Upgrading k3s to the latest stable release…")
            : UiText.Get("Workload_Text_Installing_k3s_0_9cb7e1", "Installing k3s {0}…", installVersion);

        try
        {
            var upgrade = await RunInstallerTrustedAsync(isUpgrade: true, version: installVersion);
            if (upgrade.HashMismatch)
            {
                // User declined the changed installer; message already logged. Leave the log up.
            }
            else if (!upgrade.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Upgrade_failed_9b8d54", "Upgrade failed"), upgrade.Result.ErrorText);
            }
            else
            {
                AppendLog(string.Empty);
                AppendLog(UiText.Get("Workload_Text_Upgrade_complete_b68ef9", "Upgrade complete."));
                ShowOperationLog = false;
            }
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
        }
    }

    /// <summary>Starts the installed k3s service and then begins resource polling.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        Working = true;
        StatusMessage = UiText.Get("Workload_Text_Starting_cluster_1c3f3a", "Starting cluster…");
        try
        {
            var result = await _k8s.StartAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Failed_to_start_99ef15", "Failed to start"), result.ErrorText);
            }

            // Give k3s a moment to bring the node up.
            await Task.Delay(3000);
        }
        finally
        {
            Working = false;
            await RefreshStatusAsync();
        }
    }

    /// <summary>Stops k3s, clears resource lists, and stops port-forwards.</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        StopPolling();
        Working = true;
        StatusMessage = UiText.Get("Workload_Text_Stopping_cluster_ba0634", "Stopping cluster…");
        try
        {
            var result = await _k8s.StopAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Failed_to_stop_6408b3", "Failed to stop"), result.ErrorText);
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
            await RefreshStatusCoreAsync(discover: false);
        }
    }

    // ---- Apply YAML ----------------------------------------------------

    /// <summary>Prompts for YAML and applies it to the cluster through the Kubernetes service.</summary>
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
                ? UiText.Get("Workload_Text_Manifest_applied_c4a4ae", "Manifest applied.")
                : applied.StandardOutput.Trim();
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Manifest_applied_716a5c", "Manifest applied"), summary);

            // Refresh immediately so the new objects show up without waiting for the next poll.
            if (State == ClusterState.Running)
            {
                await PollOnceAsync(CancellationToken.None);
            }
        }
        else
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Apply_failed_cae4cf", "Apply failed"), applied.ErrorText);
        }
    }

    // ---- Resource row actions ------------------------------------------

    /// <summary>Deletes a resource row after confirmation and refreshes the lists.</summary>
    public async Task DeleteResourceAsync(K8sResourceRef reference)
    {
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Delete_0_7eab12", "Delete {0}", UiText.Translate(reference.DisplayKind)),
            UiText.Get("Workload_Text_Delete_0_1_This_cannot_be_0b19ba", "Delete {0} \"{1}\"? This cannot be undone.", UiText.Translate(reference.DisplayKind), reference.Name),
            UiText.Get("Workload_Text_Delete_e2d0a5", "Delete"));
        if (!ok)
        {
            return;
        }

        var result = await _k8s.DeleteResourceAsync(reference.Kind, reference.Namespace, reference.Name);
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Delete_failed_8727e2", "Delete failed"), result.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    /// <summary>Prompts for replicas and scales the selected deployment row.</summary>
    public async Task ScaleDeploymentAsync(K8sResourceRef reference)
    {
        var dialog = new SimpleInputDialog(UiText.Get("Workload_Text_Scale_0_c8b401", "Scale {0}", reference.Name), UiText.Get("Workload_Text_Desired_replicas_598a02", "Desired replicas"), UiText.Get("Workload_Text_e_g_3_8c2fb7", "e.g. 3"));
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            !int.TryParse(dialog.Value.Trim(), out var replicas) || replicas < 0)
        {
            return;
        }

        var scaled = await _k8s.ScaleDeploymentAsync(reference.Namespace, reference.Name, replicas);
        if (!scaled.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Scale_failed_d6a24c", "Scale failed"), scaled.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    /// <summary>Triggers a rollout restart for the selected deployment row.</summary>
    public async Task RestartDeploymentAsync(K8sResourceRef reference)
    {
        var restarted = await _k8s.RestartDeploymentAsync(reference.Namespace, reference.Name);
        if (!restarted.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Restart_failed_b8864c", "Restart failed"), restarted.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    /// <summary>Suspends or resumes the selected cron job.</summary>
    public async Task SetCronSuspendAsync(K8sResourceRef reference, bool suspend)
    {
        var result = await _k8s.SetCronJobSuspendAsync(reference.Namespace, reference.Name, suspend);
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Failed_031a8f", "Failed"), result.ErrorText);
        }

        await PollOnceAsync(CancellationToken.None);
    }

    /// <summary>Creates a one-off job from the selected cron job.</summary>
    public async Task TriggerCronAsync(K8sResourceRef reference)
    {
        var result = await _k8s.TriggerCronJobAsync(reference.Namespace, reference.Name);
        if (result.Success)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Workload_Text_Job_started_514ca6", "Job started"),
                string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? UiText.Get("Workload_Text_A_one_off_job_was_created_8a1a9c", "A one-off job was created from this cronjob.")
                    : result.StandardOutput.Trim());
            await PollOnceAsync(CancellationToken.None);
        }
        else
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Trigger_failed_78bed2", "Trigger failed"), result.ErrorText);
        }
    }

    // ---- Port forwarding -----------------------------------------------

    /// <summary>Prompts for a pod or service target and starts a local port-forward.</summary>
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
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Port_in_use_3bb735", "Port in use"),
                UiText.Get("Workload_Text_Local_port_0_is_already_being_ee8135", "Local port {0} is already being forwarded.", forward.LocalPort));
            return;
        }

        if (_k8s.StartPortForward(forward))
        {
            PortForwards.Add(forward);
        }
        else
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Port_forward_failed_8e0e0e", "Port forward failed"),
                UiText.Get("Workload_Text_Could_not_start_the_port_forward_84c580", "Could not start the port-forward. Check that the target and ports are valid."));
        }
    }

    /// <summary>Stops one active port-forward.</summary>
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

    /// <summary>Opens the local URL for an active port-forward.</summary>
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

    /// <summary>Stops every port-forward and empties the list, e.g. before the app closes for an update.</summary>
    public void StopAllPortForwards() => ClearPortForwards();

    private void AppendLog(string line)
    {
        _dispatcher.TryEnqueue(() =>
        {
            OperationLog += line + "\n";
            OperationLogUpdated?.Invoke();
        });
    }

    // ---- Resource polling ----------------------------------------------

    /// <summary>
    /// Starts the background resource poller that updates bound collections on the UI thread. Does
    /// nothing once the page has been left, because an operation that finishes after that must not
    /// leave a poller running for a page nobody is on.
    /// </summary>
    public void StartPolling()
    {
        StopPolling();
        if (!_pageOpen || !_k8s.CanObserve)
        {
            return;
        }

        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            // Load namespaces once up front.
            try
            {
                await _visibility.WhenViewableAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!_k8s.CanObserve)
                {
                    return;
                }
                var nsList = await _k8s.GetNamespacesAsync(token).ConfigureAwait(false);
                _dispatcher.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested || !_pageOpen || !_k8s.CanObserve)
                    {
                        return;
                    }

                    var current = SelectedNamespace;
                    Namespaces.Clear();
                    Namespaces.Add("All namespaces");
                    foreach (var n in nsList)
                    {
                        Namespaces.Add(n);
                    }

                    // Preserve selection (defaults to "All namespaces").
                    Sync(NamespaceOptions, Namespaces.Select(value => new K8sNamespaceOption(value)).ToList());
                    SelectedNamespace = Namespaces.Contains(current) ? current : "All namespaces";
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // ignore
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // The lists only feed the visible page; don't query the cluster while nobody can see it.
                    await _visibility.WhenViewableAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await PollOnceAsync(token).ConfigureAwait(false);
                if (!_k8s.CanObserve)
                {
                    break;
                }

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
            token.ThrowIfCancellationRequested();
            if (!_k8s.CanObserve || !_visibility.IsViewable)
            {
                return;
            }

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

            var nodes = await nodesT;
            var deps = await depsT;
            var pods = await podsT;
            var svcs = await svcsT;
            var ings = await ingsT;
            var pvcs = await pvcsT;
            var cms = await cmsT;
            var secs = await secsT;
            var jobs = await jobsT;
            var crons = await cronsT;

            _dispatcher.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || !_pageOpen || !_k8s.CanObserve)
                {
                    return;
                }

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

    /// <summary>Cancels and disposes the background resource poller, and stops waiting for k3s to start.</summary>
    public void StopPolling()
    {
        ++_refreshRevision;
        _startupWatcher?.Stop();
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

    /// <summary>
    /// Called when the page is left: stops polling and keeps operations that finish later from
    /// starting it again until the page is opened.
    /// </summary>
    public void Deactivate()
    {
        _pageOpen = false;
        ++_pageVisit;
        StopPolling();
    }

    /// <summary>
    /// Reconciles <paramref name="target"/> to match <paramref name="source"/> in place,
    /// touching only rows that actually changed. This avoids the full Clear()+re-add churn
    /// that made the Kubernetes lists visibly flicker on every poll. Requires value equality
    /// on T (the K8s row models are records), so identical rows compare equal and are skipped.
    /// </summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        // Remove rows that are no longer present.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        // Insert / move / no-op so target matches source order and content.
        for (var i = 0; i < source.Count; i++)
        {
            var desired = source[i];

            if (i >= target.Count)
            {
                target.Add(desired);
                continue;
            }

            if (Equals(target[i], desired))
            {
                continue; // unchanged — leave the row untouched (no flicker).
            }

            // If the desired item already exists further down, move it up; otherwise insert.
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (Equals(target[j], desired))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, desired);
            }
        }

        // Trim any trailing leftovers.
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

/// <summary>Keeps kubectl namespace values separate from the synthetic option's display text.</summary>
public sealed class K8sNamespaceOption(string value) : ObservableObject
{
    public string Value { get; } = value;
    public string DisplayName => Value == "All namespaces"
        ? UiText.Get("Workload_Text_All_namespaces_50c3cb", "All namespaces")
        : Value;
    public void RefreshLanguage() => OnPropertyChanged(nameof(DisplayName));
    public override bool Equals(object? obj) => obj is K8sNamespaceOption other && Value == other.Value;
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}
