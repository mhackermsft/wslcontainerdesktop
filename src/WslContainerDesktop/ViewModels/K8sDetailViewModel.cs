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
public partial class K8sDetailViewModel : ObservableObject
{
    private readonly IKubernetesService _k8s;
    private readonly DialogService _dialogs;

    public K8sResourceRef? Resource { get; private set; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _displayKind = string.Empty;

    [ObservableProperty]
    private string _yaml = string.Empty;

    [ObservableProperty]
    private string _describeText = string.Empty;

    [ObservableProperty]
    private string _logsText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _applyingKube;

    // Capability gates for the view.
    [ObservableProperty]
    private bool _supportsLogs;

    [ObservableProperty]
    private bool _supportsScale;

    [ObservableProperty]
    private bool _supportsCron;

    [ObservableProperty]
    private bool _cronSuspended;

    /// <summary>Raised after a delete so the host page can navigate back.</summary>
    public event Action? Deleted;

    public K8sDetailViewModel(IKubernetesService k8s, DialogService dialogs)
    {
        _k8s = k8s;
        _dialogs = dialogs;
    }

    public async Task LoadAsync(K8sResourceRef reference)
    {
        Resource = reference;
        Title = reference.Name;
        DisplayKind = reference.DisplayKind;
        Subtitle = reference.ClusterScoped
            ? reference.DisplayKind
            : $"{reference.DisplayKind}  ·  namespace {reference.Namespace}";
        SupportsLogs = reference.SupportsLogs;
        SupportsScale = reference.SupportsScale;
        SupportsCron = reference.SupportsCron;

        await RefreshAllAsync();
    }

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
                    LogsText = "(no logs)";
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
            LogsText = "(no logs)";
        }
    }

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
                    "Changes applied",
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? "Configuration applied to the cluster."
                        : result.StandardOutput.Trim());
                await RefreshAllAsync();
            }
            else
            {
                await _dialogs.ShowMessageAsync("Apply failed", result.ErrorText);
            }
        }
        finally
        {
            ApplyingKube = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Resource is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            $"Delete {Resource.DisplayKind}",
            $"Delete {Resource.DisplayKind.ToLowerInvariant()} \"{Resource.Name}\"? This cannot be undone.",
            "Delete");
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
            await _dialogs.ShowMessageAsync("Delete failed", result.ErrorText);
        }
    }

    [RelayCommand]
    private async Task ScaleAsync()
    {
        if (Resource is null || !SupportsScale)
        {
            return;
        }

        var dialog = new Dialogs.SimpleInputDialog(
            $"Scale {Resource.Name}", "Desired replicas", "e.g. 3");
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            !int.TryParse(dialog.Value.Trim(), out var replicas) || replicas < 0)
        {
            return;
        }

        var scaled = await _k8s.ScaleDeploymentAsync(Resource.Namespace, Resource.Name, replicas);
        if (!scaled.Success)
        {
            await _dialogs.ShowMessageAsync("Scale failed", scaled.ErrorText);
        }

        await RefreshAllAsync();
    }

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
            await _dialogs.ShowMessageAsync("Restart failed", restarted.ErrorText);
        }

        await RefreshAllAsync();
    }

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
            await _dialogs.ShowMessageAsync("Failed", result.ErrorText);
        }

        await RefreshAllAsync();
    }

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
                "Job started",
                string.IsNullOrWhiteSpace(result.StandardOutput)
                    ? "A one-off job was created from this cronjob."
                    : result.StandardOutput.Trim());
        }
        else
        {
            await _dialogs.ShowMessageAsync("Trigger failed", result.ErrorText);
        }
    }
}
