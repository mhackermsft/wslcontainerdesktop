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
/// Collects a build context, image name, and optional Dockerfile for `wslc build`. Builds stay on
/// this PC by default; optionally, a configured registry's host is prefixed to the name so the image
/// is ready to push (Push can also add it later).
/// </summary>
public sealed class BuildImageDialog : ContentDialog
{
    private static string LocalOnly => UiText.Get("Workload_Text_None_keep_it_on_this_PC_3f35de", "None — keep it on this PC");

    private readonly ComboBox _registryBox;
    private readonly TextBox _contextBox;
    private readonly TextBox _tagBox;
    private readonly TextBox _dockerfileBox;
    private readonly TextBlock _preview;

    // Only registries with a host change the name; Docker Hub (no host) would be a no-op choice.
    private readonly IReadOnlyList<RegistryEntry> _hostRegistries;

    /// <summary>Gets the context path.</summary>
    public string ContextPath => _contextBox.Text.Trim();

    /// <summary>The image name, prefixed with the chosen registry's host when one is selected.</summary>
    public string ImageTag => SelectedRegistry?.Qualify(_tagBox.Text.Trim()) ?? _tagBox.Text.Trim();

    /// <summary>Gets the dockerfile.</summary>
    public string? Dockerfile =>
        string.IsNullOrWhiteSpace(_dockerfileBox.Text) ? null : _dockerfileBox.Text.Trim();

    /// <summary>Creates a new &lt;c&gt;BuildImageDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="registries">The registries value supplied by the caller.</param>
    public BuildImageDialog(IReadOnlyList<RegistryEntry> registries)
    {
        _hostRegistries = registries.Where(r => r.HasHost).ToList();

        Title = UiText.Get("Workload_Text_Build_image_327d5c", "Build image");
        PrimaryButtonText = UiText.Get("Workload_Text_Build_bdd254", "Build");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _contextBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Folder_to_build_contains_your_Dockerfile_06d592", "Folder to build (contains your Dockerfile)"),
            PlaceholderText = @"C:\src\myapp",
            MinWidth = 420,
        };

        _dockerfileBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Dockerfile_optional_relative_to_the_folder_6c47c0", "Dockerfile (optional, relative to the folder)"),
            PlaceholderText = "Dockerfile",
        };

        _tagBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Image_name_52493e", "Image name"),
            PlaceholderText = "myapp:latest",
        };
        _tagBox.TextChanged += (_, _) => { _tagBox.Description = null; UpdatePreview(); };
        _contextBox.TextChanged += (_, _) => _contextBox.Description = null;

        _registryBox = new ComboBox
        {
            Header = UiText.Get("Workload_Text_Prepare_for_pushing_to_a_registry_69afc1", "Prepare for pushing to a registry (optional)"),
            MinWidth = 420,
            Visibility = _hostRegistries.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        _registryBox.Items.Add(LocalOnly);
        foreach (var r in _hostRegistries)
        {
            _registryBox.Items.Add($"{r.Name} ({r.Host})");
        }

        _registryBox.SelectedIndex = 0;
        _registryBox.SelectionChanged += (_, _) => UpdatePreview();

        _preview = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _contextBox, _dockerfileBox, _tagBox, _registryBox, _preview },
        };

        UpdatePreview();
        _dockerfileBox.TextChanged += (_, _) => _dockerfileBox.Description = null;
        PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(ContextPath))
            {
                args.Cancel = true;
                _contextBox.Description = UiText.Get("Workload_Text_Enter_the_folder_that_contains_your_0376dd", "Enter the folder that contains your Dockerfile.");
                _contextBox.Focus(FocusState.Programmatic);
            }
            else if (string.IsNullOrWhiteSpace(_tagBox.Text))
            {
                args.Cancel = true;
                _tagBox.Description = UiText.Get("Workload_Text_Enter_a_name_for_the_image_cd0692", "Enter a name for the image, for example myapp:latest.");
                _tagBox.Focus(FocusState.Programmatic);
            }
            else if (Services.WslcService.IsStdinDockerfile(Dockerfile))
            {
                args.Cancel = true;
                _dockerfileBox.Description = Services.WslcService.StdinDockerfileError;
                _dockerfileBox.Focus(FocusState.Programmatic);
            }
        };
    }

    private RegistryEntry? SelectedRegistry =>
        _registryBox.SelectedIndex > 0 && _registryBox.SelectedIndex <= _hostRegistries.Count
            ? _hostRegistries[_registryBox.SelectedIndex - 1]
            : null;

    private void UpdatePreview()
    {
        var tag = (_tagBox?.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(tag))
        {
            _preview.Text = UiText.Get("Workload_Text_The_image_is_built_and_kept_ee8e62", "The image is built and kept on this PC. Nothing is uploaded.");
            return;
        }

        _preview.Text = SelectedRegistry is null
            ? UiText.Get("Workload_Text_Builds_0_on_this_PC_Nothing_c00a00", "Builds {0} on this PC. Nothing is uploaded; use Push later if you want to share it.", tag)
            : UiText.Get("Workload_Text_Builds_0_on_this_PC_named_748512", "Builds {0} on this PC, named so it is ready to push. Nothing is uploaded until you use Push.", ImageTag);
    }
}
