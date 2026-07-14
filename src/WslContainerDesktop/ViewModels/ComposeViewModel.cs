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
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>A compose project row shown on the Compose page, with its live running/total counts.</summary>
public partial class ComposeProjectRow : ObservableObject
{
    public ComposeProjectRow(ComposeProject project)
    {
        Project = project;
    }

    public ComposeProject Project { get; }

    public string Name => Project.Name;

    public int ServiceCount => Project.Services.Count;

    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private string _statusText = "Not running";

    public string ServicesSummary
    {
        get
        {
            var names = Project.Services.Select(s => s.Name).ToList();
            return names.Count == 0 ? "No services" : string.Join(", ", names);
        }
    }
}

/// <summary>
/// Backs the Compose page: lists imported compose projects and drives the supervisor to bring them
/// up/down as a unit. Restart and health policies are enforced only while the app is running.
/// </summary>
public partial class ComposeViewModel : ObservableObject
{
    private readonly IComposeProjectStore _store;
    private readonly ComposeProjectSupervisor _supervisor;
    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private ComposeProjectRow? _selected;

    public ObservableCollection<ComposeProjectRow> Projects { get; } = new();

    public ComposeViewModel(
        IComposeProjectStore store,
        ComposeProjectSupervisor supervisor,
        IWslcService wslc,
        DialogService dialogs)
    {
        _store = store;
        _supervisor = supervisor;
        _wslc = wslc;
        _dialogs = dialogs;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading compose projects…";
        try
        {
            IReadOnlyList<ContainerInfo> containers;
            try
            {
                containers = await _wslc.ListContainersAsync(all: true);
            }
            catch
            {
                containers = Array.Empty<ContainerInfo>();
            }

            var projects = _store.GetAll();
            Projects.Clear();
            foreach (var project in projects)
            {
                var row = new ComposeProjectRow(project);
                UpdateStatus(row, containers);
                Projects.Add(row);
            }

            StatusMessage = Projects.Count == 0
                ? "No compose projects. Import one to get started."
                : $"{Projects.Count} project{(Projects.Count == 1 ? "" : "s")}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void UpdateStatus(ComposeProjectRow row, IReadOnlyList<ContainerInfo> containers)
    {
        var running = 0;
        foreach (var service in row.Project.Services)
        {
            var name = string.IsNullOrWhiteSpace(service.Options.Name)
                ? row.Project.ContainerNameFor(service.Name)
                : service.Options.Name!.Trim();

            var container = containers.FirstOrDefault(c =>
                string.Equals(c.Name.TrimStart('/'), name, StringComparison.Ordinal));
            if (container is not null && container.State == ContainerState.Running)
            {
                running++;
            }
        }

        row.RunningCount = running;
        row.StatusText = running == 0
            ? "Not running"
            : running == row.ServiceCount
                ? "Running"
                : $"Partial ({running}/{row.ServiceCount})";
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new ImportComposeDialog();
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        ComposeProject project;
        try
        {
            project = ComposeImporter.ParseProject(dialog.Yaml, baseDirectory: dialog.BaseDirectory);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Import failed", ex.Message);
            return;
        }

        if (project.Services.Count == 0)
        {
            await _dialogs.ShowMessageAsync(
                "Nothing to import",
                "No services with an image were found in the compose file.");
            return;
        }

        // Warn about compose features that aren't supported and were dropped during import, so the
        // user can decide before bringing the project up.
        if (project.Warnings.Count > 0)
        {
            const int maxShown = 12;
            var shown = project.Warnings.Take(maxShown);
            var more = project.Warnings.Count - maxShown;
            var body = string.Join("\n", shown.Select(w => "• " + w));
            if (more > 0)
            {
                body += $"\n• …and {more} more.";
            }

            var proceed = await _dialogs.ShowConfirmAsync(
                "Some features aren't supported",
                "This compose file uses features this app can't reproduce. They were ignored:\n\n" +
                body + "\n\nImport the project anyway?",
                "Import anyway");
            if (!proceed)
            {
                return;
            }
        }

        // Let the user name the project (defaults to the compose 'name:' or "compose").
        var nameDialog = new SimpleInputDialog("Name this project", "Project name", project.Name)
        {
            Value = project.Name,
        };
        if (await _dialogs.ShowDialogAsync(nameDialog) == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(nameDialog.Value))
        {
            project.Name = nameDialog.Value.Trim();
        }

        // Namespace project-created volumes/networks with the (now-final) project name so removing
        // this project can never delete or detach resources another project shares by bare name.
        project.ApplyProjectNamespacing();

        _store.Save(project);
        await RefreshAsync();
        StatusMessage = $"Imported project \"{project.Name}\" ({project.Services.Count} services)";

        var bringUp = await _dialogs.ShowConfirmAsync(
            "Bring project up now?",
            $"\"{project.Name}\" has {project.Services.Count} service(s). Start them now in dependency order?\n\n" +
            "Restart and health-check policies are enforced by this app, so they only apply while it is running.",
            "Bring up");
        if (bringUp)
        {
            await BringUpAsync(project);
        }
    }

    [RelayCommand]
    private async Task UpAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        await BringUpAsync(row.Project);
    }

    private async Task BringUpAsync(ComposeProject project)
    {
        IsBusy = true;
        StatusMessage = $"Bringing up \"{project.Name}\"…";
        try
        {
            var result = await _supervisor.UpAsync(project);
            await RefreshAsync();

            if (result.AllSucceeded)
            {
                StatusMessage = $"\"{project.Name}\" up — {result.Started} service(s) started";
            }
            else
            {
                var failed = result.Services.Where(s => !s.Success).ToList();
                StatusMessage = $"\"{project.Name}\" partially up ({result.Started}/{result.Services.Count})";

                if (failed.Any(f => ComposeProjectSupervisor.IsMountLimitFailure(f.Detail)))
                {
                    var restart = await _dialogs.ShowConfirmAsync(
                        "wslc mount limit reached",
                        $"Some services couldn't mount their configs/secrets because wslc hit its "
                            + "session bind-mount limit (15 distinct host paths). Restart the WSL "
                            + "session to release the slots, then bring the project up again. "
                            + "Restarting stops all running containers. Restart now?",
                        "Restart WSL session");
                    if (restart)
                    {
                        await RestartSessionCoreAsync();
                    }
                    return;
                }

                await _dialogs.ShowMessageAsync(
                    "Some services failed to start",
                    string.Join("\n", failed.Select(f => $"• {f.Service}: {f.Detail}")));
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Bring up failed", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Bring project down",
            $"Stop and remove all containers for \"{row.Name}\"? The project definition is kept.",
            "Bring down");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Bringing down \"{row.Name}\"…";
        try
        {
            await _supervisor.DownAsync(row.Name);
            await RefreshAsync();
            StatusMessage = $"\"{row.Name}\" is down";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Bring down failed", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Restart project",
            $"Restart \"{row.Name}\"? This brings the project down and back up in dependency order.",
            "Restart");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Restarting \"{row.Name}\"…";
        try
        {
            var result = await _supervisor.RestartAsync(row.Name);
            await RefreshAsync();

            if (result.AllSucceeded)
            {
                StatusMessage = $"\"{row.Name}\" restarted — {result.Started} service(s) started";
            }
            else
            {
                var failed = result.Services.Where(s => !s.Success).ToList();
                StatusMessage = $"\"{row.Name}\" partially up ({result.Started}/{result.Services.Count})";
                if (failed.Count > 0)
                {
                    await _dialogs.ShowMessageAsync(
                        "Some services failed to start",
                        string.Join("\n", failed.Select(f => $"• {f.Service}: {f.Detail}")));
                }
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Restart failed", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove project",
            $"Remove the project definition for \"{row.Name}\"? This also brings it down (stops and removes its containers) and deletes the volumes it created. External volumes are preserved.",
            "Remove");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _supervisor.DownAsync(row.Name, removeVolumes: true);
            _store.Delete(row.Name);
            _supervisor.CleanStaging(row.Name);
            await RefreshAsync();
            StatusMessage = $"Removed \"{row.Name}\"";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Remove failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Terminates the wslc session to release leaked bind-mount slots (the 15-distinct-host-path
    /// cap that blocks config/secret mounts). This stops all running containers, so it is confirmed
    /// first. Exposed as a toolbar command and offered automatically when a bring-up hits the limit.
    /// </summary>
    [RelayCommand]
    private async Task RestartSessionAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Restart WSL session",
            "This releases wslc's leaked bind-mount slots (needed when config/secret mounts start "
                + "failing after ~15 distinct mounts). It also STOPS ALL running containers. "
                + "Continue?",
            "Restart");
        if (!ok)
        {
            return;
        }

        await RestartSessionCoreAsync();
    }

    private async Task RestartSessionCoreAsync()
    {
        IsBusy = true;
        StatusMessage = "Restarting WSL session…";
        try
        {
            var result = await _wslc.RestartSessionAsync();
            await RefreshAsync();
            StatusMessage = result.Success
                ? "WSL session restarted — mount slots released. Bring your projects up again."
                : "Restart failed";
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(
                    "Restart failed",
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Restart failed", ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
