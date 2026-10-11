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
/// <summary>Observable row for one imported Compose project in the Compose page.</summary>
public partial class ComposeProjectRow : ObservableObject
{
    /// <summary>
    /// Returns the project name. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Creates a row and wires it to the page-level service-management command.</summary>
    public ComposeProjectRow(ComposeProject project, IAsyncRelayCommand<ComposeProjectRow?> manageServicesCommand)
    {
        Project = project;
        ManageServicesCommand = manageServicesCommand;
    }

    /// <summary>Notifies localized row labels without changing persisted values or output.</summary>
    public void RefreshLanguage()
    {
        StatusText = UiText.Translate(StatusText);
        OnPropertyChanged(nameof(ServicesSummary));
    }

    /// <summary>Persisted Compose project definition backing this row.</summary>
    public ComposeProject Project { get; }

    /// <summary>Command invoked by service-level buttons in this row.</summary>
    public IAsyncRelayCommand<ComposeProjectRow?> ManageServicesCommand { get; }

    /// <summary>Project name shown in the list.</summary>
    public string Name => Project.Name;

    /// <summary>Number of services in the Compose project.</summary>
    public int ServiceCount => Project.Services.Count;
    /// <summary>Total desired container instances after replica overrides are applied.</summary>
    public long DesiredInstanceCount => Project.Services.Sum(s => (long)ComposeReconciliationPlanner.DesiredReplicas(Project, s, new()));

    /// <summary>Generated count of currently running instances matched to the project.</summary>
    [ObservableProperty]
    private int _runningCount;

    /// <summary>Generated status text such as running, partial, or not running.</summary>
    [ObservableProperty]
    private string _statusText = UiText.Get("Workload_Text_Not_running_9e3856", "Not running");

