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
using Windows.Storage;
using Windows.Storage.Pickers;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Lets the user pick or paste a basic <c>docker-compose.yml</c> to seed one run profile per
/// service. Only the common single-container fields are honored; full compose orchestration is out
/// of scope (see <see cref="Services.ComposeImporter"/>).
/// </summary>
public sealed class ImportComposeDialog : ContentDialog
{
    private readonly TextBox _editor;
    private readonly TextBlock _fileLabel;

    /// <summary>The compose text the user chose to import.</summary>
    public string Yaml { get; private set; } = string.Empty;

    public ImportComposeDialog()
    {
        Title = "Import compose file";
        PrimaryButtonText = "Import";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var browseButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { FontSize = 14, Glyph = "\uE838" },
                    new TextBlock { Text = "Browse for a compose file…" },
                },
            },
        };
        browseButton.Click += OnBrowse;

        _fileLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = "No file selected",
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var topRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { browseButton, _fileLabel },
        };

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            PlaceholderText = "Paste a docker-compose.yml here, or browse for a file.",
            Height = 320,
            Width = 640,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);

        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = "Each service becomes a container. Imported from the Compose page, the project keeps its "
                 + "depends_on order, restart and healthcheck policies (enforced by this app while it runs), "
                 + "environment interpolation, and resource limits. Imported from the Images/Containers page, "
                 + "each service is saved as a standalone run profile instead.",
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children = { topRow, _editor, hint },
        };

        PrimaryButtonClick += OnPrimary;
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");

            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(
                App.Current.MainWindow!.AppWindow.Id);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var text = await FileIO.ReadTextAsync(file);
            _editor.Text = text;
            _fileLabel.Text = file.Name;
        }
        catch
        {
            _fileLabel.Text = "Could not read the selected file.";
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var text = _editor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            args.Cancel = true;
            return;
        }

        Yaml = text;
    }
}
