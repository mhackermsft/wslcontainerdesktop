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

namespace WslContainerDesktop.Dialogs;

/// <summary>
/// Lets the user upgrade (or reinstall) k3s to the latest stable channel or a specific
/// pinned version. <see cref="TargetVersion"/> is null when "latest stable" is chosen.
/// </summary>
public sealed class UpgradeK3sDialog : ContentDialog
{
    private readonly RadioButton _latestRadio;
    private readonly RadioButton _specificRadio;
    private readonly TextBox _versionBox;

    /// <summary>The chosen version tag, or null for "latest stable".</summary>
    public string? TargetVersion { get; private set; }

    public UpgradeK3sDialog(string currentVersion, string? latestVersion)
    {
        Title = "Upgrade Kubernetes (k3s)";
        PrimaryButtonText = "Upgrade";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var latestKnown = !string.IsNullOrWhiteSpace(latestVersion);
        var upToDate = latestKnown &&
            string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);

        var current = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Installed version: {currentVersion}",
        };

        var latestText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Text = latestKnown
                ? (upToDate
                    ? $"Latest stable: {latestVersion} — you're up to date. Re-running will reinstall the same version."
                    : $"Latest stable: {latestVersion} — an upgrade is available.")
                : "Latest stable: could not be determined (no network?). You can still pin a specific version.",
        };

        _latestRadio = new RadioButton
        {
            GroupName = "k3sUpgrade",
            IsChecked = true,
            Content = latestKnown ? $"Latest stable ({latestVersion})" : "Latest stable",
        };

        _specificRadio = new RadioButton
        {
            GroupName = "k3sUpgrade",
            Content = "Specific version",
        };

        _versionBox = new TextBox
        {
            PlaceholderText = "e.g. v1.36.2+k3s1",
            IsEnabled = false,
            Margin = new Thickness(28, 0, 0, 0),
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _latestRadio.Checked += (_, _) => _versionBox.IsEnabled = false;
        _specificRadio.Checked += (_, _) =>
        {
            _versionBox.IsEnabled = true;
            _versionBox.Focus(FocusState.Programmatic);
        };

        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = "The upgrade re-runs the k3s install script in place. Your cluster data and workloads are preserved; the service restarts briefly.",
        };

        Content = new StackPanel
        {
            Spacing = 10,
            MinWidth = 380,
            Children = { current, latestText, _latestRadio, _specificRadio, _versionBox, hint },
        };

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_specificRadio.IsChecked == true)
        {
            var v = _versionBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(v))
            {
                args.Cancel = true;
                return;
            }

            // Be forgiving: accept "1.36.2+k3s1" and normalize to a leading "v".
            TargetVersion = v.StartsWith('v') ? v : "v" + v;
        }
        else
        {
            TargetVersion = null;
        }
    }
}
