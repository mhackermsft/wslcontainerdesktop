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
using WslContainerDesktop.Helpers;

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Dialogs;

/// <summary>Collects username + password to log in to an already-registered registry.</summary>
public sealed class RegistryLoginDialog : ContentDialog
{
    private readonly TextBox _userBox;
    private readonly PasswordBox _passwordBox;
    private readonly bool _dockerHub;
    private readonly string _registryName;
    private readonly TextBlock? _hint;
    private WslcSessionSelection? _session;
    private bool _foldingCase;
    private bool _isComposing;

    /// <summary>Gets or sets the username.</summary>
    public string Username { get; private set; } = string.Empty;

    /// <summary>Gets or sets the password.</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>Creates a new &lt;c&gt;RegistryLoginDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="registryName">The registry name value supplied by the caller.</param>
    /// <param name="prefillUsername">The prefill username value supplied by the caller.</param>
    /// <param name="dockerHub">Whether to normalize usernames for Docker Hub.</param>
    public RegistryLoginDialog(string registryName, string? prefillUsername, bool dockerHub = false)
    {
        _dockerHub = dockerHub;
        _registryName = registryName;
        Title = UiText.Get("Workload_Text_Log_in_to_0_78a87c", "Log in to {0}", registryName);
        PrimaryButtonText = UiText.Get("Workload_Text_Log_in_c18984", "Log in");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _userBox = new TextBox
        {
            Header = UiText.Get("Workload_Text_Username_e3b89e", "Username"),
            PlaceholderText = dockerHub
                ? UiText.Get("Workload_Text_Docker_ID_or_organization_namespace_a1c4e7", "Docker ID or organization namespace")
                : UiText.Get("Workload_Text_username_or_token_name_39f3c5", "username or token name"),
            Text = prefillUsername ?? string.Empty,
            MinWidth = 360,
        };

        _passwordBox = new PasswordBox
        {
            Header = UiText.Get("Workload_Text_Password_token_051a24", "Password / token"),
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            Children = { _userBox, _passwordBox },
        };

        if (_dockerHub)
        {
            _hint = new TextBlock
            {
                Text = UiText.Get(
                    "Workload_Text_Docker_Hub_login_hint_5d2b9f",
                    "Use your Docker ID for a password or personal access token, or your organization namespace for an organization access token. Put the token in the password field."),
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Insert(1, _hint);
        }

        Content = panel;
        _userBox.TextCompositionStarted += (_, _) => _isComposing = true;
        _userBox.TextCompositionEnded += (_, _) =>
        {
            _isComposing = false;
            FoldAsciiCase();
        };
        _userBox.TextChanged += (_, _) => FoldAsciiCase();
        PrimaryButtonClick += OnPrimary;
        Opened += (_, _) => UiText.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => UiText.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Keeps session-specific login wording localized without changing credentials.</summary>
    public void SetSession(WslcSessionSelection session)
    {
        _session = session;
        RefreshLanguage();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLanguage();

    private void RefreshLanguage()
    {
        Title = _session is null ? UiText.Get("Workload_Text_Log_in_to_0_78a87c", "Log in to {0}", _registryName)
            : UiText.Get("Registry_LoginInSession", "Sign in to {0} using session {1}", _registryName,
                _session.Target.Name ?? UiText.Get("WslEngine_Session_Default", "Default CLI session"));
        PrimaryButtonText = UiText.Get("Workload_Text_Log_in_c18984", "Log in");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        _userBox.Header = UiText.Get("Workload_Text_Username_e3b89e", "Username");
        _userBox.PlaceholderText = _dockerHub
            ? UiText.Get("Workload_Text_Docker_ID_or_organization_namespace_a1c4e7", "Docker ID or organization namespace")
            : UiText.Get("Workload_Text_username_or_token_name_39f3c5", "username or token name");
        _passwordBox.Header = UiText.Get("Workload_Text_Password_token_051a24", "Password / token");
        if (_hint is not null)
            _hint.Text = UiText.Get("Workload_Text_Docker_Hub_login_hint_5d2b9f",
                "Use your Docker ID for a password or personal access token, or your organization namespace for an organization access token. Put the token in the password field.");
    }

    /// <summary>Shows ASCII case correction after input commits, preserving the selection and IME composition.</summary>
    private void FoldAsciiCase()
    {
        if (!_dockerHub || _foldingCase || _isComposing)
        {
            return;
        }

        var text = _userBox.Text;
        foreach (var c in text)
        {
            if (c > (char)0x7F)
            {
                return;
            }
        }

        var lowered = text.ToLowerInvariant();
        if (string.Equals(lowered, text, StringComparison.Ordinal))
        {
            return;
        }

        var selectionStart = _userBox.SelectionStart;
        var selectionLength = _userBox.SelectionLength;
        _foldingCase = true;
        try
        {
            _userBox.Text = lowered;
            selectionStart = Math.Min(selectionStart, lowered.Length);
            _userBox.Select(selectionStart, Math.Min(selectionLength, lowered.Length - selectionStart));
        }
        finally
        {
            _foldingCase = false;
        }
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Submit also covers prefilled values and full-width input that the live ASCII fold skips.
        Username = RegistryUsername.Normalize(_userBox.Text, _dockerHub);
        if (!string.Equals(Username, _userBox.Text, StringComparison.Ordinal))
        {
            _userBox.Text = Username;
        }
        Password = _passwordBox.Password;

        if (string.IsNullOrEmpty(Password))
        {
            args.Cancel = true;
            _passwordBox.Focus(FocusState.Programmatic);
        }
    }
}
