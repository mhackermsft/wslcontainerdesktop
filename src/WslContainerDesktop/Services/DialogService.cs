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

namespace WslContainerDesktop.Services;

/// <summary>
/// Centralizes ContentDialog display. The active window sets <see cref="XamlRoot"/>
/// so view models can raise dialogs without holding a reference to the window.
/// </summary>
public sealed class DialogService
{
    public XamlRoot? XamlRoot { get; set; }

    // ContentDialog content renders in a separate popup scope that does NOT inherit the app-level
    // TextControlPlaceholderForeground override declared in App.xaml, so hint text in dialog
    // TextBoxes would otherwise render at the framework's brighter default. Copy those dim
    // placeholder brushes into each dialog's own resource scope (which its content DOES inherit)
    // so placeholders match the rest of the app.
    private static readonly string[] PlaceholderBrushKeys =
    {
        "TextControlPlaceholderForeground",
        "TextControlPlaceholderForegroundPointerOver",
        "TextControlPlaceholderForegroundFocused",
        "ComboBoxPlaceHolderForeground",
        "ComboBoxPlaceHolderForegroundPointerOver",
        "ComboBoxPlaceHolderForegroundFocused",
        "ComboBoxPlaceHolderForegroundPressed",
        "ComboBoxPlaceHolderForegroundFocusedPressed",
    };

    private static void ApplyDimPlaceholders(ContentDialog dialog)
    {
        var appResources = Application.Current.Resources;
        foreach (var key in PlaceholderBrushKeys)
        {
            if (appResources.TryGetValue(key, out var brush))
            {
                dialog.Resources[key] = brush;
            }
        }
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                MaxHeight = 480,
            },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };

        ApplyDimPlaceholders(dialog);
        await dialog.ShowAsync();
    }

    public async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "Yes", string closeText = "Cancel")
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ApplyDimPlaceholders(dialog);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>Shows a caller-built dialog, wiring the correct XamlRoot.</summary>
    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = XamlRoot;
        ApplyDimPlaceholders(dialog);
        return await dialog.ShowAsync();
    }
}
