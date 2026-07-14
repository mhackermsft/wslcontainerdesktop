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

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Lets the user pick a <c>docker-compose.yml</c> from disk to import. Importing from a file (rather
/// than pasting text) is required so relative <c>env_file</c> paths and a sibling <c>.env</c> file
/// resolve against the compose file's folder (see <see cref="Services.ComposeImporter"/>).
/// </summary>
public sealed class ImportComposeDialog : ContentDialog
{
    private readonly TextBlock _fileLabel;

    /// <summary>The compose text read from the chosen file.</summary>
    public string Yaml { get; private set; } = string.Empty;

    /// <summary>Full path of the chosen compose file, or null if none was selected.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Directory the compose file lives in, used to resolve <c>.env</c> and relative paths.</summary>
    public string? BaseDirectory =>
        string.IsNullOrEmpty(FilePath) ? null : Path.GetDirectoryName(FilePath);

    public ImportComposeDialog()
    {
        Title = "Import compose file";
        PrimaryButtonText = "Import";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // Nothing can be imported until a file is chosen.
        IsPrimaryButtonEnabled = false;

        var browseButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { FontSize = 14, Glyph = "\uE838" },
                    new TextBlock { Text = "Choose a compose file…" },
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

        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Width = 460,
            Text = "Pick a docker-compose.yml (or .yaml) from disk. A sibling .env file and relative "
                 + "env_file paths are read from the same folder. Imported from the Compose page, the "
                 + "project keeps its depends_on order, restart and healthcheck policies (enforced by this "
                 + "app while it runs), environment interpolation, and resource limits. Imported from the "
                 + "Images/Containers page, each service is saved as a standalone run profile instead.",
        };

        Content = new StackPanel
        {
            Spacing = 12,
            Children = { topRow, hint },
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

            Yaml = await FileIO.ReadTextAsync(file);
            FilePath = file.Path;
            _fileLabel.Text = file.Name;
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(Yaml);
        }
        catch
        {
            Yaml = string.Empty;
            FilePath = null;
            _fileLabel.Text = "Could not read the selected file.";
            IsPrimaryButtonEnabled = false;
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(Yaml))
        {
            args.Cancel = true;
        }
    }
}
