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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>A category section of templates for the grouped gallery.</summary>
public sealed class TemplateGroup : List<StackTemplate>, INotifyPropertyChanged
{
    /// <summary>Creates the TemplateGroup instance and stores the services it needs.</summary>
    public TemplateGroup(string category, IEnumerable<StackTemplate> items)
        : base(items)
    {
        Category = category;
    }

    /// <summary>Value for category shown or edited by the view.</summary>
    public string Category { get; }

    /// <summary>Localizes catalog categories while preserving user-authored category values.</summary>
    public string DisplayCategory => this.FirstOrDefault(template => template.Source == TemplateSource.BuiltIn)?.DisplayCategory ?? Category;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Updates the group label without replacing its stable items.</summary>
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(DisplayCategory)));

    /// <summary>Returns the category so screen readers announce the section name, not the type.</summary>
    public override string ToString() => DisplayCategory;
}

/// <summary>
/// Backs the Templates gallery: a curated catalog of one-click stacks. The Launch button starts a
/// template immediately using the user's saved configuration (or catalog defaults); the Settings
/// button opens a configuration dialog whose choices are persisted (see <see cref="ITemplateConfigStore"/>)
/// and reused by future launches.
/// </summary>
public partial class TemplatesViewModel : ObservableObject
{
    private readonly ITemplateCatalog _catalog;
    private readonly IWslcService _wslc;
    private readonly OpenWebUiPlanner _openWebUi;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly IRunProfileStore _profiles;
    private readonly ITemplateConfigStore _configs;
    private readonly IUserTemplateStore _userTemplates;
    private readonly ITemplateVisibilityStore _visibility;
    private readonly ComposeViewModel _compose;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>When true, hidden templates are shown (dimmed) instead of filtered out of the gallery.</summary>
    [ObservableProperty]
    private bool _showHidden;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    private string _statusMessage = UiText.Get("Workload_Text_Pick_a_template_to_get_started_e1abfd", "Pick a template to get started.");

    /// <summary>Value for groups shown or edited by the view.</summary>
    public ObservableCollection<TemplateGroup> Groups { get; } = new();

    /// <summary>Creates the Templates view model and stores its injected services.</summary>
    public TemplatesViewModel(
        ITemplateCatalog catalog,
        IWslcService wslc,
        StatusMonitor monitor,
        DialogService dialogs,
        ISettingsService settings,
        RegistryAuthRefresher authRefresher,
        IRunProfileStore profiles,
        ITemplateConfigStore configs,
        IUserTemplateStore userTemplates,
        ITemplateVisibilityStore visibility,
        ComposeViewModel compose,
        IWslcCapabilitiesService capabilities)
    {
        _catalog = catalog;
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _profiles = profiles;
        _configs = configs;
        _userTemplates = userTemplates;
        _visibility = visibility;
        _compose = compose;
        _openWebUi = new OpenWebUiPlanner(wslc);

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        RebuildGroups();

        _monitor.StatusChanged += OnStatusChanged;
        _catalog.Changed += OnCatalogOrVisibilityChanged;
        _visibility.Changed += OnCatalogOrVisibilityChanged;
    }

    /// <summary>Refreshes labels from the catalog while preserving custom template contents.</summary>
    public void RefreshLanguage()
    {
        StatusMessage = UiText.Translate(StatusMessage);
        foreach (var template in _catalog.Templates) template.RefreshLanguage();
        foreach (var group in Groups) group.RefreshLanguage();
    }

    /// <summary>Handles catalog or visibility changed changes and updates related view-model state.</summary>
    private void OnCatalogOrVisibilityChanged(object? sender, EventArgs e) => RunOnUi(RebuildGroups);

    /// <summary>Handles show hidden changed changes and updates related view-model state.</summary>
    partial void OnShowHiddenChanged(bool value) => RebuildGroups();

    /// <summary>
    /// Rebuilds the grouped gallery from the composite catalog: stamps each template's hidden state,
    /// applies the "Show hidden" filter, groups by category, and re-applies live deployment state.
    /// The catalog returns stable instances, so transient card state survives a rebuild.
    /// </summary>
    private void RebuildGroups()
    {
        var all = _catalog.Templates;
        foreach (var template in all)
        {
            template.IsHidden = _visibility.IsHidden(template.Id);
        }

        var visible = all.Where(t => ShowHidden || !t.IsHidden);

        Groups.Clear();
        foreach (var group in visible.GroupBy(t => t.Category))
        {
            Groups.Add(new TemplateGroup(group.Key, group));
        }

        if (_monitor.Latest is not null)
        {
            UpdateDeploymentState(_monitor.Latest);
        }
    }

