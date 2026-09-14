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

public sealed class AssistantActionGate(ISettingsService settings) : IAssistantActionGate
{
    /// <summary>
    /// Tools that destroy state the app cannot bring back. <c>delete_resource</c> is included
    /// explicitly: it is categorized as Kubernetes work, but deleting a cluster resource is as
    /// unrecoverable as removing a volume.
    /// </summary>
    private static readonly HashSet<string> AlwaysDestructiveTools =
        new(["delete_resource"], StringComparer.Ordinal);

    public AssistantActionRisk Classify(AssistantPermissionCategory category) => category switch
    {
        AssistantPermissionCategory.ReadOnly => AssistantActionRisk.ReadOnly,
        AssistantPermissionCategory.Destructive or AssistantPermissionCategory.ContainerExec => AssistantActionRisk.HighRisk,
        _ => AssistantActionRisk.StateChanging,
    };

    public bool IsDestructive(string toolName, AssistantPermissionCategory category) =>
        category is AssistantPermissionCategory.Destructive or AssistantPermissionCategory.ContainerExec
        || (!string.IsNullOrWhiteSpace(toolName) && AlwaysDestructiveTools.Contains(toolName));

    /// <summary>
    /// Whether the assistant may attempt this action at all. Destructive actions are a capability
    /// the user grants in Settings, deliberately separate from approving one: an approval prompt is
    /// answered in the flow of a conversation and is easy to accept by reflex, so it is the wrong
    /// place to decide whether an agent may delete things in the first place.
    /// </summary>
    public bool IsPermitted(string toolName, AssistantPermissionCategory category) =>
        !IsDestructive(toolName, category) || settings.AiAssistantAllowDestructive;

    public bool RequiresApproval(string toolName, AssistantPermissionCategory category) =>
        RequiresApproval(toolName, category, requiresExplicitApproval: false);

    /// <summary>
    /// Single place the approval policy is decided.
    /// </summary>
    /// <param name="requiresExplicitApproval">
    /// Set by tools whose consequences cannot be summarized by a tool name alone — Compose
    /// deployment, which applies model-authored multi-service YAML including ports, mounts and
    /// volumes. These ignore per-tool auto-approve, because approving "deploy a Compose stack" once
    /// is not informed consent for every future stack.
    /// </param>
    public bool RequiresApproval(string toolName, AssistantPermissionCategory category, bool requiresExplicitApproval)
    {
        // Read-only tools never prompt.
        if (category == AssistantPermissionCategory.ReadOnly)
        {
            return false;
        }

        // Opting out of approvals entirely is a single deliberate choice, so it also waives the
        // reviews that per-tool toggles cannot. It waives the prompt only: blocked plans stay
        // blocked, and every action is still recorded in the activity timeline.
        if (settings.AiAssistantApproveEverything)
        {
            return false;
        }

        return requiresExplicitApproval || !settings.IsAssistantToolAutoApproved(toolName);
    }
}
