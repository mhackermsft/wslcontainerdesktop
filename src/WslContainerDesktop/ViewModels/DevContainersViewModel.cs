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

    /// <summary>Parsed Dev Container configuration backing this row.</summary>
    public DevContainerConfig Config { get; }
    /// <summary>Display name from the Dev Container configuration.</summary>
    public string Name => Config.Name;
    /// <summary>Windows workspace folder that contains the Dev Container file.</summary>
    public string WorkspacePath => Config.WorkspacePath;
    /// <summary>Short description of the image, build, or Compose service used by this container.</summary>
    public string ImageSummary => Config.Compose is not null
        ? $"Compose: {Config.Compose.Service}"
        : Config.Build is not null
            ? $"Build: {Config.Build.Dockerfile ?? "Dockerfile"}"
            : Config.Image ?? "(no image)";
    /// <summary>Comma-separated list of forwarded ports requested by the configuration.</summary>
    public string PortsSummary => Config.ForwardPorts.Count == 0 ? "No forwarded ports" : string.Join(", ", Config.ForwardPorts);
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
    public string WarningsSummary => Config.Warnings.Count == 0 ? "No warnings" : $"{Config.Warnings.Count} warning(s)";
    /// <summary>Last captured lifecycle command output for the row.</summary>
    public string LifecycleLog => string.IsNullOrWhiteSpace(Config.LifecycleLog) ? "No lifecycle output yet." : Config.LifecycleLog;

    /// <summary>Generated status text indicating whether the backing container is running.</summary>
    [ObservableProperty]
    private string _statusText = "Not running";

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
    private string _statusMessage = "Ready";

    /// <summary>Currently selected Dev Container row.</summary>
    [ObservableProperty]
    private DevContainerRow? _selected;

    /// <summary>Rows displayed by the Dev Containers page.</summary>
    public ObservableCollection<DevContainerRow> DevContainers { get; } = new();

    /// <summary>Reloads persisted Dev Container configs and matches them to current containers.</summary>
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
            approveHostCommandsAsync: hostCommandPresenter.ConfirmAsync), "Starting", "Start failed");
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
            approveHostCommandsAsync: hostCommandPresenter.ConfirmAsync), "Rebuilding", "Rebuild failed");
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
            approveHostCommandsAsync: hostCommandPresenter.ConfirmAsync), "Rebuilding without cache", "Rebuild failed");
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
        StatusMessage = $"Stopping \"{row.Name}\"…";
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

        var ok = await dialogs.ShowConfirmAsync("Remove dev container", $"Stop, remove, and forget \"{row.Name}\"?", "Remove");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Removing \"{row.Name}\"…";
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
            StatusMessage = result.Success ? result.Detail : $"{row.Name}: failed";
            if (!result.Success)
            {
                await dialogs.ShowMessageAsync(failureTitle, result.Detail);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{progress} \"{row.Name}\" cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"{row.Name}: failed";
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
        if (config.Features.Count > 0)
        {
            sb.AppendLine($"Features: {string.Join(", ", config.Features.Select(f => f.Id))}");
        }
        if (!string.IsNullOrWhiteSpace(row(config.Lifecycle)))
        {
            sb.AppendLine("Lifecycle: " + row(config.Lifecycle));
        }
        if (config.Lifecycle.Initialize.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            sb.AppendLine(config.Compose is null
                ? "Host initializeCommand scripts require separate Windows host execution approval on every start or rebuild. Importing does not approve them."
                : "Host initializeCommand scripts are blocked for Compose dev containers.");
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
