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

        var plan = await fake.Planner.PlanAsync();

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

        var plan = await fake.Planner.PlanAsync();

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

        Assert.True((await fake.Planner.PlanAsync()).ReusesExistingRuntime);
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

        Assert.False((await fake.Planner.PlanAsync()).ReusesExistingRuntime);
    }

    [Fact]
    public async Task RunningRuntimeIsPreferredOverStopped()
    {
        var fake = new FakeWslc();
        fake.Containers.Add(new() { Id = "111111111111", Name = "stopped", Image = "ollama/ollama", StateValue = (int)ContainerState.Stopped });
        fake.Containers.Add(new() { Id = "222222222222", Name = "running", Image = "ollama/ollama", StateValue = (int)ContainerState.Running });

        Assert.Equal("222222222222", (await fake.Planner.PlanAsync()).ExistingOllamaId);
    }

    /// <summary>Unreadable inventory must not be read as "a runtime exists".</summary>
    [Fact]
    public async Task InventoryFailure_DeploysBundledStack()
    {
        var fake = new FakeWslc { FailContainers = true };

        Assert.False((await fake.Planner.PlanAsync()).ReusesExistingRuntime);
    }

    [Fact]
    public async Task AttachUsesTheProjectNetworkAndAlias()
    {
        var fake = new FakeWslc();

        await fake.Planner.AttachExistingAsync("b94507b6ce5e");

        var (attachment, containerId) = Assert.Single(fake.Connections);
        Assert.Equal(OpenWebUiPlanner.NetworkName, attachment.Network);
        Assert.Equal(OpenWebUiPlanner.OllamaAlias, Assert.Single(attachment.Aliases));
        Assert.Equal("b94507b6ce5e", containerId);
        // An address would break when the reused container restarts; the alias must be used.
        Assert.Null(attachment.Ipv4Address);
    }

    /// <summary>
    /// Teardown removes project services, so a reused runtime must never appear as one. Otherwise
    /// removing the template would delete an Ollama the user set up for the AI assistant.
    /// </summary>
    [Fact]
    public void ReuseYamlDeclaresOnlyTheWebUiSoTeardownCannotRemoveAReusedRuntime()
    {
        // A service is declared at two-space indent; the alias also appears inside OLLAMA_BASE_URL,
        // so match the declaration rather than the name anywhere in the document.
        Assert.DoesNotContain("\n  ollama:", OpenWebUiPlanner.ReuseYaml);
        Assert.DoesNotContain("image: ollama/ollama", OpenWebUiPlanner.ReuseYaml);
        Assert.Contains("\n  open-webui:", OpenWebUiPlanner.ReuseYaml);
        // The bundled stack owns its runtime, so removing the template should take it with it.
        Assert.Contains("\n  ollama:", OpenWebUiPlanner.BundledYaml);
        Assert.Contains("image: ollama/ollama", OpenWebUiPlanner.BundledYaml);
    }

    [Theory]
    [InlineData("llama3.2:3b")]
    [InlineData("qwen2.5:7b")]
    [InlineData("phi4-mini:latest")]
    [InlineData("library/gemma3:4b")]
    [InlineData("mistral")]
    public void OrdinaryModelNamesAreAccepted(string model) =>
        Assert.True(OpenWebUiPlanner.IsValidModelName(model));

    /// <summary>The model name reaches a shell, so anything that could extend the command is rejected.</summary>
    [Theory]
    [InlineData("llama3.2:3b; rm -rf /")]
    [InlineData("llama3.2 && curl evil.example")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("model | tee /tmp/x")]
    [InlineData("model\nsecond")]
    [InlineData("--flag")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsafeOrEmptyModelNamesAreRejected(string? model) =>
        Assert.False(OpenWebUiPlanner.IsValidModelName(model));

    [Fact]
    public async Task InstallModelRefusesAnUnsafeNameWithoutRunningAnything()
    {
        var fake = new FakeWslc();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fake.Planner.InstallModelAsync("bad; rm -rf /"));
    }

    [Fact]
    public async Task InstallModelTargetsOnlyTheBundledContainer()
    {
        var fake = new FakeWslc();

        await fake.Planner.InstallModelAsync(" llama3.2:3b ");

        var (container, command) = Assert.Single(fake.Execs);
        Assert.Equal(OpenWebUiPlanner.BundledOllamaContainer, container);
        Assert.Equal("ollama pull llama3.2:3b", command);
    }

    [Fact]
    public void RecommendedModelIsOfferedAmongTheSuggestions()
    {
        Assert.Contains(OpenWebUiPlanner.RecommendedModel, OpenWebUiPlanner.SuggestedModels);
        Assert.All(OpenWebUiPlanner.SuggestedModels, m => Assert.True(OpenWebUiPlanner.IsValidModelName(m)));
    }

    /// <summary>CPU-only inference is dramatically slower, so a fresh runtime must ask for the GPU
    /// when the engine advertises it.</summary>
    [Fact]
    public async Task GpuSupported_DeploysTheStackWithAGpuReservation()
    {
        var fake = new FakeWslc { Gpu = WslcCapabilitySupport.Supported };

        var plan = await fake.Planner.PlanAsync();

        Assert.True(plan.UsesGpu);
        Assert.Equal(OpenWebUiPlanner.BundledGpuYaml, plan.Yaml);
        Assert.Contains("capabilities: [gpu]", plan.Yaml);
    }

    /// <summary>Anything short of definite support stays on CPU rather than risking a launch that
    /// fails on a flag the engine does not know.</summary>
    [Theory]
    [InlineData(WslcCapabilitySupport.Unsupported)]
    [InlineData(WslcCapabilitySupport.Unknown)]
    public async Task GpuNotDefinitelySupported_DeploysTheCpuStack(WslcCapabilitySupport support)
    {
        var fake = new FakeWslc { Gpu = support };

        var plan = await fake.Planner.PlanAsync();

        Assert.False(plan.UsesGpu);
        Assert.Equal(OpenWebUiPlanner.BundledYaml, plan.Yaml);
        Assert.DoesNotContain("gpu", plan.Yaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapabilityProbeFailure_DeploysTheCpuStackInsteadOfFailingTheLaunch()
    {
        var fake = new FakeWslc { FailCapabilities = true };

        var plan = await fake.Planner.PlanAsync();

        Assert.False(plan.UsesGpu);
        Assert.Equal(OpenWebUiPlanner.BundledYaml, plan.Yaml);
    }

    /// <summary>A reused runtime keeps the access it was created with; the template must not claim
    /// to have enabled a GPU it never configured.</summary>
    [Fact]
    public async Task ReusedRuntime_IsNeverReportedAsGpuEnabled()
    {
        var fake = new FakeWslc { Gpu = WslcCapabilitySupport.Supported };
        fake.Containers.Add(new() { Id = "abc123abc123", Name = "my-ollama", Image = "ollama/ollama", StateValue = (int)ContainerState.Running });

        var plan = await fake.Planner.PlanAsync();

        Assert.True(plan.ReusesExistingRuntime);
        Assert.False(plan.UsesGpu);
        Assert.Equal(OpenWebUiPlanner.ReuseYaml, plan.Yaml);
    }

    /// <summary>Guards drift: the two bundled stacks must differ only by the GPU reservation, so a
    /// change to ports, volumes or images cannot silently apply to just one of them.</summary>
    [Fact]
    public void TheGpuStackIsTheBundledStackPlusOnlyTheReservation()
    {
        var withoutReservation = OpenWebUiPlanner.BundledGpuYaml
            .Replace(OpenWebUiPlanner.GpuReservationBlock, string.Empty, StringComparison.Ordinal);

        Assert.NotEqual(OpenWebUiPlanner.BundledGpuYaml, withoutReservation);
        Assert.Equal(Normalize(OpenWebUiPlanner.BundledYaml), Normalize(withoutReservation));

        static string Normalize(string yaml) => string.Join('\n',
            yaml.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0));
    }

    /// <summary>
    /// The reservation only helps if the importer actually turns it into a GPU request, so parse
    /// the real template text. A mis-indented or misnamed key would still be valid YAML and would
    /// otherwise deploy silently on CPU.
    /// </summary>
    [Fact]
    public void TheGpuStackParsesIntoAnActualGpuRequestForTheRuntimeOnly()
    {
        var project = ComposeImporter.ParseProject(OpenWebUiPlanner.BundledGpuYaml);

        var ollama = Assert.Single(project.Services, s => s.Name == "ollama");
        Assert.True(ollama.Options.AllGpus);
        // Only the runtime benefits; the web UI must not request a GPU it never uses.
        Assert.False(Assert.Single(project.Services, s => s.Name == "open-webui").Options.AllGpus);
        Assert.Empty(project.Warnings);
    }

    /// <summary>The CPU stack must stay genuinely CPU-only.</summary>
    [Fact]
    public void TheBundledStackRequestsNoGpu() =>
        Assert.All(ComposeImporter.ParseProject(OpenWebUiPlanner.BundledYaml).Services,
            s => Assert.False(s.Options.AllGpus));

    private sealed class FakeWslc
    {
        public List<ContainerInfo> Containers { get; } = [];
        public List<ImageInfo> Images { get; } = [];
        public bool FailContainers { get; init; }

        /// <summary>Engine GPU-create support. Defaults to unsupported so a test that cares about
        /// GPU selection must opt in explicitly.</summary>
        public WslcCapabilitySupport Gpu { get; init; } = WslcCapabilitySupport.Unsupported;

        /// <summary>Makes the capability probe throw, standing in for an unreadable engine.</summary>
        public bool FailCapabilities { get; init; }

        public List<(NetworkAttachment Attachment, string ContainerId)> Connections { get; } = [];
        public List<(string Container, string Command)> Execs { get; } = [];

        /// <summary>The planner under test, wired to this harness.</summary>
        public OpenWebUiPlanner Planner => new(Service, Capabilities);

        public IWslcCapabilitiesService Capabilities => NetworkTestProxy.Create<IWslcCapabilitiesService>((method, _) =>
        {
            if (method.Name != nameof(IWslcCapabilitiesService.GetAsync))
                throw new InvalidOperationException($"Unexpected capability call: {method.Name}");
            if (FailCapabilities)
                return Task.FromException<WslcCapabilities>(new InvalidOperationException("capabilities unavailable"));
            return Task.FromResult(new WslcCapabilities("synthetic-wslc-not-executable", "synthetic",
                new Dictionary<WslcFeature, WslcCapability>
                {
                    [WslcFeature.CreateGpus] = new(Gpu, "synthetic GPU help evidence"),
                }));
        });

        /// <summary>Any engine call these tests do not script fails, so an unexpected operation
        /// cannot pass silently.</summary>
        public IWslcService Service => NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
        {
            nameof(IWslcService.ListContainersAsync) => FailContainers
                ? Task.FromException<IReadOnlyList<ContainerInfo>>(new InvalidOperationException("inventory unavailable"))
                : Task.FromResult<IReadOnlyList<ContainerInfo>>(Containers),
            nameof(IWslcService.ListImagesAsync) => Task.FromResult<IReadOnlyList<ImageInfo>>(Images),
            nameof(IWslcService.ConnectNetworkAsync) => Connect(args),
            nameof(IWslcService.ExecAsync) => Exec(args),
            _ => throw new InvalidOperationException($"Unexpected engine call: {method.Name}"),
        });

        private Task<CommandResult> Exec(object?[] args)
        {
            Execs.Add(((string)args[0]!, (string)args[1]!));
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }

        private Task<CommandResult> Connect(object?[] args)
        {
            Connections.Add(((NetworkAttachment)args[0]!, (string)args[1]!));
            return Task.FromResult(new CommandResult { ExitCode = 0 });
        }
    }
}

