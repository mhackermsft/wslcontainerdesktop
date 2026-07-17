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

public partial class DevContainerRow : ObservableObject
{
    public DevContainerRow(DevContainerConfig config)
    {
        Config = config;
    }

    public DevContainerConfig Config { get; }
    public string Name => Config.Name;
    public string WorkspacePath => Config.WorkspacePath;
    public string ImageSummary => Config.Build is not null ? $"Build: {Config.Build.Dockerfile ?? "Dockerfile"}" : Config.Image ?? "(no image)";
    public string PortsSummary => Config.ForwardPorts.Count == 0 ? "No forwarded ports" : string.Join(", ", Config.ForwardPorts);
    public string LifecycleSummary => string.Join(", ", new[] { Config.PostCreateCommand is null ? null : "postCreate", Config.PostStartCommand is null ? null : "postStart" }.Where(s => s is not null));
    public string WarningsSummary => Config.Warnings.Count == 0 ? "No warnings" : $"{Config.Warnings.Count} warning(s)";
    public string LifecycleLog => string.IsNullOrWhiteSpace(Config.LifecycleLog) ? "No lifecycle output yet." : Config.LifecycleLog;

    [ObservableProperty]
    private string _statusText = "Not running";

    [ObservableProperty]
    private string _containerId = string.Empty;
}

/// <summary>Lists known Dev Containers and drives their MVP lifecycle.</summary>
public partial class DevContainersViewModel(
    IDevContainerImporter importer,
    IDevContainerStore store,
    IDevContainerSupervisor supervisor,
    IWslcService wslc,
    DialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DevContainerRow? _selected;

    public ObservableCollection<DevContainerRow> DevContainers { get; } = new();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading dev containers…";
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
                ? "No dev containers. Open a workspace folder to import one."
                : $"{DevContainers.Count} dev container{(DevContainers.Count == 1 ? "" : "s")}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportFolderAsync(string workspacePath)
    {
        IsBusy = true;
        try
        {
            var result = await importer.ImportAsync(workspacePath);
            if (!result.Success || result.Config is null)
            {
                await dialogs.ShowMessageAsync("Import failed", result.ErrorMessage ?? "Could not import devcontainer.json.");
                return;
            }

            var config = result.Config;
            var preview = BuildPreview(config, result.Warnings);
            var ok = await dialogs.ShowConfirmAsync("Import Dev Container", preview, "Import");
            if (!ok)
            {
                return;
            }

            store.Save(config);
            await RefreshAsync();
            StatusMessage = $"Imported \"{config.Name}\"";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(row, () => supervisor.UpAsync(row.Config), "Starting", "Start failed");
    }

    [RelayCommand]
    private async Task RebuildAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(row, () => supervisor.UpAsync(row.Config, rebuild: true), "Rebuilding", "Rebuild failed");
    }

    [RelayCommand]
    private async Task RebuildNoCacheAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(row, () => supervisor.UpAsync(row.Config, rebuild: true, noCache: true), "Rebuilding without cache", "Rebuild failed");
    }

    [RelayCommand]
    private async Task StopAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await supervisor.StopAsync(row.Config);
            await RefreshAsync();
            StatusMessage = $"Stopped \"{row.Name}\"";
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync("Stop failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        var ok = await dialogs.ShowConfirmAsync("Remove dev container", $"Stop, remove, and forget \"{row.Name}\"?", "Remove");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await supervisor.RemoveAsync(row.Config);
            await RefreshAsync();
            StatusMessage = $"Removed \"{row.Name}\"";
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync("Remove failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenTerminal(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is not null && !string.IsNullOrWhiteSpace(row.ContainerId))
        {
            supervisor.OpenTerminal(row.Config, row.ContainerId);
        }
    }

    [RelayCommand]
    private void OpenVsCode(DevContainerRow? row)
    {
        row ??= Selected;
        if (row is not null)
        {
            supervisor.OpenInVsCode(row.Config);
        }
    }

    private async Task RunOperationAsync(DevContainerRow row, Func<Task<DevContainerOperationResult>> operation, string progress, string failureTitle)
    {
        IsBusy = true;
        StatusMessage = $"{progress} \"{row.Name}\"…";
        try
        {
            var result = await operation();
            await RefreshAsync();
            StatusMessage = result.Success ? result.Detail : $"{row.Name}: failed";
            if (!result.Success)
            {
                await dialogs.ShowMessageAsync(failureTitle, result.Detail);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void UpdateStatus(DevContainerRow row, IReadOnlyList<ContainerInfo> containers)
    {
        var name = row.Config.RunOptions.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"devcontainer-{row.Config.Id}";
        }

        var container = containers.FirstOrDefault(c => string.Equals(c.Name.TrimStart('/'), name, StringComparison.Ordinal));
        row.ContainerId = container?.Id ?? string.Empty;
        row.StatusText = container is null
            ? "Not running"
            : container.State == ContainerState.Running
                ? "Running"
                : container.State.ToString();
    }

    private static string BuildPreview(DevContainerConfig config, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {config.Name}");
        sb.AppendLine($"Workspace: {config.WorkspacePath}");
        sb.AppendLine(config.Build is null ? $"Image: {config.Image}" : $"Build: {config.Build.Context}");
        sb.AppendLine($"Workspace folder: {config.WorkspaceFolder}");
        sb.AppendLine($"Ports: {(config.ForwardPorts.Count == 0 ? "none" : string.Join(", ", config.ForwardPorts))}");
        sb.AppendLine($"Environment variables: {config.ContainerEnv.Count}");
        if (!string.IsNullOrWhiteSpace(config.PostCreateCommand))
        {
            sb.AppendLine("Lifecycle: postCreateCommand");
        }
        if (!string.IsNullOrWhiteSpace(config.PostStartCommand))
        {
            sb.AppendLine("Lifecycle: postStartCommand");
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var warning in warnings.Take(12))
            {
                sb.AppendLine("• " + warning);
            }
            if (warnings.Count > 12)
            {
                sb.AppendLine($"• …and {warnings.Count - 12} more.");
            }
        }

        return sb.ToString();
    }
}
