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

    public RegistryEntry? Registry { get; private set; }

    /// <summary>The password entered for an optional immediate login (not stored).</summary>
    public string? Password { get; private set; }

    /// <summary>Whether the user asked to log in now.</summary>
    public bool LoginNow { get; private set; }

    public AddRegistryDialog()
    {
        Title = "Add registry";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _nameBox = new TextBox
        {
            Header = "Display name",
            PlaceholderText = "e.g. Company ACR",
            MinWidth = 380,
        };

        _hostBox = new TextBox
        {
            Header = "Registry host",
            PlaceholderText = "e.g. myregistry.azurecr.io, ghcr.io",
        };

        _userBox = new TextBox
        {
            Header = "Username (optional)",
            PlaceholderText = "username or token name",
        };

        _passwordBox = new PasswordBox
        {
            Header = "Password / token (optional)",
            PlaceholderText = "used only to log in now",
        };

        _loginNow = new CheckBox
        {
            Content = "Log in to this registry now",
            IsChecked = false,
        };

        var hint = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = "Credentials are handed to the container engine (wslc login) and stored in its credential store — the app never saves your password.",
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
