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

using WslContainerDesktop.Services;

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

    /// <summary>Creates the dialog used to name and review a run profile before it is saved.</summary>
    public SaveRunProfileDialog(string suggestedName, RunContainerOptions options, IReadOnlyList<string> notCaptured,
        IReadOnlyList<string>? warnings = null)
    {
        Title = UiText.Get("Workload_Text_Save_as_run_profile_e6193b", "Save as run profile");
        PrimaryButtonText = UiText.Get("Workload_Text_Save_1509f5", "Save");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = warnings is { Count: > 0 } ? ContentDialogButton.Close : ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(this, "SaveRunProfileDialog");

        Resources["ContentDialogMaxWidth"] = 640.0;
        Resources["ContentDialogMinWidth"] = 480.0;

        _nameBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Profile_name_d36632", "Profile name"),
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
                    Text = UiText.Get("Workload_Text_Captured_settings_0eb697", "Captured settings"),
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
                Text = UiText.Get("Workload_Text_Review_storage_before_saving_4c4ee4", "Review storage before saving:\n\n") + string.Join("\n\n", warnings.Distinct(StringComparer.Ordinal).Select(UiText.TranslateLines))
                     + UiText.Get("Workload_Text_Cancel_to_keep_profiles_unchanged_or_64ca1c", "\n\nCancel to keep profiles unchanged, or save and review storage after loading the profile."),
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
                Text = UiText.Get("Workload_Text_Not_captured_a_running_container_doesn_868de2", "Not captured (a running container doesn't expose these): ")
                     + string.Join(", ", notCaptured)
                     + UiText.Get("Workload_Text_Add_them_after_loading_the_profile_e0266e", ". Add them after loading the profile if needed."),
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
        var lines = new List<string> { UiText.Get("Workload_Text_image_0_13f3da", "image: {0}", o.Image) };
        if (!string.IsNullOrWhiteSpace(o.Name))
        {
            lines.Add(UiText.Get("Workload_Text_name_0_f86121", "name: {0}", o.Name));
        }

        if (!string.IsNullOrWhiteSpace(o.Network))
        {
            lines.Add(UiText.Get("Workload_Text_network_0_4deab9", "network: {0}", o.Network));
        }

        foreach (var p in o.PortMappings)
        {
            lines.Add(UiText.Get("Workload_Text_port_0_f21231", "port: {0}", p));
        }

        foreach (var e in o.EnvironmentVariables)
        {
            lines.Add(UiText.Get("Workload_Text_env_0_48f549", "env: {0}", e));
        }

        foreach (var volume in o.Volumes)
        {
            lines.Add(UiText.Get("Workload_Text_mount_0_0f247a", "mount: {0}", volume));
        }

        foreach (var mount in o.Mounts)
        {
            lines.Add(UiText.Get("Workload_Text_mount_0_0f247a", "mount: {0}", mount.ToArgument()));
        }

        if (o.StopTimeoutSeconds is int stopTimeout)
        {
            lines.Add(UiText.Get("Workload_Text_stop_timeout_0_5571f3", "stop timeout: {0}", stopTimeout));
        }

        if (!string.IsNullOrWhiteSpace(o.WorkingDir))
        {
            lines.Add(UiText.Get("Workload_Text_workdir_0_95c98f", "workdir: {0}", o.WorkingDir));
        }

        if (!string.IsNullOrWhiteSpace(o.User))
        {
            lines.Add(UiText.Get("Workload_Text_user_0_b84b5c", "user: {0}", o.User));
        }

        if (!string.IsNullOrWhiteSpace(o.Command))
        {
            lines.Add(UiText.Get("Workload_Text_command_0_d5f841", "command: {0}", o.Command));
        }

        if (!string.IsNullOrWhiteSpace(o.Entrypoint))
        {
            lines.Add(UiText.Get("Workload_Text_entrypoint_0_a89ece", "entrypoint: {0}", o.Entrypoint));
        }

        return string.Join('\n', lines);
    }
}
