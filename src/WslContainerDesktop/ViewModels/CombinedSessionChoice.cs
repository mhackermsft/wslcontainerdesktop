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

/// <summary>A session generation selected for an explicit combined inventory read.</summary>
public sealed partial class CombinedSessionChoice(WslcSessionInfo session, bool selected, bool available) : ObservableObject
{
    /// <summary>The session generation reported by the engine.</summary>
    public WslcSessionInfo Session { get; } = session;
    /// <summary>The unmodified name reported by the engine.</summary>
    public string Name => Session.Name;

    /// <summary>Whether this session is included in the display filter.</summary>
    [ObservableProperty]
    private bool _isSelected = selected;

    /// <summary>Whether the session still appears in the latest discovery.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailabilityText))]
    private bool _isAvailable = available;

    /// <summary>Localized explanation when a selected session has disappeared.</summary>
    public string AvailabilityText => IsAvailable ? string.Empty :
        UiText.Get("CombinedSession_Unavailable", "Unavailable; refresh after the session returns.");

    /// <summary>Updates localized computed text without changing selection.</summary>
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(AvailabilityText));
}
