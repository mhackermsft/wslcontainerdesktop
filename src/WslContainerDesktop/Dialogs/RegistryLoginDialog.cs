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

/// <summary>Collects username + password to log in to an already-registered registry.</summary>
public sealed class RegistryLoginDialog : ContentDialog
{
    private readonly TextBox _userBox;
    private readonly PasswordBox _passwordBox;

    /// <summary>Gets or sets the username.</summary>
    public string Username { get; private set; } = string.Empty;

    /// <summary>Gets or sets the password.</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>Creates a new &lt;c&gt;RegistryLoginDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="registryName">The registry name value supplied by the caller.</param>
    /// <param name="prefillUsername">The prefill username value supplied by the caller.</param>
    public RegistryLoginDialog(string registryName, string? prefillUsername)
    {
        Title = UiText.Get("Workload_Text_Log_in_to_0_78a87c", "Log in to {0}", registryName);
        PrimaryButtonText = UiText.Get("Workload_Text_Log_in_c18984", "Log in");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _userBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Username_e3b89e", "Username"),
            PlaceholderText = UiText.Get("Workload_Text_username_or_token_name_39f3c5", "username or token name"),
            Text = prefillUsername ?? string.Empty,
            MinWidth = 360,
        };

        _passwordBox = new PasswordBox
        {
            Header = UiText.Get("Workload_Text_Password_token_051a24", "Password / token"),
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children = { _userBox, _passwordBox },
        };

        PrimaryButtonClick += OnPrimary;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Username = _userBox.Text.Trim();
        Password = _passwordBox.Password;

        if (string.IsNullOrEmpty(Password))
        {
            args.Cancel = true;
            _passwordBox.Focus(FocusState.Programmatic);
        }
    }
}
