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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Lets assistant actions confirm that a requested operation is allowed before mutating containers or settings.
/// </summary>
public interface IAssistantActionGate
{
    /// <summary>
    /// Maps an assistant action to the permission category shown to the user.
    /// </summary>
    AssistantActionRisk Classify(AssistantPermissionCategory category);

    /// <summary>
    /// Gets whether the assistant action category requires explicit user approval.
    /// </summary>
    bool RequiresApproval(string toolName, AssistantPermissionCategory category);

    /// <summary>
    /// Decides approval including tools that demand an explicit consequence review regardless of
    /// per-tool auto-approve. Implementations must keep this the single source of that policy.
    /// </summary>
    bool RequiresApproval(string toolName, AssistantPermissionCategory category, bool requiresExplicitApproval);

    /// <summary>Whether this action destroys state the app cannot restore.</summary>
    bool IsDestructive(string toolName, AssistantPermissionCategory category);

    /// <summary>Whether the assistant is permitted to attempt this action at all.</summary>
    bool IsPermitted(string toolName, AssistantPermissionCategory category);
}
