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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Rich "Run a container" form mirroring the common docker/podman run options.
/// Populates <see cref="Options"/> when the user confirms.
/// </summary>
public sealed class RunContainerDialog : ContentDialog
{
    private readonly IWslcService _wslc;
    private readonly IReadOnlyList<RegistryEntry> _registries;

    private readonly ComboBox _imageBox;
    private readonly ComboBox _registryBox;
    private readonly TextBox _nameBox;
    private readonly ComboBox _networkBox;
    private readonly TextBox _portsBox;
    private readonly TextBox _envBox;
    private readonly TextBox _volumesBox;
    private readonly TextBox _commandBox;
    private readonly CheckBox _detached;
    private readonly CheckBox _removeOnExit;
    private readonly CheckBox _interactive;
    private readonly CheckBox _gpus;

    public RunContainerOptions? Options { get; private set; }

    public RunContainerDialog(IWslcService wslc, IReadOnlyList<RegistryEntry> registries, string? prefillImage = null)
    {
        _wslc = wslc;
        _registries = registries;

        Title = "Run a container";
        PrimaryButtonText = "Run";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // Widen the dialog so option labels are never clipped.
        Resources["ContentDialogMaxWidth"] = 720.0;
        Resources["ContentDialogMinWidth"] = 620.0;

        _imageBox = new ComboBox
        {
            Header = "Image",
            IsEditable = true,
            MinWidth = 460,
            PlaceholderText = "e.g. ubuntu:latest",
        };

        _registryBox = new ComboBox
        {
            Header = "Registry (qualifies a bare image name)",
            MinWidth = 460,
        };
        foreach (var r in registries)
        {
            _registryBox.Items.Add(r.HasHost ? $"{r.Name} ({r.Host})" : r.Name);
        }

        _registryBox.SelectedIndex = 0;

        _nameBox = new TextBox { Header = "Name (optional)", PlaceholderText = "my-container" };

        _networkBox = new ComboBox
        {
            Header = "Network",
            IsEditable = true,
            MinWidth = 460,
            PlaceholderText = "Default (bridge)",
        };
        // First entry is the engine default; user networks are added on open.
        _networkBox.Items.Add("Default (bridge)");
        _networkBox.SelectedIndex = 0;

        _portsBox = new TextBox
        {
            Header = "Port mappings (one per line: host:container[/proto])",
            PlaceholderText = "8080:80\n5432:5432/tcp",
            AcceptsReturn = true,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            MinHeight = 60,
        };

        _envBox = new TextBox
        {
            Header = "Environment variables (one KEY=VALUE per line)",
            PlaceholderText = "POSTGRES_PASSWORD=secret",
            AcceptsReturn = true,
            MinHeight = 60,
        };

        _volumesBox = new TextBox
        {
            Header = "Volumes / binds (one per line: source:destination)",
            PlaceholderText = "my-data:/var/lib/data",
            AcceptsReturn = true,
            MinHeight = 50,
        };

        _commandBox = new TextBox
        {
            Header = "Command / arguments (optional)",
            PlaceholderText = "sleep infinity",
        };

        _detached = new CheckBox { Content = "Run in background (-d)", IsChecked = true };
        _removeOnExit = new CheckBox { Content = "Remove when it exits (--rm)" };
        _interactive = new CheckBox { Content = "Keep STDIN open (-i)" };
        _gpus = new CheckBox { Content = "Pass all GPUs (--gpus all)" };

        // 2x2 grid so long checkbox labels never clip on the dialog width.
        var toggles = new Grid { ColumnSpacing = 16, RowSpacing = 2 };
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_detached, 0);
        Grid.SetColumn(_detached, 0);
        Grid.SetRow(_removeOnExit, 0);
        Grid.SetColumn(_removeOnExit, 1);
        Grid.SetRow(_interactive, 1);
        Grid.SetColumn(_interactive, 0);
        Grid.SetRow(_gpus, 1);
        Grid.SetColumn(_gpus, 1);
        toggles.Children.Add(_detached);
        toggles.Children.Add(_removeOnExit);
        toggles.Children.Add(_interactive);
        toggles.Children.Add(_gpus);

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _imageBox,
                _registryBox,
                _nameBox,
                _networkBox,
                toggles,
                _portsBox,
                _envBox,
                _volumesBox,
                _commandBox,
            },
        };

        Content = new ScrollViewer
        {
            Content = panel,
            MaxHeight = 560,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        if (!string.IsNullOrWhiteSpace(prefillImage))
        {
            _imageBox.Items.Add(prefillImage);
            _imageBox.SelectedItem = prefillImage;
            _imageBox.Text = prefillImage;
        }

        Opened += OnOpened;
        PrimaryButtonClick += OnPrimary;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            var images = await _wslc.ListImagesAsync();
            var current = _imageBox.Text;
            foreach (var reference in images.Select(i => i.Reference).Distinct())
            {
                if (!_imageBox.Items.Contains(reference))
                {
                    _imageBox.Items.Add(reference);
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                _imageBox.Text = current;
            }
        }
        catch
        {
            // Non-fatal; the user can still type an image reference.
        }

        try
        {
            var networks = await _wslc.ListNetworksAsync();
            foreach (var name in networks.Select(n => n.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                if (!_networkBox.Items.Contains(name))
                {
                    _networkBox.Items.Add(name);
                }
            }
        }
        catch
        {
            // Non-fatal; the user can still type a network name or use the default.
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var image = (_imageBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(image) && _imageBox.SelectedItem is string sel)
        {
            image = sel;
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            args.Cancel = true;
            _imageBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            return;
        }

        // Qualify a bare image name with the selected registry's host.
        var registry = _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries.Count > 0 ? _registries[0] : null;
        if (registry is not null)
        {
            image = registry.Qualify(image);
        }

        Options = new RunContainerOptions
        {
            Image = image,
            Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? null : _nameBox.Text.Trim(),
            Network = ResolveNetwork(),
            Detached = _detached.IsChecked == true,
            RemoveOnExit = _removeOnExit.IsChecked == true,
            Interactive = _interactive.IsChecked == true,
            AllGpus = _gpus.IsChecked == true,
            Command = string.IsNullOrWhiteSpace(_commandBox.Text) ? null : _commandBox.Text.Trim(),
            PortMappings = SplitLines(_portsBox.Text),
            EnvironmentVariables = SplitLines(_envBox.Text),
            Volumes = SplitLines(_volumesBox.Text),
        };
    }

    /// <summary>Returns the chosen network name, or null for the engine default bridge.</summary>
    private string? ResolveNetwork()
    {
        var value = (_networkBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(value) && _networkBox.SelectedItem is string sel)
        {
            value = sel;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
