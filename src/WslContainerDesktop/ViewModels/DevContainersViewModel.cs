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
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Observable row for one imported <c>devcontainer.json</c> workspace.</summary>
public partial class DevContainerRow : ObservableObject
{
    /// <summary>
    /// Returns the dev container name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Creates a row from the persisted Dev Container configuration.</summary>
    public DevContainerRow(DevContainerConfig config)
    {
        Config = config;
    }

    /// <summary>Notifies localized row labels without changing persisted values or output.</summary>
    public void RefreshLanguage()
    {
        StatusText = UiText.Translate(StatusText);
        OnPropertyChanged(nameof(ImageSummary));
        OnPropertyChanged(nameof(PortsSummary));
        OnPropertyChanged(nameof(WarningsSummary));
        OnPropertyChanged(nameof(LifecycleLog));
    }

    /// <summary>Parsed Dev Container configuration backing this row.</summary>
    public DevContainerConfig Config { get; }
    /// <summary>Display name from the Dev Container configuration.</summary>
    public string Name => Config.Name;
    /// <summary>Windows workspace folder that contains the Dev Container file.</summary>
    public string WorkspacePath => Config.WorkspacePath;
    /// <summary>Short description of the image, build, or Compose service used by this container.</summary>
    public string ImageSummary => Config.Compose is not null
        ? UiText.Get("Workload_Text_Compose_0_30434f", "Compose: {0}", Config.Compose.Service)
        : Config.Build is not null
            ? UiText.Get("Workload_Text_Build_0_413948", "Build: {0}", Config.Build.Dockerfile ?? "Dockerfile")
            : Config.Image ?? UiText.Get("Workload_Text_no_image_dc74e6", "(no image)");
    /// <summary>Comma-separated list of forwarded ports requested by the configuration.</summary>
    public string PortsSummary => Config.ForwardPorts.Count == 0 ? UiText.Get("Workload_Text_No_forwarded_ports_b75594", "No forwarded ports") : string.Join(", ", Config.ForwardPorts);
    /// <summary>Lifecycle script phases present in the configuration.</summary>
    public string LifecycleSummary => string.Join(", ", new[]
    {
        Config.Lifecycle.Initialize.Count == 0 ? null : "initialize",
        Config.Lifecycle.OnCreate.Count == 0 ? null : "onCreate",
        Config.Lifecycle.UpdateContent.Count == 0 ? null : "updateContent",
        Config.Lifecycle.PostCreate.Count == 0 ? null : "postCreate",
        Config.Lifecycle.PostStart.Count == 0 ? null : "postStart",
        Config.Lifecycle.PostAttach.Count == 0 ? null : "postAttach",
    }.Where(s => s is not null));
    /// <summary>Count of import warnings, or a no-warning message.</summary>
    public string WarningsSummary => Config.Warnings.Count == 0 ? UiText.Get("Workload_Text_No_warnings_3a1134", "No warnings") : UiText.Get("Workload_Text_0_warning_s_d02ebe", "{0} warning(s)", Config.Warnings.Count);
    /// <summary>Last captured lifecycle command output for the row.</summary>
    public string LifecycleLog => string.IsNullOrWhiteSpace(Config.LifecycleLog) ? UiText.Get("Workload_Text_No_lifecycle_output_yet_4043c0", "No lifecycle output yet.") : Config.LifecycleLog;

    /// <summary>Generated status text indicating whether the backing container is running.</summary>
    [ObservableProperty]
    private string _statusText = UiText.Get("Workload_Text_Not_running_9e3856", "Not running");

    /// <summary>Generated full container id of the running instance, if found.</summary>
    [ObservableProperty]
    private string _containerId = string.Empty;
}