    /// <summary>Human-readable service and replica summary for the row.</summary>
    public string ServicesSummary
    {
        get
        {
            var names = Project.Services.Select(s => $"{s.Name} × {ComposeReconciliationPlanner.DesiredReplicas(Project, s, new())}").ToList();
            return names.Count == 0 ? UiText.Get("Workload_Text_No_services_76de88", "No services") : string.Join(", ", names);
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
    private int _busyOperations;

    /// <summary>Generated busy flag shared by Compose page commands.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ManageServicesCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>Status text displayed above the Compose project list.</summary>
    [ObservableProperty]
    private string _statusMessage = UiText.Get("Workload_Text_Ready_5fa7aa", "Ready");

    /// <summary>Currently selected Compose project row.</summary>
    [ObservableProperty]
    private ComposeProjectRow? _selected;

    /// <summary>Compose projects displayed by the page.</summary>
    public ObservableCollection<ComposeProjectRow> Projects { get; } = new();

    /// <summary>Refreshes app-owned display text for the current language.</summary>
    public void RefreshLanguage()
    {
        StatusMessage = UiText.Translate(StatusMessage);
        foreach (var row in Projects) row.RefreshLanguage();
    }

    /// <summary>Creates the Compose page model with persistence, supervisor, <c>wslc</c>, and dialog collaborators.</summary>
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

    /// <summary>Reloads stored projects and reconciles each row with the current container inventory.</summary>
    [RelayCommand(CanExecute = nameof(CanManageServices))]
    public async Task RefreshAsync()
    {
        BeginBusyOperation();
        StatusMessage = UiText.Get("Workload_Text_Loading_compose_projects_33a0b7", "Loading compose projects…");
        try
        {
            IReadOnlyList<ContainerInfo>? containers = null;
            string? inventoryError = null;
            try
            {
                containers = await _wslc.ListContainersAsync(all: true);
            }
            catch (Exception ex)
            {
                inventoryError = ex.Message;
            }

            var projects = _store.GetAll();
            Projects.Clear();
            foreach (var project in projects)
            {
                var row = new ComposeProjectRow(project, ManageServicesCommand);
                if (containers is null) row.StatusText = UiText.Get("Workload_Text_Status_unavailable_7eb5af", "Status unavailable");
                else UpdateStatus(row, containers);
                Projects.Add(row);
            }

            StatusMessage = Projects.Count == 0
                ? UiText.Get("Workload_Text_No_compose_projects_Import_one_to_5185a1", "No compose projects. Import one to get started.")
                : UiText.Get("Workload_Text_0_project_s_53276f", "{0} project(s)", Projects.Count);
            if (inventoryError is not null)
                StatusMessage += UiText.Get("Workload_Text_Container_status_unavailable_0_f892d1", " - Container status unavailable: {0}", inventoryError);
            if (_supervisor.ReconciliationWarnings.Count > 0)
            {
                StatusMessage += UiText.Get("Workload_Text_Network_attention_f45fdf", " - Network attention: ") + string.Join("; ", _supervisor.ReconciliationWarnings.Select(UiText.TranslateLines));
            }
        }
        finally
        {
            EndBusyOperation();
        }
    }

    private static void UpdateStatus(ComposeProjectRow row, IReadOnlyList<ContainerInfo> containers)
    {
        var running = ComposeReconciliationPlanner.ObservedRunningCount(row.Project, containers);

        row.RunningCount = running;
        row.StatusText = running == 0
            ? row.DesiredInstanceCount == 0 ? UiText.Get("Workload_Text_Scaled_to_zero_edb205", "Scaled to zero") : UiText.Get("Workload_Text_Not_running_0_0_instances_e67878", "Not running (0/{0} instances)", row.DesiredInstanceCount)
            : running == row.DesiredInstanceCount
                ? UiText.Get("Workload_Text_Running_0_1_instances_684b8f", "Running ({0}/{1} instances)", running, row.DesiredInstanceCount)
                : UiText.Get("Workload_Text_Partial_0_1_instances_ce3931", "Partial ({0}/{1} instances)", running, row.DesiredInstanceCount);
    }

    /// <summary>Prompts for Compose YAML and imports it through the shared import flow.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var dialog = new ImportComposeDialog();
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        await ImportProjectFromYamlAsync(dialog.Yaml, baseDirectory: dialog.BaseDirectory);
    }

    /// <summary>
    /// Imports a compose project from raw YAML text: parses it, warns about unsupported features,
    /// lets the user name it, persists it, and offers to bring it up. Shared by the file-picker
    /// import command and the one-click template gallery.
    /// </summary>
    /// <param name="yaml">The docker-compose YAML content.</param>
    /// <param name="baseDirectory">Directory used to resolve relative env_file/.env paths, if any.</param>
    /// <param name="suggestedName">Pre-selected project name (e.g. from a template); falls back to the compose <c>name:</c>.</param>
    public async Task ImportProjectFromYamlAsync(string yaml, string? baseDirectory = null, string? suggestedName = null)
    {
        ComposeProject project;
        try
        {
            project = ComposeImporter.ParseProject(yaml, baseDirectory: baseDirectory);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Import_failed_0a26f4", "Import failed"), ex.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            project.Name = suggestedName.Trim();
        }

        if (project.Services.Count == 0)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Workload_Text_Nothing_to_import_c4497f", "Nothing to import"),
                UiText.Get("Workload_Text_No_services_with_an_image_were_ffb50a", "No services with an image were found in the compose file."));
            return;
        }

        // Warn about compose features that aren't supported and were dropped during import, so the
        // user can decide before bringing the project up.
        if (project.Warnings.Count > 0)
        {
            const int maxShown = 12;
            var shown = project.Warnings.Take(maxShown).Select(new ComposePreviewProjection(project).Redact).Select(UiText.TranslateLines);
            var more = project.Warnings.Count - maxShown;
            var body = string.Join("\n", shown.Select(w => "• " + w));
            if (more > 0)
            {
                body += UiText.Get("Workload_Text_and_0_more_a38682", "\n• …and {0} more.", more);
            }

            var proceed = await _dialogs.ShowConfirmAsync(
                UiText.Get("Workload_Text_Compose_compatibility_notes_7d8a36", "Compose compatibility notes"),
                UiText.Get("Workload_Text_Review_these_unsupported_or_engine_dependent_dfe63d", "Review these unsupported or engine-dependent settings before importing:\n\n") +
                body + UiText.Get("Workload_Text_Import_the_project_anyway_83f426", "\n\nImport the project anyway?"),
                UiText.Get("Workload_Text_Import_anyway_e06eac", "Import anyway"));
            if (!proceed)
            {
                return;
            }
        }

