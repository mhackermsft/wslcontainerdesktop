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
using WslContainerDesktop.Models;
using WslContainerDesktop.Views.Controls;

namespace WslContainerDesktop.Dialogs;

/// <summary>WinUI 3 content dialog that collects or confirms connect network input before a view model calls the underlying service.</summary>
public sealed class ConnectNetworkDialog : ContentDialog
{
    private readonly ComboBox _networkBox;
    private readonly TextBox _aliasesBox;
    private readonly TextBox _ipv4Box;
    private readonly TextBlock _errorText;

    /// <summary>Creates a new &lt;c&gt;ConnectNetworkDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="networks">The networks value supplied by the caller.</param>
    /// <param name="alreadyConnected">The already connected value supplied by the caller.</param>
    public ConnectNetworkDialog(IEnumerable<NetworkInfo> networks, IEnumerable<string> alreadyConnected)
    {
        Title = "Connect to network";
        PrimaryButtonText = "Connect";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var connected = alreadyConnected.ToHashSet(StringComparer.Ordinal);
        _networkBox = new ComboBox
        {
            Header = "Network",
            IsEditable = true,
            MinWidth = 420,
            PlaceholderText = "Select a network…",
        };
        foreach (var name in networks.Select(n => n.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                     .Distinct(StringComparer.Ordinal).Where(n => !connected.Contains(n)))
        {
            _networkBox.Items.Add(name);
        }

        if (_networkBox.Items.Count > 0)
        {
            _networkBox.SelectedIndex = 0;
        }

        _aliasesBox = new TextBox
        {
            Header = InfoTip.Header("Aliases (optional, comma or line separated)", FlagHelp.NetworkAliases),
            PlaceholderText = "web, api",
            AcceptsReturn = true,
            MinHeight = 64,
        };
        _ipv4Box = new TextBox { Header = "IPv4 address (optional)", PlaceholderText = "172.28.0.10" };
        _errorText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel { Spacing = 10, Children = { _networkBox, _aliasesBox, _ipv4Box, _errorText } };
        Loaded += (_, _) => _networkBox.Focus(FocusState.Programmatic);
        PrimaryButtonClick += OnPrimary;
    }

    /// <summary>Gets or sets the attachment.</summary>
    public NetworkAttachment? Attachment { get; private set; }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var network = (_networkBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(network) && _networkBox.SelectedItem is string selected)
        {
            network = selected.Trim();
        }

        if (string.IsNullOrWhiteSpace(network))
        {
            Fail(args, "Select or enter a network name.");
            return;
        }

        var ipv4 = (_ipv4Box.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(ipv4) &&
            (!System.Net.IPAddress.TryParse(ipv4, out var address) ||
             address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            Fail(args, "IPv4 address must be a valid IPv4 literal.");
            return;
        }

        Attachment = new NetworkAttachment
        {
            Network = network,
            Ipv4Address = string.IsNullOrWhiteSpace(ipv4) ? null : ipv4,
            Aliases = (_aliasesBox.Text ?? string.Empty)
                .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private void Fail(ContentDialogButtonClickEventArgs args, string message)
    {
        args.Cancel = true;
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
    }
}
