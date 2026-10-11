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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the single-object drill-down page. Loads Summary/Kube/Describe/Logs for one
/// Kubernetes object and exposes actions (edit-and-apply, delete, scale, restart, etc).
/// </summary>
/// <summary>Detail-page view model for one Kubernetes resource, showing YAML, describe output, logs, and resource-specific actions.</summary>
public partial class K8sDetailViewModel : ObservableObject
{
    private readonly IKubernetesService _k8s;
    private readonly DialogService _dialogs;

    /// <summary>The Kubernetes resource currently loaded into the detail page.</summary>
    public K8sResourceRef? Resource { get; private set; }

    /// <summary>Resource name used as the page title.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Kind and namespace text shown under the title.</summary>
    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>Friendly resource kind such as pod, deployment, or cron job.</summary>
    [ObservableProperty]
    private string _displayKind = string.Empty;

    /// <summary>Editable YAML fetched from the cluster.</summary>
    [ObservableProperty]
    private string _yaml = string.Empty;

    /// <summary>Output from <c>kubectl describe</c> for troubleshooting.</summary>
    [ObservableProperty]
    private string _describeText = string.Empty;

    /// <summary>Latest pod logs, or a placeholder when the resource has no logs.</summary>
    [ObservableProperty]
    private string _logsText = string.Empty;

    /// <summary>Generated busy flag used while refreshing resource details.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Generated flag used while applying edited YAML back to the cluster.</summary>
    [ObservableProperty]
    private bool _applyingKube;

    // Capability gates for the view.
    /// <summary>True when the loaded resource can show pod logs.</summary>
    [ObservableProperty]
    private bool _supportsLogs;

    /// <summary>True when scale and rollout-restart commands are valid for this resource.</summary>
    [ObservableProperty]
    private bool _supportsScale;

    /// <summary>True when cron-job suspend and trigger commands are valid for this resource.</summary>
    [ObservableProperty]
    private bool _supportsCron;

    /// <summary>True when the loaded cron job is currently suspended.</summary>
    [ObservableProperty]
    private bool _cronSuspended;

    /// <summary>Raised after a delete so the host page can navigate back.</summary>
    public event Action? Deleted;

    /// <summary>Creates the detail model with the Kubernetes facade and dialog service.</summary>
    public K8sDetailViewModel(IKubernetesService k8s, DialogService dialogs)
    {
        _k8s = k8s;
        _dialogs = dialogs;
    }

    /// <summary>Loads a resource reference and refreshes all detail panes.</summary>
    public async Task LoadAsync(K8sResourceRef reference)
    {
        Resource = reference;
        Title = reference.Name;
        DisplayKind = UiText.Translate(reference.DisplayKind);
        Subtitle = reference.ClusterScoped
            ? UiText.Translate(reference.DisplayKind)
            : UiText.Get("Workload_Text_0_namespace_1_f105f4", "{0}  ·  namespace {1}", UiText.Translate(reference.DisplayKind), reference.Namespace);
        SupportsLogs = reference.SupportsLogs;
        SupportsScale = reference.SupportsScale;
        SupportsCron = reference.SupportsCron;

        await RefreshAllAsync();
    }

    /// <summary>Updates labels while preserving edited YAML and captured engine output.</summary>
    public void RefreshLanguage()
    {
        if (Resource is not { } resource) return;
        DisplayKind = UiText.Translate(resource.DisplayKind);
        Subtitle = resource.ClusterScoped ? DisplayKind :
            UiText.Get("Workload_Text_0_namespace_1_f105f4", "{0}  ·  namespace {1}", DisplayKind, resource.Namespace);
    }