        // Let the user name the project (defaults to the compose 'name:' or "compose").
        var nameDialog = new SimpleInputDialog(UiText.Get("Workload_Text_Name_this_project_8a5ddc", "Name this project"), UiText.Get("Workload_Text_Project_name_254981", "Project name"), project.Name)
        {
            Value = project.Name,
        };
        if (await _dialogs.ShowDialogAsync(nameDialog) != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(nameDialog.Value)) return;
        project.Name = nameDialog.Value.Trim();

        // Namespace project-created volumes/networks with the (now-final) project name so removing
        // this project can never delete or detach resources another project shares by bare name.
        project.ApplyProjectNamespacing();

        var importChoice = await _dialogs.ShowDialogAsync(new ContentDialog
        {
            Title = UiText.Get("Workload_Text_Import_Compose_project_d389da", "Import Compose project"),
            Content = new TextBlock
            {
                Text = new ComposePreviewProjection(project).Redact(
                    UiText.Get("Workload_Text_0_has_1_service_s_Review_6f59f1", "\"{0}\" has {1} service(s). Review compatibility before applying, or save an import without starting it.\n\nApp-owned restart and health supervision require this app to remain open.", project.Name, project.Services.Count)),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryButtonText = UiText.Get("Workload_Text_Review_and_apply_4954f1", "Review and apply"),
            SecondaryButtonText = UiText.Get("Workload_Text_Import_only_c9bd79", "Import only"),
            CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        });
        if (importChoice == ContentDialogResult.Primary)
        {
            await BringUpAsync(project);
        }
        else if (importChoice == ContentDialogResult.Secondary)
        {
            // Explicit import-only choice, not a side effect of cancelling deployment review.
            _store.Save(project);
            await RefreshAsync();
            StatusMessage = UiText.Get("Workload_Text_Imported_project_0_1dff8a", "Imported project \"{0}\"", new ComposePreviewProjection(project).Redact(project.Name));
        }
    }

