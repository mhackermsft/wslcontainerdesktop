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

using WslContainerDesktop.Services;

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

    /// <summary>Creates a new &lt;c&gt;UpgradeK3sDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="currentVersion">The current version value supplied by the caller.</param>
    /// <param name="latestVersion">The latest version value supplied by the caller.</param>
    public UpgradeK3sDialog(string currentVersion, string? latestVersion)
    {
        Title = UiText.Get("Workload_Text_Upgrade_Kubernetes_k3s_18fbae", "Upgrade Kubernetes (k3s)");
        PrimaryButtonText = UiText.Get("Workload_Text_Upgrade_7ec026", "Upgrade");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        var latestKnown = !string.IsNullOrWhiteSpace(latestVersion);
        var upToDate = latestKnown &&
            string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);

        var current = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = UiText.Get("Workload_Text_Installed_version_0_15ee25", "Installed version: {0}", currentVersion),
        };

        var latestText = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            Text = latestKnown
                ? (upToDate
                    ? UiText.Get("Workload_Text_Latest_stable_0_you_re_up_5dbd92", "Latest stable: {0} — you're up to date. Re-running will reinstall the same version.", latestVersion)
                    : UiText.Get("Workload_Text_Latest_stable_0_an_upgrade_is_b786cd", "Latest stable: {0} — an upgrade is available.", latestVersion))
                : UiText.Get("Workload_Text_Latest_stable_could_not_be_determined_2b934d", "Latest stable: could not be determined (no network?). You can still pin a specific version."),
        };

        _latestRadio = new RadioButton
        {
            GroupName = "k3sUpgrade",
            IsChecked = true,
            Content = latestKnown ? UiText.Get("Workload_Text_Latest_stable_0_19df99", "Latest stable ({0})", latestVersion) : UiText.Get("Workload_Text_Latest_stable_ad00d5", "Latest stable"),
        };

        _specificRadio = new RadioButton
        {
            GroupName = "k3sUpgrade",
            Content = UiText.Get("Workload_Text_Specific_version_c36f2c", "Specific version"),
        };

        _versionBox = new TextBox
        {
            PlaceholderText = UiText.Get("Workload_Text_e_g_v1_36_2_k3s1_f05394", "e.g. v1.36.2+k3s1"),
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
            Text = UiText.Get("Workload_Text_The_upgrade_re_runs_the_k3s_f22876", "The upgrade re-runs the k3s install script in place. Your cluster data and workloads are preserved; the service restarts briefly."),
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
