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
public sealed record AssistantToolInfo(string Name, string DisplayName);

/// <summary>A named group of related assistant tools for the permission settings UI.</summary>
public sealed record AssistantToolGroup(string Header, IReadOnlyList<AssistantToolInfo> Tools);

/// <summary>
/// The catalog of assistant tools that can change state and therefore support a per-tool
/// auto-approve toggle. Read-only tools are intentionally omitted (they always run without
/// approval). This is the single source of truth for the permission settings UI; keep it in
/// sync with the tools registered in <see cref="AssistantToolset"/>.
/// </summary>
public static class AssistantToolCatalog
{
    public static IReadOnlyList<AssistantToolGroup> Groups { get; } =
    [
        new("Images", [new("pull_image", "Pull images")]),
        new("Containers — create & run", [new("run_container", "Run a container")]),
        new("Containers — lifecycle",
        [
            new("start_container", "Start a container"),
            new("stop_container", "Stop a container"),
            new("restart_container", "Restart a container"),
            new("stop_all_containers", "Stop all / multiple containers"),
        ]),
        new("Containers — remove (destructive)",
        [
            new("remove_container", "Remove a container"),
            new("remove_all_containers", "Remove all / multiple containers"),
        ]),
        new("Volumes",
        [
            new("create_volume", "Create a volume"),
            new("remove_volume", "Remove a volume (destructive)"),
        ]),
        new("Networks",
        [
            new("create_network", "Create a network"),
            new("remove_network", "Remove a network (destructive)"),
        ]),
        new("Compose & templates",
        [
            new("deploy_compose", "Deploy a compose project"),
            new("deploy_template", "Deploy an app template"),
        ]),
        new("Kubernetes (k3s)",
        [
            new("apply_yaml", "Apply a manifest"),
            new("scale_deployment", "Scale a deployment"),
            new("restart_deployment", "Restart a deployment"),
            new("delete_resource", "Delete a resource (destructive)"),
            new("cluster_start", "Start the cluster"),
            new("cluster_stop", "Stop the cluster"),
        ]),
    ];

    // Legacy category -> tool names, used once to migrate the previous four coarse
    // auto-approve toggles into the per-tool model. Destructive removals were never
    // auto-approvable before, so they are not migrated.
    public static IReadOnlyList<string> CreateRunTools { get; } =
        ["pull_image", "run_container", "create_volume", "create_network"];

    public static IReadOnlyList<string> LifecycleTools { get; } =
        ["start_container", "stop_container", "restart_container", "stop_all_containers"];

    public static IReadOnlyList<string> ComposeTemplateTools { get; } =
        ["deploy_compose", "deploy_template"];

    public static IReadOnlyList<string> KubernetesTools { get; } =
        ["apply_yaml", "scale_deployment", "restart_deployment", "delete_resource", "cluster_start", "cluster_stop"];
}
