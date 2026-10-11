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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Views.Controls;

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

    /// <summary>Gets a value indicating whether the save replica overrides flag is set.</summary>
    public bool SaveReplicaOverrides => Operation == ComposeLifecycleOperation.Up && _saveReplicas.IsChecked == true;

    private ComposeLifecycleOperation Operation => _operation.SelectedIndex switch
    {
        1 => ComposeLifecycleOperation.Restart,
        2 => ComposeLifecycleOperation.Stop,
        3 => ComposeLifecycleOperation.Down,
        _ => ComposeLifecycleOperation.Up,
    };

    /// <summary>Gets the operation label.</summary>
    public string OperationLabel => Operation switch
    {
        ComposeLifecycleOperation.Restart => UiText.Get("Workload_Text_Restart_6b983a", "Restart"),
        ComposeLifecycleOperation.Stop => UiText.Get("Workload_Text_Stop_cae7d5", "Stop"),
        ComposeLifecycleOperation.Down => UiText.Get("Workload_Text_Remove_containers_f0e82a", "Remove containers"),
        _ => UiText.Get("Workload_Text_Apply_up_e6346a", "Apply (up)"),
    };

    /// <summary>Gets the request.</summary>
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

    /// <summary>Creates a new &lt;c&gt;ComposeServicesDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="project">The project value supplied by the caller.</param>
    public ComposeServicesDialog(ComposeProject project)
    {
        _project = project;
        Title = UiText.Get("Workload_Text_Manage_services_0_cdca2c", "Manage services: {0}", project.Name);
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
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
        AutomationProperties.SetName(_services, UiText.Get("Workload_Text_Services_to_manage_4e428f", "Services to manage"));

        _operation = new ComboBox
        {
            Header = UiText.Get("Workload_Text_Operation_0f044f", "Operation"),
            ItemsSource = new[] { UiText.Get("Workload_Text_Apply_up_e6346a", "Apply (up)"), UiText.Get("Workload_Text_Restart_6b983a", "Restart"), UiText.Get("Workload_Text_Stop_cae7d5", "Stop"), UiText.Get("Workload_Text_Remove_containers_f0e82a", "Remove containers") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_operation, "ComposeServiceOperation");

        _rebuild = new CheckBox
        {
            Content = InfoTip.Labeled(
                new TextBlock { Text = UiText.Get("Workload_Text_Rebuild_images_when_applying_6434fd", "Rebuild images when applying"), VerticalAlignment = VerticalAlignment.Center },
                InfoTip.Create(FlagHelp.ComposeRebuild)),
            IsChecked = false,
        };
        AutomationProperties.SetAutomationId(_rebuild, "ComposeServiceRebuild");
        _replicas = new StackPanel { Spacing = 8 };
        _saveReplicas = new CheckBox { Content = UiText.Get("Workload_Text_Save_these_replica_counts_for_future_f66fa8", "Save these replica counts for future applies"), IsChecked = true };
        AutomationProperties.SetAutomationId(_saveReplicas, "ComposeSaveReplicaOverrides");
        _description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_description, "ComposeServiceScope");
        _validation = new InfoBar
        {
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Message = UiText.Get("Workload_Text_Select_at_least_one_service_before_f4ea1a", "Select at least one service before continuing."),
        };
        AutomationProperties.SetAutomationId(_validation, "ComposeServiceValidation");

        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = UiText.Get("Workload_Text_Select_one_or_more_services_15d5fd", "Select one or more services."), TextWrapping = TextWrapping.Wrap },
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
                UiText.Get("Workload_Text_Stop_and_start_only_existing_selected_3b48b2", "Stop and start only existing selected containers, plus dependents with restart: true. Does not create or recreate containers, build images, or apply configuration changes."),
            ComposeLifecycleOperation.Stop =>
                UiText.Get("Workload_Text_Stop_only_the_selected_services_Containers_f80159", "Stop only the selected services. Containers, shared networks, and volumes are preserved. Other services are not stopped and may lose access to these services."),
            ComposeLifecycleOperation.Down =>
                UiText.Get("Workload_Text_Stop_and_remove_only_the_selected_e2c247", "Stop and remove only the selected services' containers. Shared networks, volumes, and the project definition are preserved. Other services are not removed and may lose access to these services."),
            _ =>
                UiText.Get("Workload_Text_Apply_configuration_to_the_selected_services_c06fac", "Apply configuration to the selected services and their required dependency closure. Unchanged running containers are kept; stopped containers are started and changed containers may be recreated. Replicas are local instances, not a Swarm deployment. Zero removes all selected instances. Named volumes and bind mounts are shared; anonymous volumes belong to each instance and are preserved on recreation. Scaling down does not delete volumes. A local operation plans at most {0} total instances.", ComposeReconciliationPlanner.MaximumPlanInstances),
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
                Header = UiText.Get("Workload_Text_0_desired_replicas_95c372", "{0} — desired replicas", name),
                Minimum = 0,
                Maximum = int.MaxValue,
                SmallChange = 1,
                LargeChange = 1,
                Value = ComposeReconciliationPlanner.DesiredReplicas(_project, service, new()),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };
            AutomationProperties.SetAutomationId(input, $"ComposeReplicas_{name}");
            AutomationProperties.SetName(input, UiText.Get("Workload_Text_Desired_replicas_for_0_306ce6", "Desired replicas for {0}", name));
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
            _validation.Message = UiText.Get("Workload_Text_Select_at_least_one_service_before_f4ea1a", "Select at least one service before continuing.");
            _validation.IsOpen = true;
            _services.Focus(FocusState.Programmatic);
        }
        else if (Operation == ComposeLifecycleOperation.Up && _replicaInputs.Values.Any(input =>
            !double.IsFinite(input.Value) || input.Value < 0 || input.Value > int.MaxValue || Math.Truncate(input.Value) != input.Value))
        {
            args.Cancel = true;
            _validation.Message = UiText.Get("Workload_Text_Every_replica_count_must_be_a_b03ed1", "Every replica count must be a nonnegative whole number.");
            _validation.IsOpen = true;
        }
    }
}