    /// <summary>Refreshes YAML, describe output, and logs for the loaded resource.</summary>
    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (Resource is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var yamlT = _k8s.GetResourceYamlAsync(Resource.Kind, Resource.Namespace, Resource.Name);
            var descT = _k8s.DescribeResourceAsync(Resource.Kind, Resource.Namespace, Resource.Name);
            var logsT = SupportsLogs
                ? _k8s.GetPodLogsAsync(Resource.Namespace, Resource.Name, 500)
                : Task.FromResult(new CommandResult());

            await Task.WhenAll(yamlT, descT, logsT);

            var yaml = await yamlT;
            var desc = await descT;
            var logs = await logsT;

            Yaml = yaml.Success ? yaml.StandardOutput.TrimEnd() : yaml.ErrorText;
            DescribeText = desc.Success ? desc.StandardOutput.TrimEnd() : desc.ErrorText;

            if (SupportsLogs)
            {
                LogsText = logs.Success
                    ? logs.StandardOutput.TrimEnd()
                    : logs.ErrorText;
                if (string.IsNullOrWhiteSpace(LogsText))
                {
                    LogsText = UiText.Get("Workload_Text_no_logs_b52742", "(no logs)");
                }
            }

            if (SupportsCron)
            {
                CronSuspended = Yaml.Contains("suspend: true", StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refreshes only the logs pane for resources that support logs.</summary>
    [RelayCommand]
    private async Task RefreshLogsAsync()
    {
        if (Resource is null || !SupportsLogs)
        {
            return;
        }

        var logs = await _k8s.GetPodLogsAsync(Resource.Namespace, Resource.Name, 500);
        LogsText = logs.Success ? logs.StandardOutput.TrimEnd() : logs.ErrorText;
        if (string.IsNullOrWhiteSpace(LogsText))
        {
            LogsText = UiText.Get("Workload_Text_no_logs_b52742", "(no logs)");
        }
    }

    /// <summary>Applies the edited YAML to the cluster and refreshes the detail panes.</summary>
    [RelayCommand]
    private async Task ApplyKubeAsync()
    {
        if (Resource is null || string.IsNullOrWhiteSpace(Yaml))
        {
            return;
        }

        ApplyingKube = true;
        try
        {
            var result = await _k8s.ApplyManifestAsync(Yaml);
            if (result.Success)
            {
                await _dialogs.ShowMessageAsync(
                    UiText.Get("Workload_Text_Changes_applied_9cf6db", "Changes applied"),
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? UiText.Get("Workload_Text_Configuration_applied_to_the_cluster_270136", "Configuration applied to the cluster.")
                        : result.StandardOutput.Trim());
                await RefreshAllAsync();
            }
            else
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Apply_failed_cae4cf", "Apply failed"), result.ErrorText);
            }
        }
        finally
        {
            ApplyingKube = false;
        }
    }

    /// <summary>Confirms and deletes the loaded Kubernetes resource.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Resource is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Delete_0_7eab12", "Delete {0}", UiText.Translate(Resource.DisplayKind)),
            UiText.Get("Workload_Text_Delete_0_1_This_cannot_be_0b19ba", "Delete {0} \"{1}\"? This cannot be undone.", UiText.Translate(Resource.DisplayKind), Resource.Name),
            UiText.Get("Workload_Text_Delete_e2d0a5", "Delete"));
        if (!ok)
        {
            return;
        }

        var result = await _k8s.DeleteResourceAsync(Resource.Kind, Resource.Namespace, Resource.Name);
        if (result.Success)
        {
            Deleted?.Invoke();
        }
        else
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Delete_failed_8727e2", "Delete failed"), result.ErrorText);
        }
    }

    /// <summary>Prompts for a replica count and scales the loaded deployment-like resource.</summary>
    [RelayCommand]
    private async Task ScaleAsync()
    {
        if (Resource is null || !SupportsScale)
        {
            return;
        }

        var dialog = new Dialogs.SimpleInputDialog(
            UiText.Get("Workload_Text_Scale_0_c8b401", "Scale {0}", Resource.Name), UiText.Get("Workload_Text_Desired_replicas_598a02", "Desired replicas"), UiText.Get("Workload_Text_e_g_3_8c2fb7", "e.g. 3"));
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            !int.TryParse(dialog.Value.Trim(), out var replicas) || replicas < 0)
        {
            return;
        }

        var scaled = await _k8s.ScaleDeploymentAsync(Resource.Namespace, Resource.Name, replicas);
        if (!scaled.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Scale_failed_d6a24c", "Scale failed"), scaled.ErrorText);
        }

        await RefreshAllAsync();
    }

    /// <summary>Runs a rollout restart for the loaded deployment-like resource.</summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        if (Resource is null || !SupportsScale)
        {
            return;
        }

        var restarted = await _k8s.RestartDeploymentAsync(Resource.Namespace, Resource.Name);
        if (!restarted.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Restart_failed_b8864c", "Restart failed"), restarted.ErrorText);
        }

        await RefreshAllAsync();
    }

    /// <summary>Toggles suspend on the loaded cron job.</summary>
    [RelayCommand]
    private async Task ToggleSuspendAsync()
    {
        if (Resource is null || !SupportsCron)
        {
            return;
        }

        var suspend = !CronSuspended;
        var result = await _k8s.SetCronJobSuspendAsync(Resource.Namespace, Resource.Name, suspend);
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Failed_031a8f", "Failed"), result.ErrorText);
        }

        await RefreshAllAsync();
    }

    /// <summary>Creates a one-off job from the loaded cron job.</summary>
    [RelayCommand]
    private async Task TriggerCronAsync()
    {
        if (Resource is null || !SupportsCron)
        {
            return;
        }

        var result = await _k8s.TriggerCronJobAsync(Resource.Namespace, Resource.Name);
        if (result.Success)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Workload_Text_Job_started_514ca6", "Job started"),
                string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? UiText.Get("Workload_Text_A_one_off_job_was_created_8a1a9c", "A one-off job was created from this cronjob.")
                    : result.StandardOutput.Trim());
        }
        else
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Trigger_failed_78bed2", "Trigger failed"), result.ErrorText);
        }
    }
}
