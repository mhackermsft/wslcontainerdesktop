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
/// Lets the user paste a <c>docker run …</c> (or <c>podman run …</c>) command line; on confirm it is
/// parsed into <see cref="Options"/> via <see cref="DockerRunParser"/>. Flags that can't be
/// represented are surfaced in <see cref="Warnings"/> so the caller can show them.
/// </summary>
public sealed class ImportDockerRunDialog : ContentDialog
{
    private readonly TextBox _input;
    private readonly TextBlock _status;

    /// <summary>The parsed options once the user confirms with a valid command, else null.</summary>
    public RunContainerOptions? Options { get; private set; }

    /// <summary>Notes about flags that could not be represented in the parsed options.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = System.Array.Empty<string>();

    public ImportDockerRunDialog()
    {
        Title = "Import from docker run";
        PrimaryButtonText = "Import";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        Resources["ContentDialogMaxWidth"] = 720.0;
        Resources["ContentDialogMinWidth"] = 560.0;

        _input = new TextBox
        {
            Header = "Paste a docker run command",
            PlaceholderText = "docker run -d --name web -p 8080:80 -e TZ=UTC nginx:alpine",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 500,
            MinHeight = 120,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };

        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Width = 500,
            Text = "Recognized flags (ports, env, volumes, --name, --network, -w, -u, --entrypoint, "
                 + "--gpus, and more) prefill the Run dialog. Line-continuation backslashes are fine. "
                 + "Anything that can't be represented is reported and skipped.",
        };

        _status = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Width = 500,
            Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _input, hint, _status },
        };

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var result = DockerRunParser.Parse(_input.Text);
        if (result.Options is null)
        {
            args.Cancel = true;
            _status.Text = "Couldn't find an image reference in that command. "
                         + "Paste a full 'docker run … <image>' line.";
            _status.Visibility = Visibility.Visible;
            _input.Focus(FocusState.Programmatic);
            return;
        }

        Options = result.Options;
        Warnings = result.Warnings;
    }
}
