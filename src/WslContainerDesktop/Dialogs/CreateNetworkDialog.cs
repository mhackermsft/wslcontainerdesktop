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
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Services;
using WslContainerDesktop.Views.Controls;

namespace WslContainerDesktop.Dialogs;

/// <summary>WinUI 3 content dialog that collects or confirms create network input before a view model calls the underlying service.</summary>
public sealed class CreateNetworkDialog : ContentDialog
{
    private readonly TextBox _nameBox;
    private readonly Expander _advanced;
    private readonly CheckBox _internalBox;
    private readonly TextBox _driverOptionsBox;
    private readonly TextBox _labelsBox;
    private readonly TextBox _subnetBox;
    private readonly TextBox _gatewayBox;
    private readonly TextBox _ipRangeBox;
    private readonly TextBlock _errorText;

    /// <summary>Creates a new &lt;c&gt;CreateNetworkDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    public CreateNetworkDialog()
    {
        Title = UiText.Get("Workload_Text_Create_network_dbf4e6", "Create network");
        PrimaryButtonText = UiText.Get("Workload_Text_Create_475949", "Create");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _nameBox = new TextBox { Header = UiText.Get("Workload_Text_Network_name_b97af0", "Network name"), PlaceholderText = UiText.Get("Workload_Text_e_g_app_net_8fe647", "e.g. app-net"), MinWidth = 420 };
        _internalBox = new CheckBox
        {
            Content = InfoTip.Labeled(
                new TextBlock { Text = UiText.Get("Workload_Text_Internal_network_internal_7c7e70", "Internal network (--internal)"), VerticalAlignment = VerticalAlignment.Center },
                InfoTip.Create(FlagHelp.NetworkInternal)),
        };
        _driverOptionsBox = new TextBox
        {
            Header = InfoTip.Header(UiText.Get("Workload_Text_Driver_options_one_key_value_per_0d7e76", "Driver options (one key=value per line)"), FlagHelp.NetworkDriverOptions),
            AcceptsReturn = true,
            MinHeight = 76,
            PlaceholderText = "com.example.option=value",
        };
        _labelsBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Labels_one_key_value_per_line_f7aa78", "Labels (one key=value per line)"),
            AcceptsReturn = true,
            MinHeight = 76,
            PlaceholderText = "com.example.owner=team",
        };
        _subnetBox = new TextBox { Header = UiText.Get("Workload_Text_Subnet_optional_CIDR_786318", "Subnet (optional, CIDR)"), PlaceholderText = UiText.Get("Workload_Text_e_g_172_28_0_0_c3ba25", "e.g. 172.28.0.0/16") };
        _gatewayBox = new TextBox { Header = UiText.Get("Workload_Text_Gateway_optional_9a3176", "Gateway (optional)"), PlaceholderText = UiText.Get("Workload_Text_e_g_172_28_0_1_af839f", "e.g. 172.28.0.1") };
        _ipRangeBox = new TextBox { Header = UiText.Get("Workload_Text_IP_range_optional_CIDR_within_the_205cbd", "IP range (optional, CIDR within the subnet)"), PlaceholderText = UiText.Get("Workload_Text_e_g_172_28_5_0_dba741", "e.g. 172.28.5.0/24") };
        _errorText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        // Addressing, driver options and labels are rarely needed, so they start collapsed.
        _advanced = new Expander
        {
            Header = UiText.Get("Workload_Text_Advanced_options_9443ff", "Advanced options"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 10,
                Children = { _subnetBox, _gatewayBox, _ipRangeBox, _driverOptionsBox, _labelsBox },
            },
        };

        // The error sits first so a validation failure is visible without scrolling the dialog.
        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _errorText, _nameBox, _internalBox, _advanced },
        };

        Loaded += (_, _) => _nameBox.Focus(FocusState.Programmatic);
        PrimaryButtonClick += OnPrimary;
    }

    /// <summary>Gets or sets the network name.</summary>
    public string NetworkName { get; private set; } = string.Empty;
    /// <summary>Gets or sets the driver.</summary>
    public string Driver { get; private set; } = "bridge";
    /// <summary>Gets or sets a value indicating whether the internal network flag is set.</summary>
    public bool InternalNetwork { get; private set; }
    /// <summary>Gets or sets the driver options.</summary>
    public IReadOnlyList<string> DriverOptions { get; private set; } = Array.Empty<string>();
    /// <summary>Gets or sets the labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; private set; } = new Dictionary<string, string>();
    /// <summary>Gets or sets the subnet.</summary>
    public string? Subnet { get; private set; }
    /// <summary>Gets or sets the gateway.</summary>
    public string? Gateway { get; private set; }
    /// <summary>Gets or sets the ip range.</summary>
    public string? IpRange { get; private set; }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = (_nameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Fail(args, UiText.Get("Workload_Text_Enter_a_network_name_d93a83", "Enter a network name."));
            _nameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!TryParseKeyValues(_driverOptionsBox.Text, requireValue: true, out var opts, out var error) ||
            !TryParseKeyValues(_labelsBox.Text, requireValue: false, out var labels, out error))
        {
            _advanced.IsExpanded = true;
            Fail(args, error ?? UiText.Get("Workload_Text_Invalid_key_value_entry_16c85f", "Invalid key/value entry."));
            return;
        }

        if (NetworkAddressing.Validate(_subnetBox.Text, _gatewayBox.Text, _ipRangeBox.Text) is { } addressError)
        {
            _advanced.IsExpanded = true;
            Fail(args, addressError);
            return;
        }

        NetworkName = name;
        InternalNetwork = _internalBox.IsChecked == true;
        DriverOptions = opts.Select(kv => $"{kv.Key}={kv.Value}").ToList();
        Labels = labels;
        Subnet = Blank(_subnetBox.Text);
        Gateway = Blank(_gatewayBox.Text);
        IpRange = Blank(_ipRangeBox.Text);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Fail(ContentDialogButtonClickEventArgs args, string message)
    {
        args.Cancel = true;
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
    }

    private static bool TryParseKeyValues(
        string text,
        bool requireValue,
        out Dictionary<string, string> values,
        out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        // WinUI multi-line TextBoxes report line breaks as '\r'; accept '\r', '\n' and "\r\n".
        foreach (var line in (text ?? string.Empty)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=');
            var key = equals < 0 ? line.Trim() : line[..equals].Trim();
            var value = equals < 0 ? string.Empty : line[(equals + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl))
            {
                error = UiText.Get("Workload_Text_Invalid_key_0_Keys_cannot_be_062b6d", "Invalid key '{0}'. Keys cannot be blank, contain '=', or contain control characters.", key);
                return false;
            }

            if ((requireValue && equals < 0) || value.Any(char.IsControl))
            {
                error = UiText.Get("Workload_Text_Invalid_value_for_0_Use_key_981fe2", "Invalid value for '{0}'. Use key=value and avoid control characters.", key);
                return false;
            }

            values[key] = value;
        }

        return true;
    }
}
