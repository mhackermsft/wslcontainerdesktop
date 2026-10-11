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
/// Collects a registry definition and optional credentials. The password is exposed only
/// transiently via <see cref="Password"/> so the caller can run `wslc login`; it is never
/// persisted by the app.
/// </summary>
public sealed class AddRegistryDialog : ContentDialog
{
    private readonly TextBox _nameBox;
    private readonly TextBox _hostBox;
    private readonly TextBox _userBox;
    private readonly PasswordBox _passwordBox;
    private readonly CheckBox _loginNow;

    /// <summary>Gets or sets the registry.</summary>
    public RegistryEntry? Registry { get; private set; }

    /// <summary>The password entered for an optional immediate login (not stored).</summary>
    public string? Password { get; private set; }

    /// <summary>Whether the user asked to log in now.</summary>
    public bool LoginNow { get; private set; }

    /// <summary>Creates a new &lt;c&gt;AddRegistryDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    public AddRegistryDialog()
    {
        Title = UiText.Get("Workload_Text_Add_registry_503bed", "Add registry");
        PrimaryButtonText = UiText.Get("Workload_Text_Save_1509f5", "Save");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _nameBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Display_name_2b7f6a", "Display name"),
            PlaceholderText = UiText.Get("Workload_Text_e_g_Company_ACR_68810c", "e.g. Company ACR"),
            MinWidth = 380,
        };

        _hostBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Registry_host_743f8e", "Registry host"),
            PlaceholderText = UiText.Get("Workload_Text_e_g_myregistry_azurecr_io_ghcr_6930ef", "e.g. myregistry.azurecr.io, ghcr.io"),
        };

        _userBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Username_optional_e946bd", "Username (optional)"),
            PlaceholderText = UiText.Get("Workload_Text_username_or_token_name_39f3c5", "username or token name"),
        };

        _passwordBox = new PasswordBox
        {
            Header = UiText.Get("Workload_Text_Password_token_optional_5caf4d", "Password / token (optional)"),
            PlaceholderText = UiText.Get("Workload_Text_used_only_to_log_in_now_fe26ce", "used only to log in now"),
        };

        _loginNow = new CheckBox
        {
            Content = UiText.Get("Workload_Text_Log_in_to_this_registry_now_510d38", "Log in to this registry now"),
            IsChecked = false,
        };

        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = UiText.Get("Workload_Text_Credentials_are_handed_to_the_container_c9f29e", "Credentials are handed to the container engine (wslc login) and stored in its credential store — the app never saves your password."),
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _nameBox, _hostBox, _userBox, _passwordBox, _loginNow, hint },
        };

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var host = _hostBox.Text.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(host))
        {
            args.Cancel = true;
            _hostBox.Focus(FocusState.Programmatic);
            return;
        }

        var name = string.IsNullOrWhiteSpace(_nameBox.Text) ? host : _nameBox.Text.Trim();
        var user = string.IsNullOrWhiteSpace(_userBox.Text) ? null : _userBox.Text.Trim();

        Registry = new RegistryEntry
        {
            Name = name,
            Host = host,
            Username = user,
        };

        LoginNow = _loginNow.IsChecked == true;
        Password = _passwordBox.Password;

        // If the user asked to log in, require a password.
        if (LoginNow && string.IsNullOrEmpty(Password))
        {
            args.Cancel = true;
            _passwordBox.Focus(FocusState.Programmatic);
        }
    }
}