/// <summary>Lists known Dev Containers and drives their lifecycle.</summary>
/// <summary>Lists imported Dev Containers and starts, rebuilds, stops, removes, or opens terminals for them.</summary>
public partial class DevContainersViewModel(
    IDevContainerImporter importer,
    IDevContainerStore store,
    IDevContainerSupervisor supervisor,
    IDevContainerHostCommandPresenter hostCommandPresenter,
    IWslcService wslc,
    DialogService dialogs) : ObservableObject
{
    /// <summary>Generated busy flag used to serialize Dev Container operations.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status text shown above the Dev Containers list.</summary>
    [ObservableProperty]
    private string _statusMessage = UiText.Get("Workload_Text_Ready_5fa7aa", "Ready");

    /// <summary>Currently selected Dev Container row.</summary>
    [ObservableProperty]
    private DevContainerRow? _selected;

    /// <summary>Refreshes app-owned display text for the current language.</summary>
    public void RefreshLanguage()
    {
        StatusMessage = UiText.Translate(StatusMessage);
        foreach (var row in DevContainers) row.RefreshLanguage();
    }

    /// <summary>Rows displayed by the Dev Containers page.</summary>
    public ObservableCollection<DevContainerRow> DevContainers { get; } = new();

    /// <summary>Reloads persisted Dev Container configs and matches them to current containers.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = UiText.Get("Workload_Text_Loading_dev_containers_c83356", "Loading dev containers…");
        try
        {
            IReadOnlyList<ContainerInfo> containers;
            try
            {
                containers = await wslc.ListContainersAsync(all: true);
            }
            catch
            {
                containers = Array.Empty<ContainerInfo>();
            }

            DevContainers.Clear();
            foreach (var config in store.GetAll())
            {
                var row = new DevContainerRow(config);
                UpdateStatus(row, containers);
                DevContainers.Add(row);
            }

            StatusMessage = DevContainers.Count == 0
                ? UiText.Get("Workload_Text_No_dev_containers_Open_a_workspace_3fac89", "No dev containers. Open a workspace folder to import one.")
                : UiText.Get("Workload_Text_0_dev_container_s_88a6a2", "{0} dev container(s)", DevContainers.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Imports a workspace folder containing <c>devcontainer.json</c> after showing a preview.</summary>
    public async Task ImportFolderAsync(string workspacePath)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await importer.ImportAsync(workspacePath);
            if (!result.Success || result.Config is null)
            {
                await dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Import_failed_0a26f4", "Import failed"), result.ErrorMessage ?? UiText.Get("Workload_Text_Could_not_import_devcontainer_json_492691", "Could not import devcontainer.json."));
                return;
            }

            var config = result.Config;
            var preview = BuildPreview(config, result.Warnings);
            var ok = await dialogs.ShowConfirmAsync(UiText.Get("Workload_Text_Import_Dev_Container_1af7e9", "Import Dev Container"), preview, UiText.Get("Workload_Text_Import_2cff9b", "Import"));
            if (!ok)
            {
                return;
            }

            store.Save(config);
            await RefreshAsync();
            StatusMessage = UiText.Get("Workload_Text_Imported_0_94041e", "Imported \"{0}\"", config.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Starts the selected Dev Container, asking before host-side lifecycle commands run.</summary>
    [RelayCommand]
    private async Task UpAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(row, () => supervisor.UpAsync(row.Config,
            approveHostCommandsAsync: hostCommandPresenter.ConfirmAsync), UiText.Get("Workload_Text_Starting_aeed4d", "Starting"), UiText.Get("Workload_Final_9cd854d29b", "Start failed"));
    }

    /// <summary>Rebuilds and starts the selected Dev Container.</summary>
    [RelayCommand]
    private async Task RebuildAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(row, () => supervisor.UpAsync(row.Config, rebuild: true,
            approveHostCommandsAsync: hostCommandPresenter.ConfirmAsync), UiText.Get("Workload_Text_Rebuilding_c3a834", "Rebuilding"), UiText.Get("Workload_Text_Rebuild_failed_8da927", "Rebuild failed"));
    }

    /// <summary>Rebuilds the selected Dev Container without using the image build cache.</summary>
    [RelayCommand]
    private async Task RebuildNoCacheAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(row, () => supervisor.UpAsync(row.Config, rebuild: true, noCache: true,
            approveHostCommandsAsync: hostCommandPresenter.ConfirmAsync), UiText.Get("Workload_Text_Rebuilding_without_cache_1f1e26", "Rebuilding without cache"), UiText.Get("Workload_Text_Rebuild_failed_8da927", "Rebuild failed"));
    }

    /// <summary>Stops the selected Dev Container.</summary>
    [RelayCommand]
    private async Task StopAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = UiText.Get("Workload_Text_Stopping_0_a7625f", "Stopping \"{0}\"…", row.Name);
        try
        {
            await supervisor.StopAsync(row.Config);
            await RefreshAsync();
            StatusMessage = UiText.Get("Workload_Text_Stopped_0_9ae09a", "Stopped \"{0}\"", row.Name);
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Stop_failed_68349a", "Stop failed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Stops, removes, and forgets the selected Dev Container.</summary>
    [RelayCommand]
    private async Task RemoveAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var ok = await dialogs.ShowConfirmAsync(UiText.Get("Workload_Text_Remove_dev_container_89dc8e", "Remove dev container"), UiText.Get("Workload_Text_Stop_remove_and_forget_0_6723d8", "Stop, remove, and forget \"{0}\"?", row.Name), UiText.Get("Workload_Text_Remove_c3812f", "Remove"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = UiText.Get("Workload_Text_Removing_0_4bf34d", "Removing \"{0}\"…", row.Name);
        try
        {
            await supervisor.RemoveAsync(row.Config);
            await RefreshAsync();
            StatusMessage = UiText.Get("Workload_Text_Removed_0_7285c3", "Removed \"{0}\"", row.Name);
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync(UiText.Get("Workload_Final_7a91db72b7", "Remove failed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens a terminal into the running Dev Container.</summary>
    [RelayCommand]
    private void OpenTerminal(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is not null && !string.IsNullOrWhiteSpace(row.ContainerId))
        {
            supervisor.OpenTerminal(row.Config, row.ContainerId);
        }
    }

    private async Task RunOperationAsync(DevContainerRow row, Func<Task<DevContainerOperationResult>> operation, string progress, string failureTitle)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"{progress} \"{row.Name}\"…";
        try
        {
            var result = await operation();
            await RefreshAsync();
            StatusMessage = result.Success ? result.Detail : UiText.Get("Workload_Text_0_failed_47953d", "{0}: failed", row.Name);
            if (!result.Success)
            {
                await dialogs.ShowMessageAsync(failureTitle, result.Detail);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = UiText.Get("Workload_Text_0_1_cancelled_969bd2", "{0} \"{1}\" cancelled.", progress, row.Name);
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Get("Workload_Text_0_failed_47953d", "{0}: failed", row.Name);
            await dialogs.ShowMessageAsync(failureTitle, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void UpdateStatus(DevContainerRow row, IReadOnlyList<ContainerInfo> containers)
    {
        string? name = null;
        if (row.Config.Compose is { } compose)
        {
            var service = compose.Project.Services.FirstOrDefault(s => string.Equals(s.Name, compose.Service, StringComparison.Ordinal));
            name = service is null
                ? null
                : string.IsNullOrWhiteSpace(service.Options.Name)
                    ? compose.Project.ContainerNameFor(service.Name)
                    : service.Options.Name!.Trim();
        }
        else
        {
            name = row.Config.RunOptions.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"devcontainer-{row.Config.Id}";
            }
        }

        var container = containers.FirstOrDefault(c => string.Equals(c.Name.TrimStart('/'), name, StringComparison.Ordinal));
        row.ContainerId = container?.Id ?? string.Empty;
        row.StatusText = container is null
            ? UiText.Get("Workload_Text_Not_running_9e3856", "Not running")
            : container.State == ContainerState.Running
                ? UiText.Get("Workload_Text_Running_f4ccae", "Running")
                : container.State.ToString();
    }

    private static string BuildPreview(DevContainerConfig config, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(UiText.Get("Workload_Text_Name_0_c45829", "Name: {0}", config.Name));
        sb.AppendLine(UiText.Get("Workload_Text_Workspace_0_738edb", "Workspace: {0}", config.WorkspacePath));
        sb.AppendLine(config.Build is null ? UiText.Get("Workload_Text_Image_0_839493", "Image: {0}", config.Image) : UiText.Get("Workload_Text_Build_0_413948", "Build: {0}", config.Build.Context));
        sb.AppendLine(UiText.Get("Workload_Text_Workspace_folder_0_c8349c", "Workspace folder: {0}", config.WorkspaceFolder));
        sb.AppendLine(UiText.Get("Workload_Text_Ports_0_f9a220", "Ports: {0}", (config.ForwardPorts.Count == 0 ? UiText.Get("Workload_Final_140bedbf9c", "none") : string.Join(", ", config.ForwardPorts))));
        sb.AppendLine(UiText.Get("Workload_Text_Environment_variables_0_6d6b79", "Environment variables: {0}", config.ContainerEnv.Count));
        if (config.Features.Count > 0)
        {
            sb.AppendLine(UiText.Get("Workload_Text_Features_0_c0d50f", "Features: {0}", string.Join(", ", config.Features.Select(f => f.Id))));
        }
        if (!string.IsNullOrWhiteSpace(row(config.Lifecycle)))
        {
            sb.AppendLine(UiText.Get("Workload_Text_Lifecycle_ffd469", "Lifecycle: ") + row(config.Lifecycle));
        }
        if (config.Lifecycle.Initialize.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.AppendLine(config.Compose is null
                ? UiText.Get("Workload_Text_Host_initializeCommand_scripts_require_separate_Windows_fa813f", "Host initializeCommand scripts require separate Windows host execution approval on every start or rebuild. Importing does not approve them.")
                : UiText.Get("Workload_Text_Host_initializeCommand_scripts_are_blocked_for_466c5e", "Host initializeCommand scripts are blocked for Compose dev containers."));
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(UiText.Get("Workload_Text_Warnings_f11208", "Warnings:"));
            foreach (var warning in warnings.Take(12))
            {
                sb.AppendLine("• " + UiText.TranslateLines(warning));
            }
            if (warnings.Count > 12)
            {
                sb.AppendLine(UiText.Get("Workload_Text_and_0_more_7bbbe9", "• …and {0} more.", warnings.Count - 12));
            }
        }

        return sb.ToString();

        static string row(DevContainerLifecycle lifecycle) => string.Join(", ", new[]
        {
            lifecycle.Initialize.Count == 0 ? null : "initializeCommand",
            lifecycle.OnCreate.Count == 0 ? null : "onCreateCommand",
            lifecycle.UpdateContent.Count == 0 ? null : "updateContentCommand",
            lifecycle.PostCreate.Count == 0 ? null : "postCreateCommand",
            lifecycle.PostStart.Count == 0 ? null : "postStartCommand",
            lifecycle.PostAttach.Count == 0 ? null : "postAttachCommand",
        }.Where(s => s is not null));
    }
}
