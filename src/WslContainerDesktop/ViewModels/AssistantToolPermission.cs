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

using CommunityToolkit.Mvvm.ComponentModel;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>A per-tool auto-approve toggle shown in the AI assistant permission settings.</summary>
public partial class AssistantToolPermission : ObservableObject
{
    private readonly Action<string, bool> _onChanged;

    /// <summary>Creates a permission row and remembers how to persist changes.</summary>
    public AssistantToolPermission(string name, string displayName, bool autoApprove, Action<string, bool> onChanged)
    {
        Name = name;
        DisplayName = UiText.Translate(displayName);
        _autoApprove = autoApprove;
        _onChanged = onChanged;
    }

    /// <summary>Stable tool/action identifier used in settings.</summary>
    public string Name { get; }

    /// <summary>Human-readable label shown in the settings page.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Updates the label without changing the stored tool identifier or permission.</summary>
    public void RefreshLocalizedText()
    {
        DisplayName = UiText.Translate(DisplayName);
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>True when this action destroys state the app cannot restore.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>
    /// False when this row cannot currently govern anything — either every action is auto-approved,
    /// or this is a destructive action the assistant is not permitted to take at all. The stored
    /// preference is preserved either way, so it returns as it was when the capability comes back.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Generated setting bound to the toggle that decides whether safe actions may run without prompting.</summary>
    [ObservableProperty]
    private bool _autoApprove;

    partial void OnAutoApproveChanged(bool value) => _onChanged(Name, value);

    /// <summary>Returns the label so screen readers announce the tool name, not the type.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>A named group of <see cref="AssistantToolPermission"/> toggles.</summary>
public sealed class AssistantToolPermissionGroup : ObservableObject
{
    /// <summary>Section heading shown above a related group of tools.</summary>
    public required string Header { get; set; }

    /// <summary>Refreshes display labels while keeping the existing permission objects.</summary>
    public void RefreshLocalizedText()
    {
        Header = UiText.Translate(Header);
        OnPropertyChanged(nameof(Header));
        foreach (var tool in Tools)
            tool.RefreshLocalizedText();
    }

    /// <summary>The permission rows displayed under <see cref="Header"/>.</summary>
    public required IReadOnlyList<AssistantToolPermission> Tools { get; init; }

    /// <summary>Returns the heading so screen readers announce the group name, not the type.</summary>
    public override string ToString() => Header;
}
