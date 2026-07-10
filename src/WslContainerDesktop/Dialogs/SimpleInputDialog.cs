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

/// <summary>A minimal single-text-field prompt dialog.</summary>
public sealed class SimpleInputDialog : ContentDialog
{
    private readonly TextBox _textBox;

    public SimpleInputDialog(string title, string label, string placeholder)
    {
        Title = title;
        PrimaryButtonText = "OK";
        CloseButtonText = "Cancel";
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
    }

    public string Value
    {
        get => _textBox.Text;
        set => _textBox.Text = value;
    }
}
