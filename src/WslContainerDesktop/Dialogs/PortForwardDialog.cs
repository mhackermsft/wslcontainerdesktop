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

using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>Collects a target (pod or service) and local/remote ports for a port-forward.</summary>
public sealed class PortForwardDialog : ContentDialog
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    private readonly ComboBox _kindBox;
    private readonly ComboBox _targetBox;
    private readonly TextBox _localPortBox;
    private readonly TextBox _remotePortBox;

    private readonly List<(string Namespace, string Name)> _pods;
    private readonly List<(string Namespace, string Name)> _services;

    /// <summary>Gets or sets the result.</summary>
    public PortForward? Result { get; private set; }

    /// <summary>Creates the dialog with the available Kubernetes pods and services to choose from.</summary>
    public PortForwardDialog(
        List<(string Namespace, string Name)> pods,
        List<(string Namespace, string Name)> services)
    {
        _pods = pods;
        _services = services;

        Title = UiText.Get("Workload_Text_Forward_a_port_c39bd3", "Forward a port");
        PrimaryButtonText = UiText.Get("Workload_Text_Forward_f1c65e", "Forward");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _kindBox = new ComboBox { Header = UiText.Get("Workload_Text_Target_type_8a1c2f", "Target type"), MinWidth = 420 };
        _kindBox.Items.Add(UiText.Get("Workload_Text_Service_d67719", "Service"));
        _kindBox.Items.Add(UiText.Get("Workload_Text_Pod_0a6bb8", "Pod"));
        _kindBox.SelectedIndex = 0;
        _kindBox.SelectionChanged += (_, _) => PopulateTargets();

        _targetBox = new ComboBox { Header = UiText.Get("Workload_Text_Target_978354", "Target"), MinWidth = 420 };

        _localPortBox = new TextBox { Header = UiText.Get("Workload_Text_Local_port_9bc348", "Local port"), PlaceholderText = UiText.Get("Workload_Text_e_g_8080_452a03", "e.g. 8080") };
        _remotePortBox = new TextBox { Header = UiText.Get("Workload_Text_Target_port_cab875", "Target port"), PlaceholderText = UiText.Get("Workload_Text_e_g_80_d3a954", "e.g. 80") };

        var ports = new StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 12,
            Children = { _localPortBox, _remotePortBox },
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children = { _kindBox, _targetBox, ports },
        };

        PopulateTargets();
        PrimaryButtonClick += OnPrimary;
    }

    private void PopulateTargets()
    {
        _targetBox.Items.Clear();
        var isService = _kindBox.SelectedIndex == 0;
        var source = isService ? _services : _pods;
        foreach (var (ns, name) in source)
        {
            _targetBox.Items.Add($"{ns}/{name}");
        }

        if (_targetBox.Items.Count > 0)
        {
            _targetBox.SelectedIndex = 0;
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_targetBox.SelectedItem is not string target ||
            !int.TryParse(_localPortBox.Text.Trim(), out var local) || local < MinPort || local > MaxPort ||
            !int.TryParse(_remotePortBox.Text.Trim(), out var remote) || remote < MinPort || remote > MaxPort)
        {
            args.Cancel = true;
            return;
        }

        var slash = target.IndexOf('/');
        var ns = slash > 0 ? target[..slash] : "default";
        var name = slash > 0 ? target[(slash + 1)..] : target;

        Result = new PortForward
        {
            Kind = _kindBox.SelectedIndex == 0 ? PortForwardTargetKind.Service : PortForwardTargetKind.Pod,
            Namespace = ns,
            TargetName = name,
            LocalPort = local,
            RemotePort = remote,
        };
    }
}
