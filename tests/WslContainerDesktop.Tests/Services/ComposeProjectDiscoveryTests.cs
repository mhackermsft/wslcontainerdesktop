// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker

using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeProjectDiscoveryTests
{
    [Fact]
    public async Task ReimportsSingleAccessibleComposeFileForManagement()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "services:\n  api:\n    image: fixture\n");
            var escaped = System.Text.Json.JsonSerializer.Serialize(new[] { file });
            var json = "{\"Labels\":{\"com.wslc-compose.project\":\"shop\",\"com.wslc-compose.service\":\"api\",\"com.wslc-compose.project.config_files\":" +
                System.Text.Json.JsonSerializer.Serialize(escaped) + "}}";
            var projects = await ComposeProjectDiscovery.DiscoverAsync([new ContainerInfo { Id = "one" }], (_, _) =>
                Task.FromResult(new CommandResult { StandardOutput = json }));

            var project = Assert.Single(projects);
            Assert.False(project.IsExternallyManaged);
            Assert.Equal("fixture", Assert.Single(project.Services).Options.Image);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ReimportsOrderedAccessibleComposeFilesForManagement()
    {
        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(first, "services:\n  api:\n    image: fixture\n");
            await File.WriteAllTextAsync(second, "services:\n  api:\n    environment: [MODE=override]\n");
            var escaped = System.Text.Json.JsonSerializer.Serialize(new[] { first, second });
            var json = "{\"Labels\":{\"com.wslc-compose.project\":\"shop\",\"com.wslc-compose.service\":\"api\",\"com.wslc-compose.project.config_files\":" +
                System.Text.Json.JsonSerializer.Serialize(escaped) + "}}";
            var projects = await ComposeProjectDiscovery.DiscoverAsync([new ContainerInfo { Id = "one" }], (_, _) =>
                Task.FromResult(new CommandResult { StandardOutput = json }));

            var project = Assert.Single(projects);
            Assert.False(project.IsExternallyManaged);
            Assert.Equal("MODE=override", Assert.Single(project.Services).Options.EnvironmentVariables.Single());
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public async Task DiscoversSharedLabelProjectAndComposeFiles()
    {
        var containers = new[]
        {
            new ContainerInfo { Id = "one", StateValue = (int)ContainerState.Running },
            new ContainerInfo { Id = "two", StateValue = (int)ContainerState.Stopped },
        };

        var projects = await ComposeProjectDiscovery.DiscoverAsync(containers, (id, _) => Task.FromResult(new CommandResult
        {
            StandardOutput = id == "one"
                ? "{\"Config\":{\"Labels\":{\"com.wslc-compose.project\":\"shop\",\"com.wslc-compose.service\":\"api\",\"com.wslc-compose.project.config_files\":\"[\\\"C:\\\\\\\\shop\\\\\\\\compose.yml\\\"]\"}}}"
                : "{\"Config\":{\"Labels\":{\"com.wslc-compose.project\":\"shop\",\"com.wslc-compose.service\":\"worker\"}}}",
        }));

        var project = Assert.Single(projects);
        Assert.Equal("shop", project.Name);
        Assert.True(project.IsExternallyManaged);
        Assert.Equal(2, project.ExternalContainerCount);
        Assert.Equal(1, project.ExternalRunningCount);
        Assert.Equal(["api", "worker"], project.Services.Select(service => service.Name));
        Assert.Equal(["C:\\shop\\compose.yml"], project.ComposeFiles);
    }

    [Fact]
    public async Task IgnoresDesktopManagedSharedLabelProjects()
    {
        var projects = await ComposeProjectDiscovery.DiscoverAsync([new ContainerInfo { Id = "one" }], (_, _) =>
            Task.FromResult(new CommandResult
            {
                StandardOutput = "{\"Labels\":{\"com.wslc-compose.project\":\"shop\",\"com.wslc-compose.managed-by\":\"wsl-container-desktop\"}}",
            }));

        Assert.Empty(projects);
    }

    [Fact]
    public async Task IgnoresLegacyDesktopOnlyLabels()
    {
        var projects = await ComposeProjectDiscovery.DiscoverAsync([new ContainerInfo { Id = "one" }], (_, _) =>
            Task.FromResult(new CommandResult
            {
                StandardOutput = "{\"Labels\":{\"com.wsldesktop.project\":\"shop\"}}",
            }));

        Assert.Empty(projects);
    }
}
