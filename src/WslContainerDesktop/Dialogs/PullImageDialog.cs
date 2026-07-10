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

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Pull an image, choosing which registered registry to pull from. The selected registry's
/// host qualifies a bare reference; a fully-qualified reference is passed through unchanged.
/// </summary>
public sealed class PullImageDialog : ContentDialog
{
    private readonly ComboBox _registryBox;
    private readonly TextBox _referenceBox;
    private readonly TextBlock _preview;
    private readonly IReadOnlyList<RegistryEntry> _registries;

    /// <summary>The fully-resolved image reference to pull.</summary>
    public string Reference { get; private set; } = string.Empty;

    public PullImageDialog(IReadOnlyList<RegistryEntry> registries)
    {
        _registries = registries;

        Title = "Pull image";
        PrimaryButtonText = "Pull";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _registryBox = new ComboBox
        {
            Header = "Registry",
            MinWidth = 380,
        };
        foreach (var r in registries)
        {
            _registryBox.Items.Add(r.HasHost ? $"{r.Name} ({r.Host})" : r.Name);
        }

        _registryBox.SelectedIndex = 0;
        _registryBox.SelectionChanged += (_, _) => UpdatePreview();

        _referenceBox = new TextBox
        {
            Header = "Image reference",
            PlaceholderText = "e.g. ubuntu:latest or myapp:1.0",
        };
        _referenceBox.TextChanged += (_, _) => UpdatePreview();

        _preview = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _registryBox, _referenceBox, _preview },
        };

        UpdatePreview();
        Loaded += (_, _) => _referenceBox.Focus(FocusState.Programmatic);
        PrimaryButtonClick += OnPrimary;
    }

    private RegistryEntry SelectedRegistry =>
        _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries[0];

    private void UpdatePreview()
    {
        var input = (_referenceBox?.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(input))
        {
            _preview.Text = "Will pull: —";
            return;
        }

        _preview.Text = $"Will pull: {SelectedRegistry.Qualify(input)}";
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var input = _referenceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            args.Cancel = true;
            _referenceBox.Focus(FocusState.Programmatic);
            return;
        }

        Reference = SelectedRegistry.Qualify(input);
    }
}
