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
/// Push a local image to a registry. The selected registry's host qualifies a bare
/// reference; a fully-qualified reference is pushed as-is.
/// </summary>
public sealed class PushImageDialog : ContentDialog
{
    private readonly ComboBox _registryBox;
    private readonly TextBox _referenceBox;
    private readonly TextBlock _preview;
    private readonly TextBlock _error;
    private readonly IReadOnlyList<RegistryEntry> _registries;

    /// <summary>The fully-resolved reference to push.</summary>
    public string Reference { get; private set; } = string.Empty;

    public PushImageDialog(IReadOnlyList<RegistryEntry> registries, string? prefillReference = null)
    {
        _registries = registries;

        Title = "Push image";
        PrimaryButtonText = "Push";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _registryBox = new ComboBox
        {
            Header = "Registry",
            MinWidth = 400,
        };
        foreach (var r in registries)
        {
            _registryBox.Items.Add(r.HasHost ? $"{r.Name} ({r.Host})" : r.Name);
        }

        // If the prefilled reference already targets a known registry host, preselect it.
        var initialIndex = 0;
        if (!string.IsNullOrWhiteSpace(prefillReference))
        {
            for (var i = 0; i < registries.Count; i++)
            {
                if (registries[i].HasHost &&
                    prefillReference.StartsWith(registries[i].Host + "/", StringComparison.OrdinalIgnoreCase))
                {
                    initialIndex = i;
                    break;
                }
            }
        }

        _registryBox.SelectedIndex = initialIndex;
        _registryBox.SelectionChanged += (_, _) => UpdatePreview();

        _referenceBox = new TextBox
        {
            Header = "Image reference",
            PlaceholderText = "e.g. myrepo/myimage:1.0",
            Text = prefillReference ?? string.Empty,
        };
        _referenceBox.TextChanged += (_, _) => UpdatePreview();

        _preview = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        _error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _registryBox, _referenceBox, _preview, _error },
        };

        UpdatePreview();
        PrimaryButtonClick += OnPrimary;
    }

    private RegistryEntry SelectedRegistry =>
        _registryBox.SelectedIndex >= 0 && _registryBox.SelectedIndex < _registries.Count
            ? _registries[_registryBox.SelectedIndex]
            : _registries[0];

    private void UpdatePreview()
    {
        var input = (_referenceBox?.Text ?? string.Empty).Trim();
        _preview.Text = string.IsNullOrEmpty(input)
            ? "Will push: —"
            : $"Will push: {SelectedRegistry.Qualify(input)}";

        if (_error is not null)
        {
            _error.Visibility = Visibility.Collapsed;
        }
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

        var target = SelectedRegistry.Qualify(input);
        var tag = ExtractTag(target);

        if (string.IsNullOrEmpty(tag))
        {
            ShowError("Add an explicit version tag (for example :1.0). " +
                "Without one the push defaults to \"latest\", which doesn't identify the actual version.");
            args.Cancel = true;
            _referenceBox.Focus(FocusState.Programmatic);
            return;
        }

        Reference = target;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Returns the explicit tag portion of an image reference, or null if it has none.
    /// Ignores any registry host <c>host:port</c> (a colon before the last '/') and any
    /// <c>@sha256:…</c> digest.
    /// </summary>
    private static string? ExtractTag(string reference)
    {
        var value = reference.Trim();
        var at = value.IndexOf('@');
        if (at >= 0)
        {
            value = value[..at];
        }

        var lastSlash = value.LastIndexOf('/');
        var lastComponent = lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
        var colon = lastComponent.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var tag = lastComponent[(colon + 1)..].Trim();
        return tag.Length == 0 ? null : tag;
    }
}
