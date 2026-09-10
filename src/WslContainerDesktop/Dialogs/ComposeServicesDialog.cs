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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>Selects explicit service targets without interpreting no selection as the whole project.</summary>
public sealed class ComposeServicesDialog : ContentDialog
{
    private readonly ListView _services;
    private readonly ComboBox _operation;
    private readonly CheckBox _rebuild;
    private readonly TextBlock _description;
    private readonly InfoBar _validation;
    private readonly ComposeProject _project;
    private readonly StackPanel _replicas;
    private readonly CheckBox _saveReplicas;
    private readonly Dictionary<string, NumberBox> _replicaInputs = new(StringComparer.Ordinal);

    public bool SaveReplicaOverrides => Operation == ComposeLifecycleOperation.Up && _saveReplicas.IsChecked == true;

    private ComposeLifecycleOperation Operation => _operation.SelectedIndex switch
    {
        1 => ComposeLifecycleOperation.Restart,
        2 => ComposeLifecycleOperation.Stop,
        3 => ComposeLifecycleOperation.Down,
        _ => ComposeLifecycleOperation.Up,
    };

    public string OperationLabel => Operation switch
    {
        ComposeLifecycleOperation.Restart => "Restart",
        ComposeLifecycleOperation.Stop => "Stop",
        ComposeLifecycleOperation.Down => "Remove containers",
        _ => "Apply (up)",
    };

    public ComposeOperationRequest Request => new()
    {
        Operation = Operation,
        Services = _services.SelectedItems.Cast<string>().ToArray(),
        Build = Operation == ComposeLifecycleOperation.Up && _rebuild.IsChecked == true,
        ForceRecreate = false,
        Replicas = Operation == ComposeLifecycleOperation.Up
            ? _replicaInputs.ToDictionary(p => p.Key, p => checked((int)p.Value.Value), StringComparer.Ordinal)
            : new Dictionary<string, int>(),
    };

    public ComposeServicesDialog(ComposeProject project)
    {
        _project = project;
        Title = $"Manage services: {project.Name}";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 560.0;
        AutomationProperties.SetAutomationId(this, "ComposeServicesDialog");

        _services = new ListView
        {
            ItemsSource = project.Services.Select(service => service.Name).ToArray(),
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 240,
        };
        AutomationProperties.SetAutomationId(_services, "ComposeServiceTargets");
        AutomationProperties.SetName(_services, "Services to manage");

        _operation = new ComboBox
        {
            Header = "Operation",
            ItemsSource = new[] { "Apply (up)", "Restart", "Stop", "Remove containers" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_operation, "ComposeServiceOperation");

        _rebuild = new CheckBox { Content = "Rebuild images when applying", IsChecked = false };
        AutomationProperties.SetAutomationId(_rebuild, "ComposeServiceRebuild");
        _replicas = new StackPanel { Spacing = 8 };
        _saveReplicas = new CheckBox { Content = "Save these replica counts for future applies", IsChecked = true };
        AutomationProperties.SetAutomationId(_saveReplicas, "ComposeSaveReplicaOverrides");
        _description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_description, "ComposeServiceScope");
        _validation = new InfoBar
        {
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Message = "Select at least one service before continuing.",
        };
        AutomationProperties.SetAutomationId(_validation, "ComposeServiceValidation");

        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Select one or more services.", TextWrapping = TextWrapping.Wrap },
                _services,
                _operation,
                _description,
                new ScrollViewer { Content = _replicas, MaxHeight = 180 },
                _saveReplicas,
                _rebuild,
                _validation,
            },
        };

        UpdateOperation();
        _operation.SelectionChanged += OnOperationChanged;
        _services.SelectionChanged += OnServicesChanged;
        PrimaryButtonClick += OnPrimary;
    }

    private void UpdateOperation()
    {
        PrimaryButtonText = OperationLabel;
        _rebuild.IsEnabled = Operation == ComposeLifecycleOperation.Up;
        _replicas.Visibility = Operation == ComposeLifecycleOperation.Up ? Visibility.Visible : Visibility.Collapsed;
        _saveReplicas.Visibility = _replicas.Visibility;
        if (!_rebuild.IsEnabled)
        {
            _rebuild.IsChecked = false;
        }

        _description.Text = Operation switch
        {
            ComposeLifecycleOperation.Restart =>
                "Stop and start only existing selected containers, plus dependents with restart: true. " +
                "Does not create or recreate containers, build images, or apply configuration changes.",
            ComposeLifecycleOperation.Stop =>
                "Stop only the selected services. Containers, shared networks, and volumes are preserved. " +
                "Other services are not stopped and may lose access to these services.",
            ComposeLifecycleOperation.Down =>
                "Stop and remove only the selected services' containers. Shared networks, volumes, and the " +
                "project definition are preserved. Other services are not removed and may lose access to these services.",
            _ =>
                "Apply configuration to the selected services and their required dependency closure. " +
                "Unchanged running containers are kept; stopped containers are started and changed containers " +
                "may be recreated. Replicas are local instances, not a Swarm deployment. Zero removes all selected " +
                "instances. Named volumes and bind mounts are shared; anonymous volumes belong to each instance " +
                "and are preserved on recreation. Scaling down does not delete volumes. " +
                $"A local operation plans at most {ComposeReconciliationPlanner.MaximumPlanInstances} total instances.",
        };
    }

    // Synchronous framework handlers perform only local control updates; no async-void work.
    private void OnOperationChanged(object sender, SelectionChangedEventArgs args) => UpdateOperation();

    private void OnServicesChanged(object sender, SelectionChangedEventArgs args)
    {
        var selected = _services.SelectedItems.Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _replicaInputs.Keys.Where(name => !selected.Contains(name)).ToArray())
        {
            _replicas.Children.Remove(_replicaInputs[removed]);
            _replicaInputs.Remove(removed);
        }
        foreach (var name in selected.Where(name => !_replicaInputs.ContainsKey(name)))
        {
            var service = _project.Services.Single(s => s.Name == name);
            var input = new NumberBox
            {
                Header = $"{name} — desired replicas",
                Minimum = 0,
                Maximum = int.MaxValue,
                SmallChange = 1,
                LargeChange = 1,
                Value = ComposeReconciliationPlanner.DesiredReplicas(_project, service, new()),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };
            AutomationProperties.SetAutomationId(input, $"ComposeReplicas_{name}");
            AutomationProperties.SetName(input, $"Desired replicas for {name}");
            _replicaInputs.Add(name, input);
            _replicas.Children.Add(input);
        }
        if (_services.SelectedItems.Count > 0)
        {
            _validation.IsOpen = false;
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_services.SelectedItems.Count == 0)
        {
            args.Cancel = true;
            _validation.Message = "Select at least one service before continuing.";
            _validation.IsOpen = true;
            _services.Focus(FocusState.Programmatic);
        }
        else if (Operation == ComposeLifecycleOperation.Up && _replicaInputs.Values.Any(input =>
            !double.IsFinite(input.Value) || input.Value < 0 || input.Value > int.MaxValue || Math.Truncate(input.Value) != input.Value))
        {
            args.Cancel = true;
            _validation.Message = "Every replica count must be a nonnegative whole number.";
            _validation.IsOpen = true;
        }
    }
}
