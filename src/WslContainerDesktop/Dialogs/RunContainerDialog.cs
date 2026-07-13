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
    private readonly IRunProfileStore _profiles;

    private readonly ComboBox _profileBox;
    private readonly TextBox _profileNameBox;
    private readonly TextBlock _profileStatus;
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

    private readonly List<RunProfile> _profileItems = new();
    private bool _applyingProfile;

    public RunContainerOptions? Options { get; private set; }

    public RunContainerDialog(IWslcService wslc, IReadOnlyList<RegistryEntry> registries, IRunProfileStore profiles, string? prefillImage = null)
    {
        _wslc = wslc;
        _registries = registries;
        _profiles = profiles;

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

        // Saved run profiles: pick one to prefill the form, or save/delete the current settings.
        _profileBox = new ComboBox
        {
            Header = "Saved profile",
            MinWidth = 460,
            PlaceholderText = "None",
        };
        _profileBox.SelectionChanged += OnProfileSelected;

        _profileNameBox = new TextBox
        {
            PlaceholderText = "Profile name",
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var saveProfileButton = new Button
        {
            Content = "Save as profile",
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        saveProfileButton.Click += OnSaveProfile;

        var deleteProfileButton = new Button
        {
            Content = "Delete",
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        deleteProfileButton.Click += OnDeleteProfile;

        var profileButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _profileNameBox, saveProfileButton, deleteProfileButton },
        };

        _profileStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var profileSection = new StackPanel
        {
            Spacing = 8,
            Children = { _profileBox, profileButtons, _profileStatus },
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                profileSection,
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

        ReloadProfiles(null);

        Opened += OnOpened;
        PrimaryButtonClick += OnPrimary;
    }

    /// <summary>Repopulates the profile picker; when <paramref name="selectName"/> is set, selects it.</summary>
    private void ReloadProfiles(string? selectName)
    {
        _applyingProfile = true;
        try
        {
            _profileItems.Clear();
            _profileItems.AddRange(_profiles.GetAll());

            _profileBox.Items.Clear();
            _profileBox.Items.Add("None");
            foreach (var profile in _profileItems)
            {
                _profileBox.Items.Add(FormatProfile(profile));
            }

            var index = string.IsNullOrWhiteSpace(selectName)
                ? -1
                : _profileItems.FindIndex(p => string.Equals(p.Name, selectName, StringComparison.OrdinalIgnoreCase));
            _profileBox.SelectedIndex = index >= 0 ? index + 1 : 0;
        }
        finally
        {
            _applyingProfile = false;
        }
    }

    private static string FormatProfile(RunProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Options.Image)
            ? profile.Name
            : $"{profile.Name}  ·  {profile.Options.Image}";

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingProfile)
        {
            return;
        }

        var index = _profileBox.SelectedIndex - 1;
        if (index < 0 || index >= _profileItems.Count)
        {
            return;
        }

        ApplyProfile(_profileItems[index]);
    }

    /// <summary>Prefills every field from a saved profile so the user can tweak and run it.</summary>
    private void ApplyProfile(RunProfile profile)
    {
        var options = profile.Options;

        if (!string.IsNullOrWhiteSpace(options.Image) && !_imageBox.Items.Contains(options.Image))
        {
            _imageBox.Items.Add(options.Image);
        }

        _imageBox.Text = options.Image;
        // The saved image is already fully qualified; keep the default registry so it isn't re-qualified.
        _registryBox.SelectedIndex = 0;
        _nameBox.Text = options.Name ?? string.Empty;
        _networkBox.Text = options.Network ?? string.Empty;
        _detached.IsChecked = options.Detached;
        _removeOnExit.IsChecked = options.RemoveOnExit;
        _interactive.IsChecked = options.Interactive;
        _gpus.IsChecked = options.AllGpus;
        _commandBox.Text = options.Command ?? string.Empty;
        _portsBox.Text = string.Join('\n', options.PortMappings);
        _envBox.Text = string.Join('\n', options.EnvironmentVariables);
        _volumesBox.Text = string.Join('\n', options.Volumes);
        _profileNameBox.Text = profile.Name;
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions();
        if (options is null)
        {
            _imageBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            ShowProfileStatus("Enter an image before saving a profile.");
            return;
        }

        var name = (_profileNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            _profileNameBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            ShowProfileStatus("Enter a profile name before saving.");
            return;
        }

        _profiles.Save(new RunProfile { Name = name, Options = options });
        ReloadProfiles(name);
        ShowProfileStatus($"Saved profile \"{name}\".");
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        var index = _profileBox.SelectedIndex - 1;
        if (index < 0 || index >= _profileItems.Count)
        {
            ShowProfileStatus("Select a saved profile to delete.");
            return;
        }

        var name = _profileItems[index].Name;
        _profiles.Delete(name);
        ReloadProfiles(null);
        _profileNameBox.Text = string.Empty;
        ShowProfileStatus($"Deleted profile \"{name}\".");
    }

    private void ShowProfileStatus(string message)
    {
        _profileStatus.Text = message;
        _profileStatus.Visibility = Visibility.Visible;
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
        var options = BuildOptions();
        if (options is null)
        {
            args.Cancel = true;
            _imageBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            return;
        }

        Options = options;
    }

    /// <summary>
    /// Reads the current form into a <see cref="RunContainerOptions"/>, or returns null when no
    /// image is entered. Shared by "Run" and "Save as profile".
    /// </summary>
    private RunContainerOptions? BuildOptions()
    {
        var image = (_imageBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(image) && _imageBox.SelectedItem is string sel)
        {
            image = sel;
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        // Qualify a bare image name with the selected registry's host.
        var registry = _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries.Count > 0 ? _registries[0] : null;
        if (registry is not null)
        {
            image = registry.Qualify(image);
        }

        return new RunContainerOptions
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
