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
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Confirms saving a running container's captured settings as a reusable run profile: prompts for a
/// profile name and shows captured settings and storage limitations before any profile is saved.
/// </summary>
public sealed class SaveRunProfileDialog : ContentDialog
{
    private readonly TextBox _nameBox;

    /// <summary>The profile name the user confirmed, trimmed.</summary>
    public string ProfileName { get; private set; } = string.Empty;

    public SaveRunProfileDialog(string suggestedName, RunContainerOptions options, IReadOnlyList<string> notCaptured,
        IReadOnlyList<string>? warnings = null)
    {
        Title = "Save as run profile";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = warnings is { Count: > 0 } ? ContentDialogButton.Close : ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(this, "SaveRunProfileDialog");

        Resources["ContentDialogMaxWidth"] = 640.0;
        Resources["ContentDialogMinWidth"] = 480.0;

        _nameBox = new TextBox
        {
            Header = "Profile name",
            Text = suggestedName ?? string.Empty,
            PlaceholderText = "my-profile",
            MinWidth = 440,
        };
        AutomationProperties.SetAutomationId(_nameBox, "SaveRunProfileName");

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Width = 440,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Text = BuildSummary(options),
        };

        var summaryScroll = new ScrollViewer
        {
            Content = summary,
            MaxHeight = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var children = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _nameBox,
                new TextBlock
                {
                    Text = "Captured settings",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                },
                summaryScroll,
            },
        };

        if (warnings is { Count: > 0 })
        {
            var warningText = new TextBlock
            {
                Text = "Review storage before saving:\n\n" + string.Join("\n\n", warnings.Distinct(StringComparer.Ordinal))
                     + "\n\nCancel to keep profiles unchanged, or save and review storage after loading the profile.",
                TextWrapping = TextWrapping.Wrap,
                Width = 440,
            };
            AutomationProperties.SetAutomationId(warningText, "SaveRunProfileStorageWarnings");
            children.Children.Insert(1, new ScrollViewer
            {
                Content = warningText,
                MaxHeight = 180,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
        }

        if (notCaptured.Count > 0)
        {
            children.Children.Add(new TextBlock
            {
                Text = "Not captured (a running container doesn't expose these): "
                     + string.Join(", ", notCaptured)
                     + ". Add them after loading the profile if needed.",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Width = 440,
            });
        }

        Content = children;

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = (_nameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            args.Cancel = true;
            _nameBox.Focus(FocusState.Programmatic);
            return;
        }

        ProfileName = name;
    }

    private static string BuildSummary(RunContainerOptions o)
    {
        var lines = new List<string> { $"image: {o.Image}" };
        if (!string.IsNullOrWhiteSpace(o.Name))
        {
            lines.Add($"name: {o.Name}");
        }

        if (!string.IsNullOrWhiteSpace(o.Network))
        {
            lines.Add($"network: {o.Network}");
        }

        foreach (var p in o.PortMappings)
        {
            lines.Add($"port: {p}");
        }

        foreach (var e in o.EnvironmentVariables)
        {
            lines.Add($"env: {e}");
        }

        foreach (var volume in o.Volumes)
        {
            lines.Add($"mount: {volume}");
        }

        if (!string.IsNullOrWhiteSpace(o.WorkingDir))
        {
            lines.Add($"workdir: {o.WorkingDir}");
        }

        if (!string.IsNullOrWhiteSpace(o.User))
        {
            lines.Add($"user: {o.User}");
        }

        if (!string.IsNullOrWhiteSpace(o.Command))
        {
            lines.Add($"command: {o.Command}");
        }

        if (!string.IsNullOrWhiteSpace(o.Entrypoint))
        {
            lines.Add($"entrypoint: {o.Entrypoint}");
        }

        return string.Join('\n', lines);
    }
}
