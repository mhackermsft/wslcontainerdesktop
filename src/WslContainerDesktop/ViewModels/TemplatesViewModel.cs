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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>A category section of templates for the grouped gallery.</summary>
public sealed class TemplateGroup : List<StackTemplate>
{
    public TemplateGroup(string category, IEnumerable<StackTemplate> items)
        : base(items)
    {
        Category = category;
    }

    public string Category { get; }
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
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly IRunProfileStore _profiles;
    private readonly ITemplateConfigStore _configs;
    private readonly ComposeViewModel _compose;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Pick a template to get started.";

    public ObservableCollection<TemplateGroup> Groups { get; } = new();

    public TemplatesViewModel(
        ITemplateCatalog catalog,
        IWslcService wslc,
        StatusMonitor monitor,
        DialogService dialogs,
        ISettingsService settings,
        RegistryAuthRefresher authRefresher,
        IRunProfileStore profiles,
        ITemplateConfigStore configs,
        ComposeViewModel compose)
    {
        _catalog = catalog;
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _profiles = profiles;
        _configs = configs;
        _compose = compose;

        foreach (var group in _catalog.Templates.GroupBy(t => t.Category))
        {
            Groups.Add(new TemplateGroup(group.Key, group));
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _monitor.StatusChanged += OnStatusChanged;
        if (_monitor.Latest is not null)
        {
            UpdateDeploymentState(_monitor.Latest);
        }
    }

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

    /// <summary>Recomputes each template's <see cref="StackTemplate.IsDeployed"/> from the live snapshot.</summary>
    private void UpdateDeploymentState(EngineStatusSnapshot snapshot)
    {
        var containers = snapshot.Containers;
        foreach (var group in Groups)
        {
            foreach (var template in group)
            {
                template.IsDeployed = IsTemplateDeployed(template, containers);
            }
        }
    }

    /// <summary>
    /// A compose template is deployed when any container is named <c>{project}_*</c> (the supervisor's
    /// naming); a single-container template is deployed when a container with its configured name exists.
    /// </summary>
    private bool IsTemplateDeployed(StackTemplate template, IReadOnlyList<ContainerInfo> containers)
    {
        if (template.Kind == StackTemplateKind.Compose)
        {
            var (_, name) = ResolveComposeConfig(template);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var prefix = name + "_";
            return containers.Any(c => c.Name.TrimStart('/').StartsWith(prefix, StringComparison.Ordinal));
        }

        var containerName = ResolveContainerOptions(template)?.Name;
        return !string.IsNullOrWhiteSpace(containerName)
            && containers.Any(c => string.Equals(
                c.Name.TrimStart('/'), containerName, StringComparison.OrdinalIgnoreCase));
    }

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
            if (template.Kind == StackTemplateKind.Compose)
            {
                await LaunchComposeAsync(template);
            }
            else
            {
                await LaunchContainerAsync(template);
            }
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

        if (template.Kind == StackTemplateKind.Compose)
        {
            await ConfigureComposeAsync(template);
        }
        else
        {
            await ConfigureContainerAsync(template);
        }
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

        var volumesCheck = new CheckBox
        {
            Content = "Also delete data volumes (permanently deletes stored data)",
            IsChecked = false,
        };
        var dialog = new ContentDialog
        {
            Title = $"Remove {template.Name}?",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = template.Kind == StackTemplateKind.Compose
                            ? "Stops and removes this stack's containers and its network."
                            : "Stops and removes this container.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    volumesCheck,
                },
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var removeVolumes = volumesCheck.IsChecked == true;

        template.IsRemoving = true;
        StatusMessage = $"Removing {template.Name}…";
        try
        {
            if (template.Kind == StackTemplateKind.Compose)
            {
                await RemoveComposeAsync(template, removeVolumes);
            }
            else
            {
                await RemoveContainerAsync(template, removeVolumes);
            }

            var volumeNote = removeVolumes ? " and its data volumes" : string.Empty;
            StatusMessage = $"{template.Name}{volumeNote} removed.";
            _monitor.RequestRefresh();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Remove failed", ex.Message);
            StatusMessage = "Remove failed";
        }
        finally
        {
            template.IsRemoving = false;
        }
    }

    private async Task RemoveComposeAsync(StackTemplate template, bool removeVolumes)
    {
        var (_, name) = ResolveComposeConfig(template);
        if (!string.IsNullOrWhiteSpace(name))
        {
            await _compose.RemoveProjectAsync(name!, removeVolumes);
        }
    }

