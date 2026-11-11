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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>One saved automatic action waiting for an explicit session assignment.</summary>
public sealed class AutomationReviewItem : ObservableObject
{
    internal enum ItemKind { Autostart, Health, Restart, Compose }

    internal AutomationReviewItem(ItemKind kind, string key, string name, object value)
    {
        Kind = kind;
        Key = key;
        Name = name;
        Value = value;
    }

    internal ItemKind Kind { get; }
    internal string Key { get; }
    internal object Value { get; private set; }
    /// <summary>The unchanged saved container name or identifier.</summary>
    public string Name { get; }
    /// <summary>Localized description of the automatic action.</summary>
    public string KindText => Kind switch
    {
        ItemKind.Autostart => UiText.Get("Automation_KindAutostart", "Automatic start"),
        ItemKind.Health => UiText.Get("Automation_KindHealth", "Health check"),
        ItemKind.Restart => UiText.Get("Automation_KindRestart", "Restart policy"),
        ItemKind.Compose => UiText.Get("Automation_KindCompose", "Compose project"),
        _ => throw new InvalidOperationException("Unknown automatic-action type."),
    };

    /// <summary>Accessible description of the assignment button.</summary>
    public string AssignName => UiText.Get("Automation_AssignFor", "Assign {0} for {1}", KindText, Name);

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(AssignName));
    }

    internal void UpdateSource(object value) => Value = value;
}
