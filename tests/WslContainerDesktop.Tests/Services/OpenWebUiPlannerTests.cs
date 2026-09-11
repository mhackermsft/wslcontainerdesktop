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
/// Deploying a second Ollama duplicates multi-GB models and competes for its port, so the template
/// must reuse an existing runtime. Inventory reports a bare image ID as often as a reference, which
/// is what these cover.
/// </summary>
public class OpenWebUiPlannerTests
{
    private const string OllamaImageId = "2a5d04622211";

    [Fact]
    public async Task NoRuntimePresent_DeploysBundledStack()
    {
        var fake = new FakeWslc();

        var plan = await new OpenWebUiPlanner(fake.Service).PlanAsync();

        Assert.False(plan.ReusesExistingRuntime);
        Assert.Equal(OpenWebUiPlanner.BundledYaml, plan.Yaml);
        Assert.Null(plan.ExistingOllamaId);
        // The bundled runtime must not publish 11434, or it competes with a runtime added later.
        Assert.DoesNotContain("11434:11434", plan.Yaml);
    }

    /// <summary>The engine reports the image ID, not "ollama/ollama", for a running container.</summary>
    [Fact]
    public async Task RuntimeReportedByImageId_IsDetectedAndReused()
    {
        var fake = new FakeWslc();
        fake.Images.Add(new() { Id = OllamaImageId, Repository = "ollama/ollama", Tag = "latest" });
        fake.Containers.Add(new()
        {
            Id = "b94507b6ce5e",
            Name = "wslcd-ollama",
            Image = OllamaImageId,
            StateValue = (int)ContainerState.Running,
        });

        var plan = await new OpenWebUiPlanner(fake.Service).PlanAsync();

        Assert.True(plan.ReusesExistingRuntime);
        Assert.Equal("b94507b6ce5e", plan.ExistingOllamaId);
        Assert.Equal("wslcd-ollama", plan.ExistingOllamaName);
        Assert.Equal(OpenWebUiPlanner.ReuseYaml, plan.Yaml);
        // Only the web UI is deployed, pointed at the reused runtime by alias.
        Assert.DoesNotContain("image: ollama/ollama", plan.Yaml);
        Assert.Contains($"http://{OpenWebUiPlanner.OllamaAlias}:11434", plan.Yaml);
    }

    [Theory]
    [InlineData("ollama/ollama")]
    [InlineData("ollama/ollama:latest")]
    [InlineData("docker.io/ollama/ollama:0.12")]
    public async Task RuntimeReportedByReference_IsDetected(string image)
    {
        var fake = new FakeWslc();
        fake.Containers.Add(new() { Id = "abc123abc123", Name = "my-ollama", Image = image, StateValue = (int)ContainerState.Running });

        Assert.True((await new OpenWebUiPlanner(fake.Service).PlanAsync()).ReusesExistingRuntime);
    }

    [Theory]
    [InlineData("nginx:alpine")]
    [InlineData("myollama/notollama:1")]
    [InlineData("f0ba77f796e5")]
    public async Task UnrelatedContainers_AreNotMistakenForARuntime(string image)
    {
        var fake = new FakeWslc();
        fake.Images.Add(new() { Id = "f0ba77f796e5", Repository = "nginx", Tag = "alpine" });
        fake.Containers.Add(new() { Id = "dddddddddddd", Name = "web", Image = image, StateValue = (int)ContainerState.Running });

        Assert.False((await new OpenWebUiPlanner(fake.Service).PlanAsync()).ReusesExistingRuntime);
    }

    [Fact]
    public async Task RunningRuntimeIsPreferredOverStopped()
    {
        var fake = new FakeWslc();
        fake.Containers.Add(new() { Id = "111111111111", Name = "stopped", Image = "ollama/ollama", StateValue = (int)ContainerState.Stopped });
        fake.Containers.Add(new() { Id = "222222222222", Name = "running", Image = "ollama/ollama", StateValue = (int)ContainerState.Running });

        Assert.Equal("222222222222", (await new OpenWebUiPlanner(fake.Service).PlanAsync()).ExistingOllamaId);
    }

    /// <summary>Unreadable inventory must not be read as "a runtime exists".</summary>
    [Fact]
    public async Task InventoryFailure_DeploysBundledStack()
    {
        var fake = new FakeWslc { FailContainers = true };

        Assert.False((await new OpenWebUiPlanner(fake.Service).PlanAsync()).ReusesExistingRuntime);
    }

    [Fact]
    public async Task AttachUsesTheProjectNetworkAndAlias()
    {
        var fake = new FakeWslc();

        await new OpenWebUiPlanner(fake.Service).AttachExistingAsync("b94507b6ce5e");

        var (attachment, containerId) = Assert.Single(fake.Connections);
        Assert.Equal(OpenWebUiPlanner.NetworkName, attachment.Network);
        Assert.Equal(OpenWebUiPlanner.OllamaAlias, Assert.Single(attachment.Aliases));
        Assert.Equal("b94507b6ce5e", containerId);
        // An address would break when the reused container restarts; the alias must be used.
        Assert.Null(attachment.Ipv4Address);
    }

    private sealed class FakeWslc
    {
        public List<ContainerInfo> Containers { get; } = [];
        public List<ImageInfo> Images { get; } = [];
        public bool FailContainers { get; init; }
        public List<(NetworkAttachment Attachment, string ContainerId)> Connections { get; } = [];

        /// <summary>Any engine call these tests do not script fails, so an unexpected operation
        /// cannot pass silently.</summary>
        public IWslcService Service => NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => FailContainers
                ? Task.FromException<IReadOnlyList<ContainerInfo>>(new InvalidOperationException("inventory unavailable"))
                : Task.FromResult<IReadOnlyList<ContainerInfo>>(Containers),
            nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>(Images),
            nameof(IWslcService.ConnectNetworkAsync) => Connect(args),
            _ => throw new InvalidOperationException($"Unexpected engine call: {method.Name}"),
        });

        private Task<CommandResult> Connect(object?[] args)
        {
            Connections.Add(((NetworkAttachment)args[0]!, (string)args[1]!));
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }
    }
}

