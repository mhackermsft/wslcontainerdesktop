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
using Microsoft.UI.Xaml.Media;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Lets the user tweak a compose template before launching it: edit the project name and the raw
/// docker-compose YAML (e.g. change ports, images, or credentials). Confirming saves the edits so
/// the template's Launch button reuses them next time.
/// </summary>
public sealed class ConfigureComposeDialog : ContentDialog
{
    private readonly TextBox _nameBox;
    private readonly TextBox _yamlBox;

    /// <summary>The (possibly edited) project name.</summary>
    public string ProjectName => _nameBox.Text?.Trim() ?? string.Empty;

    /// <summary>The (possibly edited) compose YAML, normalized to <c>\n</c> line endings.</summary>
    public string Yaml => NormalizeToLf(_yamlBox.Text ?? string.Empty);

    public ConfigureComposeDialog(string templateName, string projectName, string yaml)
    {
        Title = $"Configure {templateName}";
        PrimaryButtonText = "Save & launch";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // Widen the dialog so the editor and hint aren't clipped on the right (the default
        // ContentDialog is too narrow for a code editor).
        Resources["ContentDialogMaxWidth"] = 760.0;
        Resources["ContentDialogMinWidth"] = 640.0;

        _nameBox = new TextBox
        {
            Header = "Project name",
            Text = projectName ?? string.Empty,
        };

        _yamlBox = new TextBox
        {
            Header = "Compose YAML",
            // AcceptsReturn MUST be set before Text: a single-line TextBox (the default) truncates
            // its Text at the first line break, so setting Text first would drop everything after
            // line one. Line endings are normalized to \r\n because a WinUI TextBox only breaks on
            // \r, and C# raw string literals normalize newlines to bare \n even in CRLF files.
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _yamlBox.Text = NormalizeToCrLf(yaml ?? string.Empty);
        ScrollViewer.SetHorizontalScrollBarVisibility(_yamlBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_yamlBox, ScrollBarVisibility.Auto);

        var hint = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Text = "Edit ports, images, environment, or credentials as needed. Saving stores these "
                 + "changes so the template's Launch button reuses them, then imports and brings the "
                 + "project up.",
        };

        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 600,
            Children = { _nameBox, _yamlBox, hint },
        };

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(Yaml))
        {
            args.Cancel = true;
        }
    }

    /// <summary>Collapses any line-ending style to <c>\r\n</c> so a WinUI TextBox renders each line.</summary>
    private static string NormalizeToCrLf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    /// <summary>Collapses any line-ending style to <c>\n</c> for clean storage and parsing.</summary>
    private static string NormalizeToLf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');
}
