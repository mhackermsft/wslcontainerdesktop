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

/// <summary>One app-created or externally created session in the session manager.</summary>
public sealed class SessionListItem(
    string name, ManagedSessionDefinition? definition, WslcSessionInfo? generation,
    bool ownedAndRunning, bool nameInUse, string? failure, bool isDefault,
    bool inventoryUnavailable = false, bool removing = false, bool defaultStoppedByUser = false) : ObservableObject
{
    /// <summary>The unchanged engine session name.</summary>
    public string Name { get; } = name;
    /// <summary>Saved app-created definition, when present.</summary>
    public ManagedSessionDefinition? Definition { get; } = definition;
    /// <summary>Observed active generation, when present.</summary>
    public WslcSessionInfo? Generation { get; } = generation;
    /// <summary>Whether this is the CLI's default session.</summary>
    public bool IsDefault { get; } = isDefault;
    /// <summary>Whether a saved session may be started.</summary>
    public bool CanStart => Definition is { PendingDeletion: false } && !ownedAndRunning && !nameInUse &&
        !inventoryUnavailable;
    /// <summary>Whether an app-created session may be removed.</summary>
    public bool CanRemove => Definition is not null;
    /// <summary>Whether this active session can be stopped.</summary>
    public bool CanStop => !defaultStoppedByUser && Generation is not null && (Definition is null || ownedAndRunning);
    /// <summary>Whether the explicitly stopped default session can be started or resumed.</summary>
    public bool CanResumeDefault => IsDefault && defaultStoppedByUser;
    public string ResumeDefaultLabel => Generation is null
        ? UiText.Get("ManagedSession_StartLabel", "Start")
        : UiText.Get("ManagedSession_ResumeLabel", "Resume");
    public string ResumeDefaultName => UiText.Get("ManagedSession_ResumeDefaultName",
        "Start or resume the default CLI session");
    /// <summary>Accessible label for starting this saved session.</summary>
    public string StartName => UiText.Get("ManagedSession_StartName", "Start session {0}", DisplayName);
    /// <summary>Accessible label for removing this saved session.</summary>
    public string RemoveName => UiText.Get("ManagedSession_RemoveName", "Remove session {0}", DisplayName);
    /// <summary>Accessible label for stopping an externally created session.</summary>
    public string StopName => UiText.Get("ManagedSession_StopName", "Stop session {0}", DisplayName);
    /// <summary>Display name, keeping external session names unchanged.</summary>
    public string DisplayName => IsDefault ? UiText.Get("WslEngine_Session_Default", "Default CLI session") : Name;
    /// <summary>Localized ownership and availability description.</summary>
    public string StatusDisplay => defaultStoppedByUser
        ? inventoryUnavailable
            ? UiText.Get("ManagedSession_StatusUnavailable", "Session status is unavailable. Check the engine and refresh.")
        : Generation is null
            ? UiText.Get("ManagedSession_DefaultStoppedRow",
                "Stopped by you · data kept. Select Start to use it again.")
            : UiText.Get("ManagedSession_DefaultStartedElsewhere",
                "The session is active, but this app's operations and automatic actions are paused. Select Resume to continue.")
        : Definition is null
        ? UiText.Get("ManagedSession_ExternalStatus", "Created outside this app · active")
        : removing
            ? UiText.Get("ManagedSession_Removing", "Removing session {0}…", DisplayName)
        : Definition.PendingDeletion
            ? UiText.Get("ManagedSession_DeletePending", "Session removal was interrupted. Remove it again to finish or keep its data.")
            : !string.IsNullOrWhiteSpace(failure)
                ? UiText.Get("ManagedSession_Failure", "Could not start: {0}", failure)
                : inventoryUnavailable
                    ? UiText.Get("ManagedSession_StatusUnavailable", "Session status is unavailable. Check the engine and refresh.")
                : ownedAndRunning
                    ? UiText.Get("ManagedSession_OwnedActive", "Created by this app · active")
                    : nameInUse
                        ? UiText.Get("ManagedSession_NameInUse",
                            "Another process is already using this session name. Stop it before starting this saved session.")
                        : UiText.Get("ManagedSession_OwnedStopped", "Created by this app · stopped");

    /// <summary>Refreshes computed text without changing session identity.</summary>
    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
    public override string ToString() => DisplayName;
}
