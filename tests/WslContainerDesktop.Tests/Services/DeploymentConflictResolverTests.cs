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
/// A new deployment must never make room for itself by disturbing something already running. The
/// assistant previously hit a name clash and decided on its own to stop and remove the user's
/// SQL Server to free the name; these pin the behaviour that makes that unnecessary.
/// </summary>
public class DeploymentConflictResolverTests
{
    private static ContainerInfo Existing(string name, params int[] hostPorts) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        StateValue = (int)ContainerState.Running,
        PortsKnown = true,
        Ports = hostPorts.Select(p => new PortMapping { HostPort = p, ContainerPort = p }).ToList(),
    };

    private static DeploymentConflictResolver Resolver(params ContainerInfo[] inventory) =>
        new(NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.ListContainersAsync)
                ? Task.FromResult<IReadOnlyList<ContainerInfo>>(inventory)
                : throw new InvalidOperationException($"Unexpected engine call: {method.Name}")));

    [Fact]
    public async Task AFreshNameAndPortAreLeftExactlyAsRequested()
    {
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver", PortMappings = { "1433:1433" } };

        var adjustment = await Resolver().ResolveAsync(options);

        Assert.False(adjustment.Adjusted);
        Assert.Equal("sqlserver", options.Name);
        Assert.Equal(["1433:1433"], options.PortMappings);
        Assert.Empty(adjustment.Summary);
    }

    [Fact]
    public async Task AClashingNameMovesAsideInsteadOfTouchingTheExistingContainer()
    {
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver" };

        var adjustment = await Resolver(Existing("sqlserver")).ResolveAsync(options);

        Assert.Equal("sqlserver-2", options.Name);
        Assert.Equal("sqlserver", adjustment.OriginalName);
        Assert.Contains("in use", adjustment.Summary);
    }

    /// <summary>The engine reports names with Docker's leading slash in its own conflict messages.</summary>
    [Fact]
    public async Task ALeadingSlashOnTheExistingNameStillCountsAsAClash()
    {
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver" };

        await Resolver(Existing("/sqlserver")).ResolveAsync(options);

        Assert.Equal("sqlserver-2", options.Name);
    }

    [Fact]
    public async Task SuffixesSkipNamesThatAreAlreadyTaken()
    {
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver" };

        await Resolver(Existing("sqlserver"), Existing("sqlserver-2"), Existing("sqlserver-3")).ResolveAsync(options);

        Assert.Equal("sqlserver-4", options.Name);
    }

    [Fact]
    public async Task AClashingHostPortMovesToAFreeOne()
    {
        var options = new RunContainerOptions { Image = "img", Name = "db", PortMappings = { "1433:1433" } };

        var adjustment = await Resolver(Existing("other", 1433)).ResolveAsync(options);

        Assert.Equal(["1434:1433"], options.PortMappings);
        Assert.Contains("1434", adjustment.Summary);
    }

    [Theory]
    [InlineData("127.0.0.1:8080:80", "127.0.0.1:8081:80")]
    [InlineData("8080:80/tcp", "8081:80/tcp")]
    public async Task RemappingPreservesBindAddressAndProtocol(string mapping, string expected)
    {
        var options = new RunContainerOptions { Image = "img", Name = "web", PortMappings = { mapping } };

        await Resolver(Existing("other", 8080)).ResolveAsync(options);

        Assert.Equal([expected], options.PortMappings);
    }

    [Fact]
    public async Task TwoMappingsCannotBeMovedOntoTheSamePort()
    {
        var options = new RunContainerOptions { Image = "img", Name = "web", PortMappings = { "8080:80", "8081:81" } };

        await Resolver(Existing("other", 8080)).ResolveAsync(options);

        Assert.Equal(options.PortMappings.Count, options.PortMappings.Distinct().Count());
        Assert.DoesNotContain("8080:80", options.PortMappings);
    }

    /// <summary>
    /// The point of renaming is an independent deployment. Two database containers sharing one
    /// volume would corrupt each other, which is worse than the clash that was avoided.
    /// </summary>
    [Fact]
    public async Task ARenamedDeploymentGetsItsOwnNamedVolumes()
    {
        var options = new RunContainerOptions
        {
            Image = "img",
            Name = "sqlserver",
            Volumes = { "sqlserver-data:/var/opt/mssql" },
        };

        var adjustment = await Resolver(Existing("sqlserver")).ResolveAsync(options);

        Assert.Equal(["sqlserver-data-2:/var/opt/mssql"], options.Volumes);
        Assert.Contains("own volume", adjustment.Summary);
    }

    /// <summary>A host path is the user's explicit decision about where data lives.</summary>
    [Theory]
    [InlineData("/srv/data:/data")]
    [InlineData("./local:/data")]
    [InlineData(@"C:\data:/data")]
    public async Task BindMountsAreNeverRewritten(string spec)
    {
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver", Volumes = { spec } };

        await Resolver(Existing("sqlserver")).ResolveAsync(options);

        Assert.Equal([spec], options.Volumes);
    }

    /// <summary>Nothing clashed, so the deployment keeps the data it was meant to use.</summary>
    [Fact]
    public async Task VolumesAreLeftAloneWhenTheNameWasFree()
    {
        var options = new RunContainerOptions
        {
            Image = "img",
            Name = "sqlserver",
            Volumes = { "sqlserver-data:/var/opt/mssql" },
        };

        await Resolver().ResolveAsync(options);

        Assert.Equal(["sqlserver-data:/var/opt/mssql"], options.Volumes);
    }

    /// <summary>Without inventory we cannot know what clashes; deploy as asked rather than guess.</summary>
    [Fact]
    public async Task UnreadableInventoryLeavesTheRequestUnchanged()
    {
        var resolver = new DeploymentConflictResolver(NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.ListContainersAsync)
                ? Task.FromException<IReadOnlyList<ContainerInfo>>(new InvalidOperationException("engine down"))
                : throw new InvalidOperationException($"Unexpected engine call: {method.Name}")));
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver", PortMappings = { "1433:1433" } };

        var adjustment = await resolver.ResolveAsync(options);

        Assert.False(adjustment.Adjusted);
        Assert.Equal("sqlserver", options.Name);
    }

    /// <summary>The resolver only ever reads inventory: it must not stop or remove anything.</summary>
    [Fact]
    public async Task ResolvingNeverCallsALifecycleOrRemovalCommand()
    {
        // The proxy throws on any engine call other than listing, so reaching the end proves it.
        var options = new RunContainerOptions { Image = "img", Name = "sqlserver", PortMappings = { "1433:1433" } };

        await Resolver(Existing("sqlserver", 1433)).ResolveAsync(options);

        Assert.Equal("sqlserver-2", options.Name);
        Assert.Equal(["1434:1433"], options.PortMappings);
    }

    private static DeploymentConflictResolver ProjectResolver(
        IEnumerable<ContainerInfo>? containers = null, IEnumerable<string>? networks = null) =>
        new(NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) =>
                Task.FromResult<IReadOnlyList<ContainerInfo>>((containers ?? []).ToList()),
            nameof(IWslcService.ListNetworksAsync) =>
                Task.FromResult<IReadOnlyList<NetworkInfo>>((networks ?? []).Select(n => new NetworkInfo { Name = n }).ToList()),
            _ => throw new InvalidOperationException($"Unexpected engine call: {method.Name}"),
        }));

    private static ComposeProject OpenWebUiLikeProject() => new()
    {
        Name = "openwebui",
        Services =
        {
            new ComposeService
            {
                Name = "web",
                Options = new RunContainerOptions { Image = "img", PortMappings = { "8084:8080" } },
            },
        },
        Networks = { new ComposeNetwork { Name = "webui", ExplicitName = "openwebui-net" } },
    };

    [Fact]
    public async Task AFreshComposeProjectKeepsItsNameNetworkAndPorts()
    {
        var project = OpenWebUiLikeProject();

        var adjustment = await ProjectResolver().ResolveProjectAsync(project);

        Assert.False(adjustment.Adjusted);
        Assert.Equal("openwebui", project.Name);
        Assert.Equal("openwebui-net", project.Networks[0].ExplicitName);
        Assert.Equal(["8084:8080"], project.Services[0].Options.PortMappings);
    }

    /// <summary>A project is already deployed when containers carry its "{project}_" prefix.</summary>
    [Fact]
    public async Task ARepeatComposeLaunchBecomesItsOwnProject()
    {
        var project = OpenWebUiLikeProject();

        var adjustment = await ProjectResolver([Existing("openwebui_web", 8084)]).ResolveProjectAsync(project);

        Assert.Equal("openwebui-2", project.Name);
        Assert.Contains("already deployed", adjustment.Summary);
    }

    /// <summary>
    /// Project namespacing deliberately preserves an explicitly named network, so without this the
    /// second deployment would attach to the first one's network instead of its own.
    /// </summary>
    [Fact]
    public async Task ARepeatComposeLaunchGetsItsOwnExplicitlyNamedNetwork()
    {
        var project = OpenWebUiLikeProject();

        await ProjectResolver([Existing("openwebui_web")], ["openwebui-net"]).ResolveProjectAsync(project);

        Assert.Equal("openwebui-net-2", project.Networks[0].ExplicitName);
    }

    [Fact]
    public async Task ARepeatComposeLaunchPublishesOnFreePorts()
    {
        var project = OpenWebUiLikeProject();

        await ProjectResolver([Existing("openwebui_web", 8084)]).ResolveProjectAsync(project);

        Assert.Equal(["8085:8080"], project.Services[0].Options.PortMappings);
    }

    /// <summary>An external network is the user's own; it must be joined, not cloned.</summary>
    [Fact]
    public async Task ExternalNetworksAreNeverRenamed()
    {
        var project = OpenWebUiLikeProject();
        project.Networks[0].External = true;

        await ProjectResolver([Existing("openwebui_web")], ["openwebui-net"]).ResolveProjectAsync(project);

        Assert.Equal("openwebui-net", project.Networks[0].ExplicitName);
    }

    [Fact]
    public async Task UnreadableInventoryLeavesTheComposeProjectUnchanged()
    {
        var resolver = new DeploymentConflictResolver(NetworkTestProxy.Create<IWslcService>((method, _) =>
            method.Name == nameof(IWslcService.ListContainersAsync)
                ? Task.FromException<IReadOnlyList<ContainerInfo>>(new InvalidOperationException("engine down"))
                : throw new InvalidOperationException($"Unexpected engine call: {method.Name}")));
        var project = OpenWebUiLikeProject();

        var adjustment = await resolver.ResolveProjectAsync(project);

        Assert.False(adjustment.Adjusted);
        Assert.Equal("openwebui", project.Name);
    }
}
