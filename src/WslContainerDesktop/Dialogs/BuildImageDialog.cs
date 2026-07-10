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
/// Collects a build context, tag, and optional Dockerfile for `wslc build`. A registry
/// selector qualifies the tag with a registry host so the resulting image is ready to push.
/// </summary>
public sealed class BuildImageDialog : ContentDialog
{
    private readonly ComboBox _registryBox;
    private readonly TextBox _contextBox;
    private readonly TextBox _tagBox;
    private readonly TextBox _dockerfileBox;
    private readonly TextBlock _preview;
    private readonly IReadOnlyList<RegistryEntry> _registries;

    public string ContextPath => _contextBox.Text.Trim();

    /// <summary>The tag qualified with the selected registry's host.</summary>
    public string ImageTag => SelectedRegistry.Qualify(_tagBox.Text.Trim());

    public string? Dockerfile =>
        string.IsNullOrWhiteSpace(_dockerfileBox.Text) ? null : _dockerfileBox.Text.Trim();

    public BuildImageDialog(IReadOnlyList<RegistryEntry> registries)
    {
        _registries = registries;

        Title = "Build image";
        PrimaryButtonText = "Build";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _contextBox = new TextBox
        {
            Header = "Build context (folder path)",
            PlaceholderText = @"C:\src\myapp",
            MinWidth = 420,
        };

        _registryBox = new ComboBox
        {
            Header = "Registry (qualifies the tag for pushing)",
            MinWidth = 420,
        };
        foreach (var r in registries)
        {
            _registryBox.Items.Add(r.HasHost ? $"{r.Name} ({r.Host})" : r.Name);
        }

        _registryBox.SelectedIndex = 0;
        _registryBox.SelectionChanged += (_, _) => UpdatePreview();

        _tagBox = new TextBox
        {
            Header = "Tag",
            PlaceholderText = "myrepo/myimage:latest",
        };
        _tagBox.TextChanged += (_, _) => UpdatePreview();

        _dockerfileBox = new TextBox
        {
            Header = "Dockerfile (optional, relative to context)",
            PlaceholderText = "Dockerfile",
        };

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
            Children = { _contextBox, _registryBox, _tagBox, _preview, _dockerfileBox },
        };

        UpdatePreview();
        PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(ContextPath) || string.IsNullOrWhiteSpace(_tagBox.Text))
            {
                args.Cancel = true;
            }
        };
    }

    private RegistryEntry SelectedRegistry =>
        _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries[0];

    private void UpdatePreview()
    {
        var tag = (_tagBox?.Text ?? string.Empty).Trim();
        _preview.Text = string.IsNullOrEmpty(tag)
            ? "Image tag: —"
            : $"Image tag: {SelectedRegistry.Qualify(tag)}";
    }
}
