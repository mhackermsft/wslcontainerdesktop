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

/// <summary>A minimal single-text-field prompt dialog.</summary>
public sealed class SimpleInputDialog : ContentDialog
{
    private readonly TextBox _textBox;
    private WslcSessionSelection? _session;
    private string? _sessionTitleKey;
    private string _sessionTitleFallback = string.Empty;
    private string _labelKey = string.Empty;
    private string _labelFallback = string.Empty;
    private string _placeholderKey = string.Empty;
    private string _placeholderFallback = string.Empty;

    /// <summary>Creates a new &lt;c&gt;SimpleInputDialog&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    /// <param name="title">The title value supplied by the caller.</param>
    /// <param name="label">The label value supplied by the caller.</param>
    /// <param name="placeholder">The placeholder value supplied by the caller.</param>
    public SimpleInputDialog(string title, string label, string placeholder)
    {
        Title = title;
        PrimaryButtonText = UiText.Get("Workload_Text_OK_565339", "OK");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        _textBox = new TextBox
        {
            Header = label,
            PlaceholderText = placeholder,
            AcceptsReturn = false,
            MinWidth = 380,
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children = { _textBox },
        };

        Loaded += (_, _) =>
        {
            _textBox.Focus(FocusState.Programmatic);
            _textBox.SelectAll();
        };
        Opened += (_, _) => UiText.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => UiText.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Keeps session-specific wording localized without changing the entered value.</summary>
    public void SetSession(WslcSessionSelection session, string titleKey, string titleFallback,
        string labelKey, string labelFallback, string placeholderKey, string placeholderFallback)
    {
        _session = session;
        _sessionTitleKey = titleKey;
        _sessionTitleFallback = titleFallback;
        _labelKey = labelKey;
        _labelFallback = labelFallback;
        _placeholderKey = placeholderKey;
        _placeholderFallback = placeholderFallback;
        RefreshLanguage();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLanguage();

    private void RefreshLanguage()
    {
        if (_session is null || _sessionTitleKey is null) return;
        Title = UiText.Get(_sessionTitleKey, _sessionTitleFallback, _session.Target.Name ??
            UiText.Get("WslEngine_Session_Default", "Default CLI session"));
        PrimaryButtonText = UiText.Get("Workload_Text_OK_565339", "OK");
        CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel");
        _textBox.Header = UiText.Get(_labelKey, _labelFallback);
        _textBox.PlaceholderText = UiText.Get(_placeholderKey, _placeholderFallback);
    }

    /// <summary>Gets or sets the text currently entered in the dialog.</summary>
    public string Value
    {
        get => _textBox.Text;
        set => _textBox.Text = value;
    }
}