    /// <summary>
    /// One-click template path: parse <paramref name="yaml"/>, import it as a project under
    /// <paramref name="suggestedName"/>, and offer resolved compatibility review.
    /// Re-importing an already-imported project refreshes it only after confirmation,
    /// so a template's Launch button is idempotent. Errors are surfaced via the dialog service.
    /// </summary>
    /// <returns>False when configuration validation/import failed; not a runtime health guarantee.</returns>
    public async Task<bool> ImportAndUpAsync(string yaml, string? suggestedName = null, string? baseDirectory = null)
    {
        ComposeProject project;
        try
        {
            project = ComposeImporter.ParseProject(yaml, baseDirectory: baseDirectory);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Launch_failed_1755d5", "Launch failed"), ex.Message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            project.Name = suggestedName.Trim();
        }

        if (project.Services.Count == 0)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Workload_Text_Nothing_to_launch_e88455", "Nothing to launch"),
                UiText.Get("Workload_Text_No_services_with_an_image_were_ffb50a", "No services with an image were found in the compose file."));
            return false;
        }

        // Step around anything already deployed before namespacing locks in the project name:
        // a repeat launch becomes its own project with its own network and free host ports, rather
        // than colliding with (or quietly adopting) the deployment already running.
        var adjustment = await new DeploymentConflictResolver(_wslc).ResolveProjectAsync(project);

        project.ApplyProjectNamespacing();
        var launched = await BringUpAsync(project);
        if (launched && adjustment.Adjusted)
        {
            StatusMessage = adjustment.Summary;
        }

        return launched;
    }

    /// <summary>
    /// Tears a template-launched project fully down: stops and removes its containers and networks
    /// (and, when <paramref name="removeVolumes"/> is true, its data volumes), then deletes the
    /// stored project definition so the system is back to its pre-launch state. Used by the Templates
    /// page's one-click Remove. External resources are always preserved by the supervisor.
    /// </summary>
    public async Task RemoveProjectAsync(string projectName, bool removeVolumes)
    {
        await _supervisor.DownAsync(projectName, removeVolumes);
        _store.Delete(projectName);
        await RefreshAsync();
    }

    /// <summary>Applies the selected Compose project after compatibility review.</summary>
    [RelayCommand]
    private async Task UpAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null || IsBusy)
        {
            return;
        }

        await BringUpAsync(row.Project);
    }

    private bool CanManageServices() => !IsBusy;

    // Navigation can overlap refreshes on this singleton, including lifecycle-owned refreshes.
    // All callers resume on the UI context; each operation releases only its own busy ownership.
    private void BeginBusyOperation()
    {
        _busyOperations++;
        IsBusy = true;
    }

    private void EndBusyOperation()
    {
        _busyOperations--;
        IsBusy = _busyOperations > 0;
    }

    /// <summary>Opens the per-service operation dialog and applies the reviewed request.</summary>
    [RelayCommand(CanExecute = nameof(CanManageServices))]
    private async Task ManageServicesAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null || IsBusy)
        {
            return;
        }

        BeginBusyOperation();
        try
        {
            var dialog = new ComposeServicesDialog(row.Project);
            if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            var request = dialog.Request;
            // An empty target set means "whole project" to the supervisor. Never let this
            // targeted UI fall through to that meaning, even if dialog validation changes.
            if (request.Services.Count == 0)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Select_services_aeef2c", "Select services"), UiText.Get("Workload_Text_Select_at_least_one_service_No_2358e8", "Select at least one service. No operation was performed."));
                return;
            }

            StatusMessage = $"{dialog.OperationLabel} — \"{row.Name}\"…";
            var review = await _supervisor.PrepareReviewAsync(row.Project, request);
            var confirmed = await _dialogs.ShowDialogAsync(new ComposePreviewDialog(review.Preview)) == ContentDialogResult.Primary;
            var outcome = await _supervisor.ApplyReviewedAsync(review, confirmed, dialog.SaveReplicaOverrides);
            if (!outcome.AllSucceeded && outcome.Execution is null)
            {
                StatusMessage = outcome.Message;
                return;
            }
            var result = outcome.ToUpResult();
            await RefreshAsync();
            StatusMessage = result.AllSucceeded
                ? UiText.Get("Workload_Text_0_completed_for_1_b46404", "{0} completed for \"{1}\"", dialog.OperationLabel, row.Name)
                : UiText.Get("Workload_Text_0_for_1_some_services_failed_553312", "{0} for \"{1}\" — some services failed", dialog.OperationLabel, row.Name);
            if (request.Operation is ComposeLifecycleOperation.Up or ComposeLifecycleOperation.Restart)
            {
                StatusMessage += UiText.Get("Workload_Text_0_instance_s_started_83484d", " — {0} instance(s) started", result.Started);
            }

            await ShowServiceOutcomesAsync($"{dialog.OperationLabel}: {row.Name}", result, row.Project);
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Get("Workload_Text_Service_operation_failed_d4aeef", "Service operation failed");
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Service_operation_failed_d4aeef", "Service operation failed"), ex.Message);
        }
        finally
        {
            EndBusyOperation();
        }
    }

    /// <summary>
    /// Reports per-service outcomes when there is something to act on. A clean run needs no modal:
    /// the status line already reports it, and planner reasoning shown after the fact reads like a
    /// problem rather than an explanation.
    /// </summary>
    private Task ShowServiceOutcomesAsync(string title, ComposeUpResult result, ComposeProject project)
    {
        var needsAttention = result.Services.Any(s => !s.Success || !string.IsNullOrWhiteSpace(s.Warning));
        if (!needsAttention && result.Services.Count > 0)
        {
            return Task.CompletedTask;
        }

        var safe = new ComposePreviewProjection(project);
        return _dialogs.ShowMessageAsync(safe.Redact(title), result.Services.Count == 0
            ? UiText.Get("Workload_Text_No_service_actions_were_performed_c99eee", "No service actions were performed.")
            : string.Join("\n", result.Services.Select(service =>
            {
                // The planner's reason explains why an action was chosen; it is only worth showing
                // when that action did not succeed.
                var reason = service.Success ? null : result.Plan?.Services.FirstOrDefault(entry =>
                    string.Equals(entry.InstanceKey, service.InstanceKey, StringComparison.Ordinal))?.Reason;
                return safe.Redact($"• {service.InstanceKey}: {UiText.Translate(service.Action.ToString())}{(service.Success ? "" : UiText.Get("Workload_Final_c695ffc1a0", " (failed)"))} — {UiText.TranslateLines(service.Detail)}" +
                    (string.IsNullOrWhiteSpace(reason) || reason == service.Detail ? "" : UiText.Get("Workload_Text_Reason_0_2fa689", "\n  Reason: {0}", UiText.TranslateLines(reason))) +
                    (string.IsNullOrWhiteSpace(service.Warning) ? "" : UiText.Get("Workload_Text_Warning_0_bebde3", "\n  Warning: {0}", UiText.TranslateLines(service.Warning))));
            })));
    }

    private async Task<bool> BringUpAsync(ComposeProject project)
    {
        BeginBusyOperation();
        StatusMessage = UiText.Get("Workload_Text_Bringing_up_0_fc2ceb", "Bringing up \"{0}\"…", project.Name);
        try
        {
            var result = await _supervisor.UpAsync(project);
            await RefreshAsync();
            if (result.AllSucceeded)
            {
                StatusMessage = UiText.Get("Workload_Text_0_applied_1_instance_s_started_b61588", "\"{0}\" applied — {1} instance(s) started", project.Name, result.Started);
            }
            else
            {
                var failed = result.Services.Where(s => !s.Success).ToList();
                StatusMessage = UiText.Get("Workload_Text_0_apply_incomplete_1_instance_s_c1d8d4", "\"{0}\" apply incomplete — {1} instance(s) started", project.Name, result.Started);

                if (failed.Any(f => ComposeProjectSupervisor.IsMountLimitFailure(f.Detail)))
                {
                    var restart = await _dialogs.ShowConfirmAsync(
                        UiText.Get("Workload_Text_wslc_mount_limit_reached_c1cb82", "wslc mount limit reached"),
                        UiText.Get("Workload_Text_Some_services_couldn_t_mount_their_679633", "Some services couldn't mount their configs/secrets because wslc hit its session bind-mount limit (15 distinct host paths). Restart the WSL session to release the slots, then bring the project up again. Restarting stops all running containers. Restart now?"),
                        UiText.Get("Workload_Text_Restart_WSL_session_574951", "Restart WSL session"));
                    if (restart)
                    {
                        await RestartSessionCoreAsync();
                    }
                    return false;
                }

            }

            await ShowServiceOutcomesAsync(UiText.Get("Workload_Text_Apply_0_9d7f20", "Apply: {0}", project.Name), result, project);
            return result.AllSucceeded;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Bring_up_failed_1f4ce8", "Bring up failed"), new ComposePreviewProjection(project).Redact(ex.Message));
            StatusMessage = UiText.Get("Workload_Text_Error_54a0e8", "Error");
            return false;
        }
        finally
        {
            EndBusyOperation();
        }
    }

    /// <summary>Stops and removes containers for the selected project while keeping the saved definition.</summary>
    [RelayCommand]
    private async Task DownAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null || IsBusy)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Bring_project_down_1ebeb2", "Bring project down"),
            UiText.Get("Workload_Text_Stop_and_remove_all_containers_for_e1a7c1", "Stop and remove all containers for \"{0}\"? The project definition is kept.", row.Name),
            UiText.Get("Workload_Text_Bring_down_151b4b", "Bring down"));
        if (!ok)
        {
            return;
        }

        BeginBusyOperation();
        StatusMessage = UiText.Get("Workload_Text_Bringing_down_0_952aea", "Bringing down \"{0}\"…", row.Name);
        try
        {
            await _supervisor.DownAsync(row.Name);
            await RefreshAsync();
            StatusMessage = UiText.Get("Workload_Text_0_is_down_5a240a", "\"{0}\" is down", row.Name);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Bring_down_failed_da796d", "Bring down failed"), ex.Message);
            StatusMessage = UiText.Get("Workload_Text_Error_54a0e8", "Error");
        }
        finally
        {
            EndBusyOperation();
        }
    }

    /// <summary>Restarts existing containers for the selected project without recreating them.</summary>
    [RelayCommand]
    private async Task RestartAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null || IsBusy)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Restart_project_101a59", "Restart project"),
            UiText.Get("Workload_Text_Stop_and_start_the_existing_containers_536030", "Stop and start the existing containers for \"{0}\" in dependency order?\n\nRestart does not create or recreate containers, build images, or apply configuration changes. Use Up (Apply) to apply changes.", row.Name),
            UiText.Get("Workload_Text_Restart_6b983a", "Restart"));
        if (!ok)
        {
            return;
        }

        BeginBusyOperation();
        StatusMessage = UiText.Get("Workload_Text_Restarting_0_aba7d5", "Restarting \"{0}\"…", row.Name);
        try
        {
            var result = await _supervisor.OperateAsync(row.Name, new ComposeOperationRequest
            {
                Operation = ComposeLifecycleOperation.Restart,
            });
            await RefreshAsync();
            if (result.AllSucceeded)
            {
                StatusMessage = UiText.Get("Workload_Text_0_restarted_1_instance_s_started_d1b2df", "\"{0}\" restarted — {1} instance(s) started", row.Name, result.Started);
            }
            else
            {
                StatusMessage = UiText.Get("Workload_Text_0_restart_incomplete_1_instance_s_5a55f5", "\"{0}\" restart incomplete — {1} instance(s) started", row.Name, result.Started);
            }

            await ShowServiceOutcomesAsync(UiText.Get("Workload_Text_Restart_0_950992", "Restart: {0}", row.Name), result, row.Project);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Restart_failed_b8864c", "Restart failed"), ex.Message);
            StatusMessage = UiText.Get("Workload_Text_Error_54a0e8", "Error");
        }
        finally
        {
            EndBusyOperation();
        }
    }

    /// <summary>Brings the selected project down, removes project-created volumes, and forgets the definition.</summary>
    [RelayCommand]
    private async Task RemoveAsync(ComposeProjectRow? row)
    {
        row ??= Selected;
        if (row is null || IsBusy)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Remove_project_704e73", "Remove project"),
            UiText.Get("Workload_Text_Remove_the_project_definition_for_0_158ca8", "Remove the project definition for \"{0}\"? This also brings it down (stops and removes its containers) and deletes the volumes it created. External volumes are preserved.", row.Name),
            UiText.Get("Workload_Text_Remove_c3812f", "Remove"));
        if (!ok)
        {
            return;
        }

        BeginBusyOperation();
        try
        {
            await _supervisor.DownAsync(row.Name, removeVolumes: true);
            _store.Delete(row.Name);
            _supervisor.CleanStaging(row.Name);
            await RefreshAsync();
            StatusMessage = UiText.Get("Workload_Text_Removed_0_7285c3", "Removed \"{0}\"", row.Name);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Final_7a91db72b7", "Remove failed"), ex.Message);
        }
        finally
        {
            EndBusyOperation();
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
        if (IsBusy)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Restart_WSL_session_574951", "Restart WSL session"),
            UiText.Get("Workload_Text_This_releases_wslc_s_leaked_bind_ddc612", "This releases wslc's leaked bind-mount slots (needed when config/secret mounts start failing after ~15 distinct mounts). It also STOPS ALL running containers. Continue?"),
            UiText.Get("Workload_Text_Restart_6b983a", "Restart"));
        if (!ok)
        {
            return;
        }

        await RestartSessionCoreAsync();
    }

    private async Task RestartSessionCoreAsync()
    {
        BeginBusyOperation();
        StatusMessage = UiText.Get("Workload_Text_Restarting_WSL_session_ab25ce", "Restarting WSL session…");
        try
        {
            var result = await _wslc.RestartSessionAsync();
            await RefreshAsync();
            StatusMessage = result.Success
                ? UiText.Get("Workload_Text_WSL_session_restarted_mount_slots_released_c52c57", "WSL session restarted — mount slots released. Bring your projects up again.")
                : UiText.Get("Workload_Text_Restart_failed_b8864c", "Restart failed");
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(
                    UiText.Get("Workload_Text_Restart_failed_b8864c", "Restart failed"),
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Restart_failed_b8864c", "Restart failed"), ex.Message);
            StatusMessage = UiText.Get("Workload_Text_Error_54a0e8", "Error");
        }
        finally
        {
            EndBusyOperation();
        }
    }
}