    /// <summary>Runs an action on the UI thread, marshaling via the captured dispatcher if needed.</summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    /// <summary>Handles status changed changes and updates related view-model state.</summary>
    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            UpdateDeploymentState(e);
        }
        else
        {
            _dispatcher.TryEnqueue(() => UpdateDeploymentState(e));
        }
    }

    /// <summary>Recomputes each template's live deployment state from the snapshot.</summary>
    private void UpdateDeploymentState(EngineStatusSnapshot snapshot)
    {
        var containers = snapshot.Containers;
        foreach (var group in Groups)
        {
            foreach (var template in group)
            {
                var deployments = FindDeployments(template, containers);
                template.DeploymentCount = deployments.Count;
                template.IsDeployed = deployments.Count > 0;
            }
        }
    }

    /// <summary>
    /// One deployment of a template. A template can be launched repeatedly — deployments step aside
    /// onto <c>name-2</c>, <c>name-3</c> rather than disturbing what is already running — so the
    /// gallery has to talk about a specific one rather than "the" deployment.
    /// </summary>
    /// <param name="Label">What the user sees, e.g. "sqlserver-2".</param>
    /// <param name="Target">Container name, or Compose project name.</param>
    public sealed record TemplateDeployment(string Label, string Target, bool IsCompose);

    /// <summary>
    /// Finds every deployment of a template: the configured name plus the <c>-N</c> variants the
    /// conflict resolver assigns when the original name is taken.
    /// </summary>
    /// <summary>
    /// Project names that actually own containers: the base name and any of its numbered repeats
    /// that own at least one <c>{project}_</c> container. Derived by testing candidates against the
    /// inventory rather than splitting container names, which mis-attributes services whose own
    /// name contains an underscore.
    /// </summary>
    private static IEnumerable<string> CandidateProjectNames(string baseName, IReadOnlyList<string> containerNames)
    {
        for (var i = 1; i < 200; i++)
        {
            var candidate = i == 1 ? baseName : $"{baseName}-{i}";
            if (containerNames.Any(n => n.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase)))
                yield return candidate;
        }
    }

    /// <summary>True for the base name itself or a <c>base-N</c> repeat deployment.</summary>
    private List<TemplateDeployment> FindDeployments(StackTemplate template, IReadOnlyList<ContainerInfo> containers)
    {
        var found = new List<TemplateDeployment>();
        if (template.Kind == StackTemplateKind.Compose)
        {
            var (_, project) = ResolveComposeConfig(template);
            if (string.IsNullOrWhiteSpace(project))
                return found;

            // Compose names containers "{project}_{service}"; a repeat launch uses "{project}-2".
            // Match candidate project names against the prefix rather than splitting the container
            // name: a service whose own name contains "_" would otherwise be attributed to the
            // wrong project and its deployment would never appear on the card.
            var names = containers.Select(c => c.Name.TrimStart('/')).ToArray();
            foreach (var candidate in CandidateProjectNames(project!, names))
            {
                if (!found.Any(d => d.Target == candidate))
                    found.Add(new(candidate, candidate, IsCompose: true));
            }
        }
        else
        {
            var configured = ResolveContainerOptions(template)?.Name;
            if (string.IsNullOrWhiteSpace(configured))
                return found;

            foreach (var name in containers.Select(c => c.Name.TrimStart('/')))
            {
                if (TemplateDeploymentNaming.IsBaseOrSuffixed(name, configured) && !found.Any(d => d.Target == name))
                    found.Add(new(name, name, IsCompose: false));
            }
        }

        // Base name first, then numerically, so "name-10" does not sort above "name-2".
        return [.. found.OrderBy(d => SuffixOf(d.Target, template))];
    }

    /// <summary>Helper for the suffix of workflow in this view model.</summary>
    private int SuffixOf(string target, StackTemplate template)
    {
        var baseName = template.Kind == StackTemplateKind.Compose
            ? ResolveComposeConfig(template).Name
            : ResolveContainerOptions(template)?.Name;
        return TemplateDeploymentNaming.SuffixOf(target, baseName);
    }

    /// <summary>
    /// A compose template is deployed when any container is named <c>{project}_*</c> (the supervisor's
    /// naming); a single-container template is deployed when a container with its configured name exists.
    /// Repeat launches add <c>-N</c> variants, which count too.
    /// </summary>
    private bool IsTemplateDeployed(StackTemplate template, IReadOnlyList<ContainerInfo> containers) =>
        FindDeployments(template, containers).Count > 0;

    /// <summary>
    /// One-click launch: starts the template immediately using the user's saved configuration (from
    /// the Settings button) if present, otherwise the catalog defaults. No dialog is shown.
    /// </summary>
    [RelayCommand]
    private async Task LaunchAsync(StackTemplate? template)
    {
        if (template is null || template.IsLaunching)
        {
            return;
        }

        template.IsLaunching = true;
        try
        {
            await ContainerInventoryOperation.RunAsync(async () =>
            {
                if (template.Kind == StackTemplateKind.Compose)
                {
                    await LaunchComposeAsync(template);
                }
                else
                {
                    await LaunchContainerAsync(template);
                }
            }, ReportLaunchFailureAsync);
        }
        finally
        {
            template.IsLaunching = false;
        }
    }

    /// <summary>
    /// Opens the configuration UI for a template. Saving both persists the config (so future Launch
    /// reuses it) and starts the template with the new configuration.
    /// </summary>
    [RelayCommand]
    private async Task ConfigureAsync(StackTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        await ContainerInventoryOperation.RunAsync(async () =>
        {
            if (template.Kind == StackTemplateKind.Compose)
            {
                await ConfigureComposeAsync(template);
            }
            else
            {
                await ConfigureContainerAsync(template);
            }
        }, ReportLaunchFailureAsync);
    }

    /// <summary>Helper for the report launch failure workflow in this view model.</summary>
    private async Task ReportLaunchFailureAsync(Exception error)
    {
        StatusMessage = UiText.Get("Workload_Text_Launch_failed_1755d5", "Launch failed");
        await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Launch_failed_1755d5", "Launch failed"),
            UiText.Get("Workload_Text_The_launch_could_not_complete_Check_6eee79", "The launch could not complete. Check the container engine and retry.\n\n{0}", error.Message));
    }

    /// <summary>
    /// One-click teardown for a deployed template: removes its container(s) and network(s), and —
    /// if the user opts in via the confirmation checkbox — its data volumes, returning the system to
    /// its pre-launch state.
    /// </summary>
    [RelayCommand]
    private async Task RemoveAsync(StackTemplate? template)
    {
        if (template is null || template.IsBusyCard || !template.IsDeployed)
        {
            return;
        }

        var deployments = FindDeployments(template, _monitor.Latest?.Containers ?? []);
        if (deployments.Count == 0)
        {
            return;
        }

        // With several deployments the old dialog silently removed whichever matched the configured
        // name, leaving the rest with no way to reach them. Make the target an explicit choice.
        TemplateDeployment target;
        if (deployments.Count == 1)
        {
            target = deployments[0];
        }
        else
        {
            var chooser = new ComboBox
            {
                Header = UiText.Get("Workload_Text_Which_deployment_44c49c", "Which deployment?"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 320,
                ItemsSource = deployments.Select(d => d.Label).ToList(),
                SelectedIndex = 0,
            };
            var pick = new ContentDialog
            {
                Title = UiText.Get("Workload_Text_Remove_a_0_deployment_01fe9f", "Remove a {0} deployment", template.DisplayName),
                Content = new StackPanel
                {
                    Spacing = 12,
                    Width = 420,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = UiText.Get("Workload_Text_There_are_0_separate_1_deployments_1f4289", "There are {0} separate {1} deployments. Choose the one to remove; the others are left running.", deployments.Count, template.DisplayName),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        chooser,
                    },
                },
                PrimaryButtonText = UiText.Get("Workload_Text_Continue_31fbef", "Continue"),
                CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await _dialogs.ShowDialogAsync(pick) != ContentDialogResult.Primary)
            {
                return;
            }

            target = deployments[Math.Max(0, chooser.SelectedIndex)];
        }

        var volumesCheck = new CheckBox
        {
            Content = UiText.Get("Workload_Text_Also_delete_data_volumes_permanently_deletes_c126ec", "Also delete data volumes (permanently deletes stored data)"),
            IsChecked = false,
        };
        var dialog = new ContentDialog
        {
            Title = UiText.Get("Workload_Text_Remove_0_9d64ce", "Remove {0}?", target.Label),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = template.Kind == StackTemplateKind.Compose
                            ? UiText.Get("Workload_Text_Stops_and_removes_the_0_stack_9276d2", "Stops and removes the \"{0}\" stack's containers and its network.", target.Label)
                            : UiText.Get("Workload_Text_Stops_and_removes_the_0_container_f4fce7", "Stops and removes the \"{0}\" container.", target.Label),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    volumesCheck,
                },
            },
            PrimaryButtonText = UiText.Get("Workload_Text_Remove_c3812f", "Remove"),
            CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var removeVolumes = volumesCheck.IsChecked == true;

        template.IsRemoving = true;
        StatusMessage = UiText.Get("Workload_Text_Removing_0_8e3a5e", "Removing {0}…", template.DisplayName);
        try
        {
            if (template.Kind == StackTemplateKind.Compose)
            {
                await RemoveComposeAsync(target, removeVolumes);
            }
            else
            {
                await RemoveContainerAsync(target, removeVolumes);
            }

            var volumeNote = removeVolumes ? UiText.Get("Workload_Text_and_its_data_volumes_fee57a", " and its data volumes") : string.Empty;
            StatusMessage = UiText.Get("Workload_Text_0_1_removed_ae80f8", "{0}{1} removed.", target.Label, volumeNote);
            _monitor.RequestRefresh();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Final_7a91db72b7", "Remove failed"), ex.Message);
            StatusMessage = UiText.Get("Workload_Final_7a91db72b7", "Remove failed");
        }
        finally
        {
            template.IsRemoving = false;
        }
    }

    /// <summary>
    /// Hides or unhides a template in the gallery. Applies to any template (built-in or user); a
    /// hidden template is filtered out unless the "Show hidden" toggle is on.
    /// </summary>
    [RelayCommand]
    private void ToggleHidden(StackTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        _visibility.SetHidden(template.Id, !template.IsHidden);
    }

    /// <summary>
    /// Permanently deletes a user-authored or imported template (never a built-in) after
    /// confirmation, also dropping its saved launch config and hidden state. Deployed resources are
    /// left untouched — this removes the template definition, not any running instance.
    /// </summary>
    [RelayCommand]
    private async Task DeleteTemplateAsync(StackTemplate? template)
    {
        if (template is null || !template.IsUserManaged)
        {
            return;
        }

        var confirmed = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Delete_the_0_template_fb09f8", "Delete the \"{0}\" template?", template.DisplayName),
            UiText.Get("Workload_Text_This_removes_the_template_from_your_44188d", "This removes the template from your gallery. Any containers it already launched are not affected — use \"Remove deployment\" first if you also want to tear those down."),
            UiText.Get("Workload_Text_Delete_e2d0a5", "Delete"));
        if (!confirmed)
        {
            return;
        }

        _userTemplates.Delete(template.Id);
        _configs.Delete(template.Id);
        _visibility.SetHidden(template.Id, false);
        StatusMessage = UiText.Get("Workload_Text_Deleted_the_0_template_cdabb5", "Deleted the \"{0}\" template.", template.DisplayName);
    }

    /// <summary>Opens the editor to author a brand-new user template, saving it on confirm.</summary>
    [RelayCommand]
    private Task CreateTemplateAsync() => ShowEditorAsync(source: null, isDuplicate: false);

    /// <summary>Opens the editor to edit an existing user/imported template in place.</summary>
    [RelayCommand]
    private Task EditTemplateAsync(StackTemplate? template)
    {
        if (template is null || !template.IsUserManaged)
        {
            return Task.CompletedTask;
        }

        return ShowEditorAsync(template, isDuplicate: false);
    }

    /// <summary>Opens the editor prefilled from any template (including a built-in) to create a copy.</summary>
    [RelayCommand]
    private Task DuplicateTemplateAsync(StackTemplate? template)
    {
        if (template is null)
        {
            return Task.CompletedTask;
        }

        return ShowEditorAsync(template, isDuplicate: true);
    }

    /// <summary>Helper for the show editor workflow in this view model.</summary>
    private async Task ShowEditorAsync(StackTemplate? source, bool isDuplicate)
    {
        var categories = _catalog.Templates.Select(t => t.Category);
        var dialog = new TemplateEditorDialog(categories, source, isDuplicate);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Result is null)
        {
            return;
        }

        var template = dialog.Result;
        // A fresh definition supersedes any stale saved launch override for this id.
        _configs.Delete(template.Id);
        _userTemplates.Save(template);

        var verb = source is null ? UiText.Get("Workload_Text_Created_d70b9e", "Created") : (isDuplicate ? UiText.Get("Workload_Text_Created_a_copy_e721ce", "Created a copy") : UiText.Get("Workload_Text_Saved_b5c120", "Saved"));
        StatusMessage = $"{verb}: \"{template.Name}\".";
    }

    /// <summary>True when the user has at least one custom/imported template that could be exported.</summary>
    public bool HasUserManagedTemplates => _userTemplates.Templates.Count > 0;

    /// <summary>Serializes a single template to export-file JSON (see <see cref="TemplatePortability"/>).</summary>
    public string ExportToJson(StackTemplate template) => TemplatePortability.Serialize(new[] { template });

    /// <summary>Serializes all of the user's custom/imported templates to export-file JSON.</summary>
    public string ExportAllUserToJson() => TemplatePortability.Serialize(_userTemplates.Templates);

    /// <summary>
    /// Guards the "Export all" action: returns true when there is something to export, otherwise
    /// tells the user there are no custom templates yet and returns false.
    /// </summary>
    public async Task<bool> EnsureHasUserTemplatesForExportAsync()
    {
        if (HasUserManagedTemplates)
        {
            return true;
        }

        await _dialogs.ShowMessageAsync(
            UiText.Get("Workload_Text_Nothing_to_export_fd3032", "Nothing to export"),
            UiText.Get("Workload_Text_You_don_t_have_any_custom_1b1898", "You don't have any custom or imported templates yet. Create or import one first, or use a card's Export… to share a built-in template."));
        return false;
    }

    /// <summary>
    /// Imports templates from an export file's JSON. Parsing errors are surfaced to the user; on
    /// success the user confirms, then each template is added non-destructively as an
    /// <see cref="TemplateSource.Imported"/> copy — a fresh unique id and a de-duplicated name are
    /// assigned so nothing existing (built-in or user) is ever overwritten.
    /// </summary>
    public async Task ImportFromJsonAsync(string json)
    {
        IReadOnlyList<StackTemplate> parsed;
        try
        {
            parsed = TemplatePortability.Parse(json);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Import_failed_0a26f4", "Import failed"), ex.Message);
            return;
        }

        var confirmed = await _dialogs.ShowConfirmAsync(
            UiText.Get("Workload_Text_Import_templates_91deef", "Import templates?"),
            UiText.Get("Workload_Text_Import_0_template_s_into_your_67ba38", "Import {0} template(s) into your gallery? Anything that clashes with an existing template is imported as a separate copy — nothing is overwritten.", parsed.Count),
            UiText.Get("Workload_Text_Import_2cff9b", "Import"));
        if (!confirmed)
        {
            return;
        }

        var ids = new HashSet<string>(
            _catalog.Templates.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(
            _catalog.Templates.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var toSave = new List<StackTemplate>();
        foreach (var template in parsed)
        {
            template.Source = TemplateSource.Imported;
            template.IsHidden = false;

            if (string.IsNullOrWhiteSpace(template.Id) || ids.Contains(template.Id))
            {
                template.Id = MakeUniqueId(ids);
            }

            ids.Add(template.Id);

            if (names.Contains(template.Name))
            {
                template.Name = MakeUniqueName(template.Name, names);
            }

            names.Add(template.Name);
            toSave.Add(template);
        }

        _userTemplates.SaveRange(toSave);
        StatusMessage = UiText.Get("Workload_Text_Imported_0_template_s_6e2106", "Imported {0} template(s).", toSave.Count);
    }

    /// <summary>Helper for the make unique id workflow in this view model.</summary>
    private static string MakeUniqueId(HashSet<string> existing)
    {
        string id;
        do
        {
            id = "imported-" + Guid.NewGuid().ToString("N")[..8];
        }
        while (existing.Contains(id));

        return id;
    }

    /// <summary>Helper for the make unique name workflow in this view model.</summary>
    private static string MakeUniqueName(string name, HashSet<string> existing)
    {
        var candidate = UiText.Get("Workload_Text_0_imported_d226e7", "{0} (imported)", name);
        var counter = 2;
        while (existing.Contains(candidate))
        {
            candidate = UiText.Get("Workload_Text_0_imported_1_a03a98", "{0} (imported {1})", name, counter++);
        }

        return candidate;
    }

    /// <summary>
    /// Reads the named volumes actually mounted by the container being removed, so a repeat
    /// deployment deletes its own data rather than the template's configured volume names, which
    /// may belong to a different deployment that is still running.
    /// </summary>
    private async Task<RunContainerOptions?> ResolveDeployedVolumesAsync(string containerId)
    {
        try
        {
            var inspect = await _wslc.InspectContainerAsync(containerId);
            if (!inspect.Success)
                return null;
            var mounts = ContainerMounts.Parse(inspect.StandardOutput);
            if (!mounts.IsComplete)
                return null;
            var options = new RunContainerOptions { Image = string.Empty };
            foreach (var item in mounts.Items.Where(m => !string.IsNullOrWhiteSpace(m.VolumeName)))
                options.Volumes.Add($"{item.VolumeName}:{item.Destination}");
            return options;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown mounts mean we cannot prove which volumes are this deployment's; keep them.
            return null;
        }
    }

    /// <summary>Helper for the remove compose workflow in this view model.</summary>
    private Task RemoveComposeAsync(TemplateDeployment deployment, bool removeVolumes) =>
        _compose.RemoveProjectAsync(deployment.Target, removeVolumes);

    /// <summary>Helper for the remove container workflow in this view model.</summary>
    private async Task RemoveContainerAsync(TemplateDeployment deployment, bool removeVolumes)
    {
        var name = deployment.Target;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = (await _wslc.ListContainersAsync(all: true))
            .FirstOrDefault(c => string.Equals(
                c.Name.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));
        RunContainerOptions? options = null;
        if (existing is not null)
        {
            // Read the mounts off the container actually being removed: a repeat deployment has its
            // own "-N" volumes, and deleting the template's configured ones would destroy the data
            // belonging to a different deployment that is still running.
            options = await ResolveDeployedVolumesAsync(existing.Id);
            await _wslc.RemoveContainerAsync(existing.Id, force: true);
        }

        if (removeVolumes && options is not null)
        {
            foreach (var volumeName in NamedVolumeSources(options.Volumes))
            {
                try
                {
                    await _wslc.RemoveVolumeAsync(volumeName);
                }
                catch
                {
                    // Best-effort: a volume still in use or already gone must not fail the removal.
                }
            }
        }
    }

    /// <summary>
    /// Extracts named-volume sources from <c>source:target[:mode]</c> mount specs. Bind mounts (the
    /// source is a path) and drive letters are skipped so only managed named volumes are removed.
    /// </summary>
    private static IEnumerable<string> NamedVolumeSources(IEnumerable<string> volumes)
    {
        foreach (var spec in volumes)
        {
            var colon = spec.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var source = spec[..colon];
            if (source.Length <= 1 || source.Contains('/') || source.Contains('\\'))
            {
                continue;
            }

            yield return source;
        }
    }

    /// <summary>Resolves the run options to use: the saved config if present, else catalog defaults.</summary>
    private RunContainerOptions? ResolveContainerOptions(StackTemplate template)
    {
        var saved = _configs.Get(template.Id)?.RunOptions;
        return (saved ?? template.RunOptions)?.Clone();
    }

    /// <summary>Helper for the launch container workflow in this view model.</summary>
    private async Task LaunchContainerAsync(StackTemplate template)
    {
        var options = ResolveContainerOptions(template);
        if (options is null)
        {
            return;
        }

        await RunContainerDirectAsync(template, options);
    }

    /// <summary>Helper for the configure container workflow in this view model.</summary>
    private async Task ConfigureContainerAsync(StackTemplate template)
    {
        var options = ResolveContainerOptions(template);
        if (options is null)
        {
            return;
        }

        var dialog = new RunContainerDialog(
            _wslc,
            _settings.Registries,
            _profiles,
            prefillImage: options.Image,
            prefillOptions: options);

        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Options is null)
        {
            return;
        }

        // Remember these choices so the next Launch reuses them.
        _configs.Save(new TemplateConfig
        {
            TemplateId = template.Id,
            RunOptions = dialog.Options.Clone(),
        });

        await RunContainerDirectAsync(template, dialog.Options);
    }

    /// <summary>Runs a single-container template, stepping around anything already deployed.</summary>
    private async Task RunContainerDirectAsync(StackTemplate template, RunContainerOptions options)
    {
        // A repeat launch used to offer to replace the existing container, which removed a running
        // deployment to make room. Deploying something new must never cost the user what is already
        // running, so the repeat moves onto a free name, free ports and its own named volumes.
        var adjustment = await new DeploymentConflictResolver(_wslc).ResolveAsync(options);
        if (adjustment.Adjusted)
        {
            var again = await _dialogs.ShowConfirmAsync(
                UiText.Get("Workload_Text_0_is_already_deployed_314a85", "{0} is already deployed", template.DisplayName),
                UiText.Get("Workload_Text_Launch_another_one_It_runs_alongside_a9e08c", "Launch another one? It runs alongside the existing deployment as \"{0}\", with its own ports and its own data volumes, so neither can affect the other's data.\n\n{1}\n\nTo replace the existing deployment instead, remove it first.", options.Name, adjustment.Summary),
                UiText.Get("Workload_Text_Launch_another_c189f3", "Launch another"));
            if (!again)
            {
                StatusMessage = UiText.Get("Workload_Text_Launch_cancelled_c36806", "Launch cancelled.");
                return;
            }
        }

        IsBusy = true;
        StatusMessage = UiText.Get("Workload_Text_Starting_0_downloading_the_image_if_1a0af8", "Starting {0}… downloading the image if needed, this can take a moment.", template.DisplayName);
        try
        {
            // `wslc run` auto-pulls if the image is absent, so refresh Azure auth first.
            await _authRefresher.EnsureFreshForReferenceAsync(options.Image);

            var result = await _wslc.RunContainerAsync(options);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Workload_Text_Launch_failed_1755d5", "Launch failed"), result.ErrorText);
                StatusMessage = UiText.Get("Workload_Text_Launch_failed_1755d5", "Launch failed");
            }
            else
            {
                var note = NoteClause(template);
                // Name the deployment that actually started; "MySQL started" would be ambiguous once
                // more than one exists.
                var started = adjustment.Adjusted ? UiText.Get("Workload_Text_0_started_as_1_d8356c", "{0} started as \"{1}\"", template.DisplayName, options.Name) : UiText.Get("Workload_Text_0_started_43003e", "{0} started", template.DisplayName);
                StatusMessage = UiText.Get("Workload_Text_0_1_See_it_in_the_60c2ff", "{0}{1}. See it in the Containers view.", started, note);
                _monitor.RequestRefresh();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The template's note as a clause that can be composed into a longer sentence. Notes are
    /// authored as sentences and usually end in a period, which produced "…password "mysql".. See it
    /// in the Containers view." once a status line continued after one.
    /// </summary>
    private static string NoteClause(StackTemplate template) =>
        string.IsNullOrWhiteSpace(template.DisplayNote) ? string.Empty : $" — {template.DisplayNote.TrimEnd('.', ' ')}";

    /// <summary>Resolves the compose YAML/name to use: the saved config if present, else defaults.</summary>
    private (string Yaml, string? Name) ResolveComposeConfig(StackTemplate template)
    {
        var saved = _configs.Get(template.Id);
        var yaml = !string.IsNullOrWhiteSpace(saved?.ComposeYaml) ? saved!.ComposeYaml! : template.ComposeYaml ?? string.Empty;
        var name = !string.IsNullOrWhiteSpace(saved?.ComposeProjectName) ? saved!.ComposeProjectName : template.ComposeProjectName;
        return (yaml, name);
    }

    /// <summary>Notes GPU passthrough when the deployed runtime asked for it. The container's GPU
    /// badge confirms the access itself; this only reports what was requested.</summary>
    private static string Acceleration(OpenWebUiPlanner.Plan? plan) =>
        plan is { UsesGpu: true } ? UiText.Get("Workload_Text_using_your_GPU_4b145b", " using your GPU") : string.Empty;

    /// <summary>Helper for the launch compose workflow in this view model.</summary>
    private async Task LaunchComposeAsync(StackTemplate template)
    {
        var (yaml, name) = ResolveComposeConfig(template);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return;
        }

        // Deploying a second Ollama would duplicate multi-GB models, so reuse an existing runtime
        // when one is present. Only applies to the built-in template with its default YAML: an
        // edited or user-saved configuration is deployed exactly as written.
        OpenWebUiPlanner.Plan? plan = null;
        var model = string.Empty;
        if (template.Id == OpenWebUiPlanner.TemplateId
            && template.Source == TemplateSource.BuiltIn
            && yaml == OpenWebUiPlanner.BundledYaml)
        {
            plan = await _openWebUi.PlanAsync();
            yaml = plan.Yaml;
            if (!plan.ReusesExistingRuntime)
            {
                // A fresh Ollama has no models, so the chat UI would open with nothing usable.
                // Ask before deploying rather than leaving setup half-finished.
                var chooser = new OllamaModelDialog();
                var choice = await _dialogs.ShowDialogAsync(chooser);
                if (choice == ContentDialogResult.None)
                {
                    StatusMessage = UiText.Get("Workload_Text_Launch_cancelled_c36806", "Launch cancelled.");
                    return;
                }
                model = choice == ContentDialogResult.Primary ? chooser.Model : string.Empty;
            }
        }

        IsBusy = true;
        StatusMessage = UiText.Get("Workload_Text_Reviewing_0_no_images_or_services_2159fa", "Reviewing {0}… no images or services are changed before confirmation.", template.DisplayName);
        try
        {
            if (!await _compose.ImportAndUpAsync(yaml, suggestedName: name))
            {
                StatusMessage = UiText.Get("Workload_Text_Compose_launch_was_cancelled_blocked_or_41a796", "Compose launch was cancelled, blocked, or incomplete. Check Compose details and refresh actual state before retrying.");
                return;
            }
            var note = NoteClause(template);
            if (plan is { ReusesExistingRuntime: true })
            {
                // The web UI resolves Ollama by alias, so the reused runtime joins this project's
                // network after it exists. Report an attach failure instead of leaving a UI that
                // silently lists no models.
                var attach = await _openWebUi.AttachExistingAsync(plan.ExistingOllamaId!);
                StatusMessage = attach.Success
                    ? UiText.Get("Workload_Text_0_launched_using_the_existing_1_ee72be", "{0} launched using the existing \"{1}\" runtime{2}.", template.DisplayName, plan.ExistingOllamaName, note)
                    : UiText.Get("Workload_Text_0_started_but_1_could_not_6165d9", "{0} started, but \"{1}\" could not join the {2} network, so no models will appear. Connect it from the Networks page.", template.DisplayName, plan.ExistingOllamaName, OpenWebUiPlanner.NetworkName);
            }
            else if (!string.IsNullOrEmpty(model))
            {
                StatusMessage = UiText.Get("Workload_Text_Downloading_0_this_can_take_several_8294ec", "Downloading {0}… this can take several minutes.", model);
                var pull = await _openWebUi.InstallModelAsync(model);
                StatusMessage = pull.Success
                    ? UiText.Get("Workload_Text_0_is_ready_with_1_2_12e6ac", "{0} is ready with {1}{2}{3}.", template.DisplayName, model, Acceleration(plan), note)
                    : UiText.Get("Workload_Text_0_started_but_1_could_not_c04bc8", "{0} started, but {1} could not be downloaded. Open the web UI and pull a model there, or retry from the container's terminal.", template.DisplayName, model);
            }
            else
            {
                StatusMessage = plan is null
                    ? UiText.Get("Workload_Text_0_launched_1_See_it_in_2da307", "{0} launched{1}. See it in the Containers view.", template.DisplayName, note)
                    : UiText.Get("Workload_Text_0_launched_without_a_model_1_c556f5", "{0} launched without a model{1}{2}. Add one from the web UI before chatting.", template.DisplayName, Acceleration(plan), note);
            }
            _monitor.RequestRefresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Helper for the configure compose workflow in this view model.</summary>
    private async Task ConfigureComposeAsync(StackTemplate template)
    {
        var (yaml, name) = ResolveComposeConfig(template);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return;
        }

        var dialog = new ConfigureComposeDialog(template.DisplayName, name ?? string.Empty, yaml);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = UiText.Get("Workload_Text_Launching_0_3dad1d", "Launching {0}…", template.DisplayName);
        try
        {
            if (!await _compose.ImportAndUpAsync(
                dialog.Yaml,
                suggestedName: string.IsNullOrWhiteSpace(dialog.ProjectName) ? template.ComposeProjectName : dialog.ProjectName))
            {
                StatusMessage = UiText.Get("Workload_Text_Compose_launch_was_cancelled_blocked_or_e9222d", "Compose launch was cancelled, blocked, or incomplete. Saved template defaults were not changed; check Compose details.");
                return;
            }
            // Preserve template defaults unless the reviewed deployment completed successfully.
            _configs.Save(new TemplateConfig
            {
                TemplateId = template.Id,
                ComposeYaml = dialog.Yaml,
                ComposeProjectName = string.IsNullOrWhiteSpace(dialog.ProjectName) ? null : dialog.ProjectName,
            });
            StatusMessage = UiText.Get("Workload_Text_0_launched_680a32", "{0} launched", template.DisplayName);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
