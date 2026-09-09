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

using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Models;

public sealed class ComposeNetworkOptionsTests
{
    [Fact]
    public void ParsesNamespacingAliasesStaticIpsAndExternalDefault()
    {
        var project = ComposeImporter.ParseProject("""
            name: demo
            services:
              web:
                image: nginx:alpine
                networks:
                  front:
                    aliases: [public, public]
                    ipv4_address: 172.28.0.10
                  back:
                    aliases:
                      - private
                    ipv4_address: 172.29.0.10
              db:
                image: nginx:alpine
            networks:
              front:
                ipam:
                  config:
                    - subnet: 172.28.0.0/24
                      gateway: 172.28.0.1
              back:
                external: true
                name: shared-back
              default:
                external: true
                name: shared-default
            """);
        project.ApplyProjectNamespacing();
        var web = project.Services[0].Options;
        Assert.Equal(["demo_front", "shared-back"], web.Networks);
        Assert.Equal("demo_front", web.Network);
        Assert.Equal("public", Assert.Single(web.NetworkAttachments[0].Aliases));
        Assert.Equal("private", Assert.Single(web.NetworkAttachments[1].Aliases));
        Assert.Equal("172.29.0.10", web.NetworkAttachments[1].Ipv4Address);
        Assert.Equal("shared-default", project.Services[1].Options.Network);
        Assert.Equal("172.28.0.0/24", project.Networks[0].Subnet);
        Assert.True(project.Networks[1].External);
        Assert.DoesNotContain(project.Warnings, w => w.Contains("single-network limitation"));
    }

    [Fact]
    public void CloneAndJsonPreserveIndependentEndpointSettings()
    {
        var options = new RunContainerOptions
        {
            Image = "image",
            Network = "a",
            Networks = ["a", "b"],
            NetworkAttachments =
            [
                new() { Network = "a", Aliases = ["front"], Ipv4Address = "10.1.0.2" },
                new() { Network = "b", Aliases = ["back"], Ipv4Address = "10.2.0.2" },
            ],
        };
        var clone = JsonSerializer.Deserialize<RunContainerOptions>(JsonSerializer.Serialize(options))!.Clone();
        clone.NetworkAttachments[1].Aliases.Add("other");
        Assert.Equal(["back"], options.NetworkAttachments[1].Aliases);
        var run = options.ToArguments();
        Assert.Contains("front", run);
        Assert.DoesNotContain("back", run);
        Assert.Contains("10.1.0.2", run);
        Assert.Equal(
            ["network", "connect", "--network-alias", "back", "--ip", "10.2.0.2", "b", "container"],
            options.NetworkAttachments[1].ToConnectArguments("container"));
        var create = options.ToCreateArguments();
        Assert.Equal("create", create[0]);
        Assert.DoesNotContain("-d", create);
        Assert.Equal(run.Where(a => a is not "-d").Skip(1), create.Skip(1));
    }

    [Fact]
    public void OldProjectsRetainFirstNetworkAndDeduplicateDeclarations()
    {
        var options = JsonSerializer.Deserialize<RunContainerOptions>(
            """{"Image":"image","Network":"a","Networks":["a","b","a"],"Aliases":["old"]}""")!;
        Assert.Empty(options.NetworkAttachments);
        Assert.Equal(["a", "b"], options.GetNetworkAttachments().Select(n => n.Network));
        Assert.Equal(["old"], options.GetNetworkAttachments()[0].Aliases);
        Assert.Empty(options.GetNetworkAttachments()[1].Aliases);
        Assert.Contains("old", options.ToArguments());
        var parsed = ComposeImporter.ParseProject("services:\n  web:\n    image: image\n    networks: [a, b, a]");
        Assert.Equal(2, parsed.Services[0].Options.NetworkAttachments.Count);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("none")]
    [InlineData("container:other")]
    [InlineData("bridge")]
    public void SpecialModesNeverReceiveAliasesOrSecondaryEndpoints(string mode)
    {
        var project = ComposeImporter.ParseProject($"""
            name: demo
            services:
              web:
                image: image
                network_mode: {mode}
                networks: [a, b]
            """);
        project.ApplyProjectNamespacing();
        var options = project.Services[0].Options;
        options.Aliases.Add("web");
        Assert.Empty(options.GetNetworkAttachments());
        Assert.Empty(project.Networks);
        Assert.Contains(mode, options.ToArguments());
        Assert.DoesNotContain("--network-alias", options.ToArguments());
    }

