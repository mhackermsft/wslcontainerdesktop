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

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Configures a per-container health probe (in-container command or host-side TCP port)
/// plus a restart policy. Populates <see cref="Result"/> when the user confirms.
/// </summary>
public sealed class HealthCheckDialog : ContentDialog
{
    private readonly string _containerName;

    private readonly ToggleSwitch _enabled;
    private readonly ComboBox _kindBox;
    private readonly TextBox _commandBox;
    private readonly ComboBox _portBox;
    private readonly NumberBox _intervalBox;
    private readonly NumberBox _restartsBox;
    private readonly StackPanel _commandPanel;
    private readonly StackPanel _portPanel;

    public HealthCheckConfig? Result { get; private set; }

    public HealthCheckDialog(string containerName, IReadOnlyList<int> hostPorts, HealthCheckConfig? existing)
    {
        _containerName = containerName;

        Title = $"Health check · {containerName}";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        Resources["ContentDialogMaxWidth"] = 640.0;
        Resources["ContentDialogMinWidth"] = 520.0;

        _enabled = new ToggleSwitch
        {
            Header = "Enable health monitoring",
            IsOn = existing?.Enabled ?? true,
        };

        _kindBox = new ComboBox
        {
            Header = "Probe type",
            MinWidth = 460,
            Items = { "Command (wslc exec, exit 0 = healthy)", "TCP port (host-side connect)" },
        };
        _kindBox.SelectedIndex = existing?.Kind == HealthProbeKind.Tcp ? 1 : 0;

        _commandBox = new TextBox
        {
            Header = "Command run inside the container",
            PlaceholderText = "e.g. curl -fsS http://localhost:8080/health",
            Text = existing?.Command ?? string.Empty,
        };
        _commandPanel = new StackPanel { Spacing = 4, Children = { _commandBox } };

        _portBox = new ComboBox
        {
            Header = "Host port to connect to",
            IsEditable = true,
            MinWidth = 460,
            PlaceholderText = "e.g. 8080",
        };
        foreach (var port in hostPorts)
        {
            _portBox.Items.Add(port.ToString());
        }

        if (existing?.Kind == HealthProbeKind.Tcp && existing.TcpPort > 0)
        {
            _portBox.Text = existing.TcpPort.ToString();
        }
        else if (_portBox.Items.Count > 0)
        {
            _portBox.SelectedIndex = 0;
        }

        _portPanel = new StackPanel { Spacing = 4, Children = { _portBox } };

        _intervalBox = new NumberBox
        {
            Header = "Check interval (seconds)",
            Minimum = HealthCheckConfig.MinIntervalSeconds,
            Maximum = HealthCheckConfig.MaxIntervalSeconds,
            SmallChange = 5,
            LargeChange = 30,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = existing?.IntervalSeconds ?? 30,
        };

        _restartsBox = new NumberBox
        {
            Header = "Auto-restarts before alerting (0 = alert only)",
            Minimum = 0,
            Maximum = HealthCheckConfig.MaxRestartLimit,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = existing?.MaxRestarts ?? 3,
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _enabled,
                _kindBox,
                _commandPanel,
                _portPanel,
                _intervalBox,
                _restartsBox,
            },
        };

        Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 520,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _kindBox.SelectionChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();

        PrimaryButtonClick += OnPrimary;
    }

    private void UpdateVisibility()
    {
        var isCommand = _kindBox.SelectedIndex != 1;
        _commandPanel.Visibility = isCommand ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        _portPanel.Visibility = isCommand ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var kind = _kindBox.SelectedIndex == 1 ? HealthProbeKind.Tcp : HealthProbeKind.Command;

        var config = new HealthCheckConfig
        {
            ContainerName = _containerName,
            Kind = kind,
            IntervalSeconds = (int)Math.Round(double.IsNaN(_intervalBox.Value) ? 30 : _intervalBox.Value),
            MaxRestarts = (int)Math.Round(double.IsNaN(_restartsBox.Value) ? 0 : _restartsBox.Value),
            Enabled = _enabled.IsOn,
        };

        if (kind == HealthProbeKind.Command)
        {
            config.Command = (_commandBox.Text ?? string.Empty).Trim();
        }
        else
        {
            var portText = (_portBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(portText) && _portBox.SelectedItem is string sel)
            {
                portText = sel;
            }

            _ = int.TryParse(portText, out var port);
            config.TcpPort = port;
        }

        // Only block confirmation when monitoring is enabled but the probe is incomplete;
        // a disabled probe is allowed (it simply removes/pauses monitoring).
        if (config.Enabled && !config.IsValid)
        {
            args.Cancel = true;
            if (kind == HealthProbeKind.Command)
            {
                _commandBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            }
            else
            {
                _portBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            }

            return;
        }

        Result = config;
    }
}
