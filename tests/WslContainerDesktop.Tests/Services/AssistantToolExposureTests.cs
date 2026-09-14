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
/// The catalog is offered per turn and competes with the conversation for one byte budget, so it
/// only advertises what is actually usable right now. Gating is on capability and present state —
/// never on guessing intent from the user's wording, which would hide a tool exactly when an
/// unusual request needed it.
/// </summary>
public class AssistantToolExposureTests
{
    private static AssistantToolset Tools(bool allowDestructive, int savedProjects)
    {
        var engine = NetworkTestProxy.Create<IWslcService>((method, _) =>
            throw new InvalidOperationException("Unexpected engine call " + method.Name));
        return new(engine,
            NetworkTestProxy.Create<IKubernetesService>((_, _) =>
                Task.FromResult(new ClusterStatus { State = ClusterState.NotInstalled })),
            NetworkTestProxy.Create<ITemplateCatalog>((_, _) => new List<StackTemplate>()),
            NetworkTestProxy.Create<IComposeProjectStore>((method, _) =>
                method.Name == nameof(IComposeProjectStore.GetAll)
                    ? Enumerable.Range(0, savedProjects).Select(i => new ComposeProject { Name = "p" + i }).ToList()
                    : throw new InvalidOperationException("Unexpected store call " + method.Name)),
            null!,
            NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_AiAssistantAllowDestructive" => allowDestructive,
                "get_Registries" => new List<RegistryEntry>(),
                _ => throw new InvalidOperationException(method.Name),
            }),
            NetworkTestProxy.Create<IRegistryCatalogService>((_, _) =>
                throw new InvalidOperationException("Unexpected registry access")));
    }

    private static async Task<HashSet<string>> Names(bool allowDestructive = true, int savedProjects = 1) =>
        (await Tools(allowDestructive, savedProjects).GetDefinitionsAsync(default))
            .Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<object[]> DestructiveTools() =>
        [["remove_container"], ["remove_all_containers"], ["remove_volume"], ["remove_network"]];

    /// <summary>
    /// Withholding these costs nothing and gains twice: fewer bytes, and no round trip spent on a
    /// call that could only ever be refused.
    /// </summary>
    [Theory]
    [MemberData(nameof(DestructiveTools))]
    public async Task DestructiveToolsAreNotOfferedUntilTheCapabilityIsGranted(string tool)
    {
        Assert.DoesNotContain(tool, await Names(allowDestructive: false));
        Assert.Contains(tool, await Names(allowDestructive: true));
    }

    /// <summary>Operating a saved project is meaningless when none are saved.</summary>
    [Theory]
    [InlineData("start_compose_project")]
    [InlineData("stop_compose_project")]
    [InlineData("restart_compose_project")]
    [InlineData("down_compose_project")]
    public async Task SavedProjectToolsAppearOnlyWhenAProjectExists(string tool)
    {
        Assert.DoesNotContain(tool, await Names(savedProjects: 0));
        Assert.Contains(tool, await Names(savedProjects: 1));
    }

    /// <summary>
    /// Listing must always be available: it is how the assistant discovers there are no projects,
    /// and how it reports that honestly instead of guessing.
    /// </summary>
    [Fact]
    public async Task ListingProjectsIsAlwaysOffered() =>
        Assert.Contains("list_compose_projects", await Names(savedProjects: 0));

    /// <summary>Read-only and core deployment tools never depend on either gate.</summary>
    [Theory]
    [InlineData("list_containers")]
    [InlineData("inspect_container")]
    [InlineData("run_container")]
    [InlineData("deploy_compose")]
    [InlineData("deploy_template")]
    [InlineData("stop_container")]
    [InlineData("create_volume")]
    public async Task CoreToolsAreAlwaysOffered(string tool)
    {
        Assert.Contains(tool, await Names(allowDestructive: false, savedProjects: 0));
        Assert.Contains(tool, await Names(allowDestructive: true, savedProjects: 1));
    }

    /// <summary>Gating is worth doing only if it measurably buys budget back.</summary>
    [Fact]
    public async Task TheDefaultCatalogIsSmallerThanTheFullyGrantedOne()
    {
        var restricted = await Tools(false, 0).GetDefinitionsAsync(default);
        var granted = await Tools(true, 1).GetDefinitionsAsync(default);

        var restrictedBytes = AiConversationContext.Measure([], restricted);
        var grantedBytes = AiConversationContext.Measure([], granted);

        Assert.True(restrictedBytes < grantedBytes,
            $"Restricted catalog ({restrictedBytes}) should cost less than the granted one ({grantedBytes}).");
        // Both must leave real room for a conversation inside the smallest supported budget.
        Assert.True(grantedBytes < AiConversationContext.DefaultInputByteLimit,
            $"The catalog ({grantedBytes}) must fit inside the default budget with room to talk.");
    }

    /// <summary>An unreadable store must not silently hide tools the user may need.</summary>
    [Fact]
    public async Task AnUnreadableProjectStoreStillOffersProjectTools()
    {
        var tools = new AssistantToolset(
            NetworkTestProxy.Create<IWslcService>((method, _) => throw new InvalidOperationException(method.Name)),
            NetworkTestProxy.Create<IKubernetesService>((_, _) =>
                Task.FromResult(new ClusterStatus { State = ClusterState.NotInstalled })),
            NetworkTestProxy.Create<ITemplateCatalog>((_, _) => new List<StackTemplate>()),
            NetworkTestProxy.Create<IComposeProjectStore>((_, _) =>
                throw new InvalidOperationException("store unavailable")),
            null!,
            NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_AiAssistantAllowDestructive" => false,
                "get_Registries" => new List<RegistryEntry>(),
                _ => throw new InvalidOperationException(method.Name),
            }),
            NetworkTestProxy.Create<IRegistryCatalogService>((_, _) =>
                throw new InvalidOperationException("Unexpected registry access")));

        var names = (await tools.GetDefinitionsAsync(default)).Select(d => d.Name).ToArray();

        Assert.Contains("start_compose_project", names);
    }
}
