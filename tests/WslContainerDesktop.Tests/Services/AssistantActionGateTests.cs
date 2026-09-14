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
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// What the assistant may do out of the box, and what only a deliberate visit to Settings unlocks.
/// These are safety defaults, so they are pinned rather than left to inspection: a future edit that
/// quietly lets a fresh install delete volumes should fail the build.
/// </summary>
public class AssistantActionGateTests
{
    private sealed class Options
    {
        public bool AllowDestructive { get; init; }
        public bool ApproveEverything { get; init; }
        public HashSet<string> AutoApproved { get; init; } = new(StringComparer.Ordinal);
    }

    private static AssistantActionGate Gate(Options? options = null)
    {
        var o = options ?? new Options();
        return new AssistantActionGate(NetworkTestProxy.Create<ISettingsService>((method, args) => method.Name switch
        {
            "get_AiAssistantAllowDestructive" => o.AllowDestructive,
            "get_AiAssistantApproveEverything" => o.ApproveEverything,
            nameof(ISettingsService.IsAssistantToolAutoApproved) => o.AutoApproved.Contains((string)args[0]!),
            _ => throw new InvalidOperationException($"Unexpected settings call: {method.Name}"),
        }));
    }

    public static IEnumerable<object[]> DestructiveActions() =>
    [
        ["remove_container", AssistantPermissionCategory.Destructive],
        ["remove_all_containers", AssistantPermissionCategory.Destructive],
        ["remove_volume", AssistantPermissionCategory.Destructive],
        ["remove_network", AssistantPermissionCategory.Destructive],
        // Categorized as Kubernetes work, but deleting a cluster resource is just as unrecoverable.
        ["delete_resource", AssistantPermissionCategory.Kubernetes],
    ];

    public static IEnumerable<object[]> NonDestructiveActions() =>
    [
        ["pull_image", AssistantPermissionCategory.CreateRun],
        ["run_container", AssistantPermissionCategory.CreateRun],
        ["create_volume", AssistantPermissionCategory.CreateRun],
        ["create_network", AssistantPermissionCategory.CreateRun],
        ["start_container", AssistantPermissionCategory.Lifecycle],
        ["stop_container", AssistantPermissionCategory.Lifecycle],
        ["deploy_compose", AssistantPermissionCategory.ComposeTemplate],
        ["scale_deployment", AssistantPermissionCategory.Kubernetes],
    ];

    [Theory]
    [MemberData(nameof(DestructiveActions))]
    public void DestructiveActionsAreNotPermittedByDefault(string tool, AssistantPermissionCategory category)
    {
        var gate = Gate();

        Assert.True(gate.IsDestructive(tool, category));
        Assert.False(gate.IsPermitted(tool, category));
    }

    [Theory]
    [MemberData(nameof(DestructiveActions))]
    public void DestructiveActionsBecomePermittedOnlyAfterTheSettingIsEnabled(string tool, AssistantPermissionCategory category) =>
        Assert.True(Gate(new() { AllowDestructive = true }).IsPermitted(tool, category));

    /// <summary>
    /// Auto-approving a removal must not smuggle in the capability. Approval answers "may it run
    /// without asking"; permission answers "may it run at all".
    /// </summary>
    [Theory]
    [MemberData(nameof(DestructiveActions))]
    public void AutoApprovingADestructiveToolDoesNotGrantPermission(string tool, AssistantPermissionCategory category) =>
        Assert.False(Gate(new() { AutoApproved = [tool] }).IsPermitted(tool, category));

    /// <summary>Waiving prompts is not the same as granting deletion.</summary>
    [Theory]
    [MemberData(nameof(DestructiveActions))]
    public void ApprovingEverythingDoesNotGrantPermissionToDelete(string tool, AssistantPermissionCategory category) =>
        Assert.False(Gate(new() { ApproveEverything = true }).IsPermitted(tool, category));

    [Theory]
    [MemberData(nameof(NonDestructiveActions))]
    public void NonDestructiveActionsArePermittedByDefaultButStillAsk(string tool, AssistantPermissionCategory category)
    {
        var gate = Gate();

        Assert.False(gate.IsDestructive(tool, category));
        Assert.True(gate.IsPermitted(tool, category));
        // Permitted is not the same as silent: a fresh install still prompts for every one of these.
        Assert.True(gate.RequiresApproval(tool, category));
    }

    [Fact]
    public void ReadOnlyToolsRunWithoutApprovalOnAFreshInstall() =>
        Assert.False(Gate().RequiresApproval("list_containers", AssistantPermissionCategory.ReadOnly));

    /// <summary>
    /// Compose applies model-authored YAML, so approving "deploy a Compose stack" once is not
    /// informed consent for every later stack. Only the explicit no-prompts mode waives it.
    /// </summary>
    [Fact]
    public void ComposeDeploymentIgnoresPerToolAutoApprove()
    {
        var autoApproved = Gate(new() { AutoApproved = ["deploy_compose"] });

        Assert.True(autoApproved.RequiresApproval("deploy_compose", AssistantPermissionCategory.ComposeTemplate,
            requiresExplicitApproval: true));
        Assert.False(Gate(new() { ApproveEverything = true }).RequiresApproval(
            "deploy_compose", AssistantPermissionCategory.ComposeTemplate, requiresExplicitApproval: true));
    }

    [Fact]
    public void EveryDestructiveCatalogEntryIsMarkedSoTheSettingsRowCanDependOnIt()
    {
        var gate = Gate();
        var marked = AssistantToolCatalog.Groups.SelectMany(g => g.Tools)
            .Where(t => t.IsDestructive).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var destructive in DestructiveActions().Select(d => (string)d[0]))
        {
            Assert.True(marked.Contains(destructive),
                $"'{destructive}' is gated as destructive but its settings row is not marked, so it would stay editable.");
        }

        // And nothing is marked destructive that the gate would happily permit by default.
        foreach (var name in marked)
        {
            Assert.False(gate.IsPermitted(name, AssistantPermissionCategory.Destructive),
                $"'{name}' is shown as destructive but is permitted by default.");
        }
    }
}
