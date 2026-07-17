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

namespace WslContainerDesktop.ViewModels;

/// <summary>A per-tool auto-approve toggle shown in the AI assistant permission settings.</summary>
public partial class AssistantToolPermission : ObservableObject
{
    private readonly Action<string, bool> _onChanged;

    public AssistantToolPermission(string name, string displayName, bool autoApprove, Action<string, bool> onChanged)
    {
        Name = name;
        DisplayName = displayName;
        _autoApprove = autoApprove;
        _onChanged = onChanged;
    }

    public string Name { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _autoApprove;

    partial void OnAutoApproveChanged(bool value) => _onChanged(Name, value);
}

/// <summary>A named group of <see cref="AssistantToolPermission"/> toggles.</summary>
public sealed class AssistantToolPermissionGroup
{
    public required string Header { get; init; }

    public required IReadOnlyList<AssistantToolPermission> Tools { get; init; }
}
