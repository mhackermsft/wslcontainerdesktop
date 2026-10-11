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

namespace WslContainerDesktop.Services;

/// <summary>A single state-changing assistant tool as shown in the permission settings.</summary>
public sealed record AssistantToolInfo(string Name, string DisplayName, bool IsDestructive = false);

/// <summary>A named group of related assistant tools for the permission settings UI.</summary>
public sealed record AssistantToolGroup(string Header, IReadOnlyList<AssistantToolInfo> Tools);

/// <summary>
/// The catalog of assistant tools that can change state and therefore support a per-tool
/// auto-approve toggle. Read-only tools are intentionally omitted (they always run without
/// approval). Compose deployment always requires an explicit consequence review, so it is
/// omitted here; template auto-approval applies only to single-container templates.
/// This is the single source of truth for the permission settings UI; keep it in
/// sync with the tools registered in <see cref="AssistantToolset"/>.
/// </summary>
public static class AssistantToolCatalog
{
    /// <summary>
    /// Tool groups displayed in AI permission settings, including which actions are destructive.
    /// </summary>
    public static IReadOnlyList<AssistantToolGroup> Groups { get; } =
    [
        new("Images", [new("pull_image", UiText.Get("Common_Text0487", "Pull images"))]),
        new(UiText.Get("Common_Text0488", "Containers — create & run"), [new("run_container", UiText.Get("Common_Text0489", "Run a container"))]),
        new(UiText.Get("Common_Text0490", "Containers — lifecycle"),
        [
            new("start_container", UiText.Get("Common_Text0491", "Start a container")),
            new("stop_container", UiText.Get("Common_Text0492", "Stop a container")),
            new("restart_container", UiText.Get("Common_Text0493", "Restart a container")),
            new("stop_all_containers", UiText.Get("Common_Text0494", "Stop all / multiple containers")),
        ]),
        new(UiText.Get("Common_Text0495", "Containers — remove (destructive)"),
        [
            new("remove_container", UiText.Get("Common_Text0496", "Remove a container"), IsDestructive: true),
            new("remove_all_containers", UiText.Get("Common_Text0497", "Remove all / multiple containers"), IsDestructive: true),
        ]),
        new("Volumes",
        [
            new("create_volume", UiText.Get("Common_Text0498", "Create a volume")),
            new("remove_volume", UiText.Get("Common_Text0499", "Remove a volume — deletes its data"), IsDestructive: true),
        ]),
        new("Networks",
        [
            new("create_network", UiText.Get("Common_Text0500", "Create a network")),
            new("remove_network", UiText.Get("Common_Text0501", "Remove a network"), IsDestructive: true),
        ]),
        new("Templates",
        [
            new("deploy_template", UiText.Get("Common_Text0502", "Deploy a single-container template (Compose always requires review)")),
        ]),
        new("Kubernetes (k3s)",
        [
            new("apply_yaml", UiText.Get("Common_Text0504", "Apply a manifest")),
            new("scale_deployment", UiText.Get("Common_Text0505", "Scale a deployment")),
            new("restart_deployment", UiText.Get("Common_Text0506", "Restart a deployment")),
            new("delete_resource", UiText.Get("Common_Text0507", "Delete a resource"), IsDestructive: true),
            new("cluster_start", UiText.Get("Common_Text0508", "Start the cluster")),
            new("cluster_stop", UiText.Get("Common_Text0509", "Stop the cluster")),
        ]),
    ];

    // Legacy category -> tool names, used once to migrate the previous four coarse
    // auto-approve toggles into the per-tool model. Destructive removals were never
    // auto-approvable before, so they are not migrated.
    /// <summary>Legacy coarse toggle entries that now map to create and run tool names.</summary>
    public static IReadOnlyList<string> CreateRunTools { get; } =
        ["pull_image", "run_container", "create_volume", "create_network"];

    /// <summary>Legacy coarse toggle entries that now map to container lifecycle tool names.</summary>
    public static IReadOnlyList<string> LifecycleTools { get; } =
        ["start_container", "stop_container", "restart_container", "stop_all_containers"];

    /// <summary>Legacy coarse toggle entries that now map to compose and template deployment tool names.</summary>
    public static IReadOnlyList<string> ComposeTemplateTools { get; } =
        ["deploy_compose", "deploy_template"];

    /// <summary>Legacy coarse toggle entries that now map to Kubernetes mutation tool names.</summary>
    public static IReadOnlyList<string> KubernetesTools { get; } =
        ["apply_yaml", "scale_deployment", "restart_deployment", "delete_resource", "cluster_start", "cluster_stop"];
}