    private async Task RemoveContainerAsync(StackTemplate template, bool removeVolumes)
    {
        var options = ResolveContainerOptions(template);
        var name = options?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = (await _wslc.ListContainersAsync(all: true))
            .FirstOrDefault(c => string.Equals(
                c.Name.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
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

    private async Task LaunchContainerAsync(StackTemplate template)
    {
        var options = ResolveContainerOptions(template);
        if (options is null)
        {
            return;
        }

        await RunContainerDirectAsync(template, options);
    }

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

    /// <summary>Runs a single-container template with the given options, handling name conflicts.</summary>
    private async Task RunContainerDirectAsync(StackTemplate template, RunContainerOptions options)
    {
        // If a named container from a previous launch already exists, offer to replace it so the
        // relaunch reflects the current configuration (named volumes keep the data).
        if (!string.IsNullOrWhiteSpace(options.Name))
        {
            var existing = (await _wslc.ListContainersAsync(all: true))
                .FirstOrDefault(c => string.Equals(c.Name, options.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var replace = await _dialogs.ShowConfirmAsync(
                    $"{template.Name} already exists",
                    $"A container named \"{options.Name}\" is already present. Replace it with the "
                        + "template's current configuration? Data in named volumes is preserved.",
                    "Replace");
                if (!replace)
                {
                    return;
                }

                var removed = await _wslc.RemoveContainerAsync(existing.Id, force: true);
                if (!removed.Success)
                {
                    await _dialogs.ShowMessageAsync("Couldn't replace container", removed.ErrorText);
                    return;
                }
            }
        }

        IsBusy = true;
        StatusMessage = $"Starting {template.Name}… downloading the image if needed, this can take a moment.";
        try
        {
            // `wslc run` auto-pulls if the image is absent, so refresh Azure auth first.
            await _authRefresher.EnsureFreshForReferenceAsync(options.Image);

            var result = await _wslc.RunContainerAsync(options);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Launch failed", result.ErrorText);
                StatusMessage = "Launch failed";
            }
            else
            {
                var note = string.IsNullOrWhiteSpace(template.Note) ? string.Empty : $" — {template.Note}";
                StatusMessage = $"{template.Name} started{note}. See it in the Containers view.";
                _monitor.RequestRefresh();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Resolves the compose YAML/name to use: the saved config if present, else defaults.</summary>
    private (string Yaml, string? Name) ResolveComposeConfig(StackTemplate template)
    {
        var saved = _configs.Get(template.Id);
        var yaml = !string.IsNullOrWhiteSpace(saved?.ComposeYaml) ? saved!.ComposeYaml! : template.ComposeYaml ?? string.Empty;
        var name = !string.IsNullOrWhiteSpace(saved?.ComposeProjectName) ? saved!.ComposeProjectName : template.ComposeProjectName;
        return (yaml, name);
    }

    private async Task LaunchComposeAsync(StackTemplate template)
    {
        var (yaml, name) = ResolveComposeConfig(template);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Launching {template.Name}… pulling images and starting services, this can take a moment.";
        try
        {
            await _compose.ImportAndUpAsync(yaml, suggestedName: name);
            var note = string.IsNullOrWhiteSpace(template.Note) ? string.Empty : $" — {template.Note}";
            StatusMessage = $"{template.Name} launched{note}. See it in the Containers view.";
            _monitor.RequestRefresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConfigureComposeAsync(StackTemplate template)
    {
        var (yaml, name) = ResolveComposeConfig(template);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return;
        }

        var dialog = new ConfigureComposeDialog(template.Name, name ?? string.Empty, yaml);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(dialog.Yaml))
        {
            return;
        }

        // Remember the edited YAML/name so the next Launch reuses them.
        _configs.Save(new TemplateConfig
        {
            TemplateId = template.Id,
            ComposeYaml = dialog.Yaml,
            ComposeProjectName = string.IsNullOrWhiteSpace(dialog.ProjectName) ? null : dialog.ProjectName,
        });

        IsBusy = true;
        StatusMessage = $"Launching {template.Name}…";
        try
        {
            await _compose.ImportAndUpAsync(
                dialog.Yaml,
                suggestedName: string.IsNullOrWhiteSpace(dialog.ProjectName) ? template.ComposeProjectName : dialog.ProjectName);
            StatusMessage = $"{template.Name} launched";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
