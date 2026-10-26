// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeHostGatewayTests
{
    private static ComposeProject Project(string publication = "1521:1521") => new()
    {
        Name = "demo", Services =
        [
            new() { Name = "keycloak", ExtraHosts = ["host.docker.internal:host-gateway"],
                Options = new() { Network = "net" } },
            new() { Name = "oracle", Options = new() { Network = "net", PortMappings = [publication] } },
        ],
    };

    private static JsonElement Inspection(string owner = "demo", bool running = true) => JsonSerializer.SerializeToElement(new
    {
        Id = "server-id", State = new { Running = running },
        Config = new { Labels = new Dictionary<string, string>
            { [ComposeProject.ProjectLabel] = owner, [ComposeProject.ServiceLabel] = "oracle" } },
        HostConfig = new { NetworkMode = "net" }, NetworkSettings = new
        {
            Networks = new Dictionary<string, object> { ["net"] = new { Gateway = "172.18.0.1", IPAddress = "172.18.0.3" } },
        },
        Ports = new Dictionary<string, object> { ["1521/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = "1521" } } },
    });

    [Fact]
    public void PublishedHostPortIsMappedWithoutChangingDesiredOptions()
    {
        var project = Project();
        var before = JsonSerializer.Serialize(project);
        var mapping = Assert.Single(ComposeHostGateway.Plan(project, [Inspection()]));
        Assert.Equal(new("172.18.0.1", "tcp", 1521, "172.18.0.3", 1521), mapping);
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void GatewayMayUseSecondaryNetworkWhenPrimaryIsInternal()
    {
        var project = Project();
        project.Services[0].Options.Network = "internal";
        project.Services[0].Options.Networks = ["internal", "net"];
        Assert.Equal("172.18.0.1", Assert.Single(ComposeHostGateway.Plan(project, [Inspection()])).Gateway);
    }

    [Theory]
    [InlineData("127.0.0.1:1521:1521")]
    [InlineData("192.168.1.2:1521:1521")]
    [InlineData("2521:1521")]
    public void ExplicitBindingsAndUnappliedPortEditsAreNotWidened(string spec) =>
        Assert.Empty(ComposeHostGateway.Plan(Project(spec), [Inspection()]));

    [Theory]
    [InlineData("foreign", true)]
    [InlineData("demo", false)]
    public void ForeignAndStoppedContainersAreNotForwarded(string owner, bool running) =>
        Assert.Empty(ComposeHostGateway.Plan(Project(), [Inspection(owner, running)]));

    [Fact]
    public void NativeScriptMatchesSharedVectorAndPreservesDockerUserPolicy()
    {
        var script = ComposeHostGateway.Script("demo", [new("172.18.0.1", "tcp", 1521, "172.18.0.3", 1521)]);
        Assert.Equal("6f67791185af72f734ba636753abe3298edefddb023233a021fc2c62ad495707",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant());
        Assert.Contains("--ctorigdst 172.18.0.1 --ctorigdstport 1521", script);
        Assert.Contains("-j DOCKER-USER", script);
        Assert.DoesNotContain("-F DOCKER", script);
    }

    [Fact]
    public void CleanupRequiresOwnerProofAndDeletesOnlyOwnedChains()
    {
        var script = ComposeHostGateway.Script("demo", [], cleanup: true);
        Assert.Contains("exit 71", script);
        Assert.Contains("-X WCGP_", script);
        Assert.Contains("-X WCGF_", script);
        Assert.DoesNotContain("-X DOCKER", script);
    }

    [Theory]
    [InlineData("172.18.0.1;exit", "tcp", 1521, 1521)]
    [InlineData("172.18.0.1", "tcp;exit", 1521, 1521)]
    [InlineData("172.18.0.1", "tcp", 0, 1521)]
    [InlineData("172.18.0.1", "tcp", 1521, 65536)]
    public void RuntimeValuesCannotInjectShellSyntax(string gateway, string protocol, int hostPort, int port) =>
        Assert.Throws<InvalidOperationException>(() => ComposeHostGateway.Script("demo",
            [new(gateway, protocol, hostPort, "172.18.0.3", port)]));
}
