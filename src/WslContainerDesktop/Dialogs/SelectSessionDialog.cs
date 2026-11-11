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

/// <summary>Requires an explicit session choice for a new workload.</summary>
public sealed class SelectSessionDialog : ContentDialog
{
    private readonly ComboBox _sessions;
    private readonly TextBlock _explanation;

    /// <summary>The selected session, or null until one is chosen.</summary>
    public WslcSessionSelection? Selection => _sessions.SelectedItem as WslcSessionSelection;

    /// <summary>Creates a session chooser without changing the app's current session.</summary>
    public SelectSessionDialog(IReadOnlyList<WslcSessionSelection> sessions)
    {
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.Primary;

        _sessions = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 340,
            DisplayMemberPath = nameof(WslcSessionSelection.DisplayName),
            ItemsSource = sessions,
        };
        _sessions.SelectionChanged += (_, _) => IsPrimaryButtonEnabled = Selection is not null;
        _explanation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _explanation,
                _sessions,
            },
        };
        RefreshLanguage();
        Opened += (_, _) => UiText.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => UiText.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLanguage();

    private void RefreshLanguage()
    {
        Title = UiText.Get("SessionPicker_Title", "Choose a session");
        PrimaryButtonText = UiText.Get("SessionPicker_Continue", "Continue");
        CloseButtonText = UiText.Get("Common_Text0094", "Cancel");
        _sessions.Header = UiText.Get("SessionPicker_Field", "Session");
        _sessions.PlaceholderText = UiText.Get("SessionPicker_Placeholder", "Choose a session");
        _explanation.Text = UiText.Get("SessionPicker_Explanation",
            "Images and storage belong to their session. Saved projects and workspaces remember their session.");
        if (_sessions.ItemsSource is IReadOnlyList<WslcSessionSelection> choices)
        {
            var selected = Selection;
            var updated = choices.Select(choice => choice.Target.Name is null
                ? choice with { DisplayName = UiText.Get("WslEngine_Session_Default", "Default CLI session") }
                : choice).ToArray();
            _sessions.ItemsSource = updated;
            _sessions.SelectedItem = selected is null ? null : updated.First(choice => choice.Target == selected.Target);
        }
    }
}
