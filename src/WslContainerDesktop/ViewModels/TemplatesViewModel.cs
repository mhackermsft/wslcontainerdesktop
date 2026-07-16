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
