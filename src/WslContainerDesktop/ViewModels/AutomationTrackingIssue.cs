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

/// <summary>One unavailable owner whose automatic-start tracking can be forgotten explicitly.</summary>
public sealed class AutomationTrackingIssue(AutomationSessionOwner owner) : ObservableObject
{
    public AutomationSessionOwner Owner { get; } = owner;
    public string SessionLabel => Owner.SessionName ?? UiText.Get("WslEngine_Session_Default", "Default CLI session");
    public string DisplayName => UiText.Get("Automation_TrackingOwner", "{0} ({1})", SessionLabel, Owner.ExecutablePath);
    public string ForgetName => UiText.Get("Automation_ForgetTrackingName",
        "Forget automatic-start tracking for {0}", DisplayName);
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(SessionLabel));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ForgetName));
    }
}