    [Fact]
    public void NamedDefaultNetworkIsNotDiscarded()
    {
        var project = ComposeImporter.ParseProject("""
            name: demo
            services:
              web:
                image: image
                networks: [default, second]
            networks:
              default:
              second:
            """);
        project.ApplyProjectNamespacing();
        Assert.Equal(["demo_default", "demo_second"], project.Services[0].Options.Networks);
    }

    [Fact]
    public void InvalidIpFailsBeforeArgumentEmission()
    {
        Assert.Throws<ArgumentException>(() => new NetworkAttachment
        {
            Network = "a", Ipv4Address = "not-an-ip",
        }.ToConnectArguments("container"));
    }

    [Theory]
    [InlineData("networks: [default]")]
    [InlineData("networks:\n      default:\n        aliases: [web-alias]")]
    public void ExplicitDefaultReferenceSynthesizesMissingDefinition(string declaration)
    {
        var project = ComposeImporter.ParseProject(
            "name: demo\nservices:\n  web:\n    image: image\n    " + declaration);
        project.ApplyProjectNamespacing();
        Assert.Equal("demo_default", Assert.Single(project.Networks).Name);
        var options = project.Services[0].Options;
        Assert.Equal("demo_default", options.Network);
        Assert.Equal(["demo_default"], options.Networks);
        Assert.Equal("demo_default", Assert.Single(options.NetworkAttachments).Network);
        Assert.Contains("demo_default", options.ToArguments());
        if (declaration.Contains("web-alias", StringComparison.Ordinal))
        {
            Assert.Equal(["web-alias"], options.NetworkAttachments[0].Aliases);
        }
    }

    [Fact]
    public void ExplicitDefaultReferencePreservesDeclaredExternalConfiguration()
    {
        var project = ComposeImporter.ParseProject("""
            name: demo
            services:
              web:
                image: image
                networks: [default]
            networks:
              default:
                name: shared
                external: true
                driver: bridge
            """);
        project.ApplyProjectNamespacing();
        var network = Assert.Single(project.Networks);
        Assert.Equal("shared", network.Name);
        Assert.True(network.External);
        Assert.Equal("bridge", network.Driver);
        Assert.Equal("shared", project.Services[0].Options.Network);
    }

    [Fact]
    public void InspectPreservesPrestartStaticIpAndAllNetworkPresentation()
    {
        const string json = """
            [{"Id":"id","Config":{"Labels":{"com.wsldesktop.project":"demo"}},
            "NetworkSettings":{"Networks":{
            "a":{"Aliases":["web"],"IPAddress":"","IPAMConfig":{"IPv4Address":"172.28.0.2"}},
            "b":{"Aliases":["private"],"IPAddress":"172.29.0.2","IPAMConfig":null}}}}]
            """;
        var state = ContainerNetworkState.Parse(json);
        Assert.Equal("172.28.0.2", state.Networks["a"].Ipv4Address);
        Assert.True(state.HasLabel(ComposeProject.ProjectLabel, "demo"));
        Assert.Equal("a, b", ContainerDetails.Parse(json).NetworkMode);
        Assert.Contains("b: 172.29.0.2", ContainerDetails.Parse(json).IpAddress);
        Assert.Throws<InvalidOperationException>(() => ContainerNetworkState.Parse("""{"Id":"id"}"""));
    }
}
