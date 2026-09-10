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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class DevContainerComposeImportTests
{
    [Fact]
    public async Task OrderedFilesMergeBeforeValidationWithFirstFilePathOwnership()
    {
        using var fixture = new Fixture();
        fixture.Write("base/compose.yaml", """
            services:
              app:
                image: fixture:base
                environment: {KEEP: base, CHANGE: base}
                volumes: ['./data:/data']
            """);
        fixture.Write("other/override.yaml", """
            include: [included.yaml]
            services:
              app:
                environment: {CHANGE: override}
                build: ./build
                env_file: ./values.env
                ports: ['8080:80']
            """);
        fixture.Write("base/included.yaml", "services: {helper: {image: fixture:helper}}");
        fixture.Write("base/values.env", "FROM_FILE=first-directory");
        fixture.Write("base/compose.override.yaml", "services: {unexpected: {image: never-load}}");
        fixture.Write("other/values.env", "FROM_FILE=wrong-directory");
        var result = await fixture.Import(["base/compose.yaml", "other/override.yaml"]);
        Assert.True(result.Success, result.ErrorMessage);
        var project = result.Config!.Compose!.Project;
        Assert.Equal(["app", "helper"], project.Services.Select(s => s.Name));
        var app = project.Services[0];
        Assert.Equal("fixture:base", app.Options.Image);
        Assert.Contains("KEEP=base", app.Options.EnvironmentVariables);
        Assert.Contains("CHANGE=override", app.Options.EnvironmentVariables);
        Assert.Contains("FROM_FILE=first-directory", app.Options.EnvironmentVariables);
        Assert.Contains(fixture.PathOf("base/data") + ":/data", app.Options.Volumes);
        Assert.Equal(fixture.PathOf("base/build"), app.Build!.Context);
        Assert.Contains("8080:80", app.Options.PortMappings);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public async Task PartialBaseCanReceiveImageInLaterLayer()
    {
        using var fixture = new Fixture();
        fixture.Write("base.yaml", "services: {app: {environment: {KEEP: base}}}");
        fixture.Write("override.yaml", "services: {app: {image: fixture}}");
        var result = await fixture.Import(["base.yaml", "override.yaml"]);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("KEEP=base", Assert.Single(result.Config!.Compose!.Project.Services).Options.EnvironmentVariables);
    }

    [Theory]
    [InlineData("services: {app: {environment: {ONLY: value}}}", "neither image nor build")]
    [InlineData("services: {app: {image: secret-value, command: [broken}", "Invalid YAML")]
    public async Task InvalidFinalConfigurationReturnsComposeDiagnosticNotJsonc(string yaml, string diagnostic)
    {
        using var fixture = new Fixture();
        fixture.Write("base.yaml", yaml);
        var result = await fixture.Import(["base.yaml"]);
        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.Contains(diagnostic, result.ErrorMessage);
        Assert.DoesNotContain("JSONC", result.ErrorMessage);
        Assert.DoesNotContain("secret-value", result.ErrorMessage);
        Assert.DoesNotContain(fixture.PathOf("base.yaml"), result.ErrorMessage);
    }

    [Fact]
    public async Task MissingLaterFileIsNotSilentlyOmitted()
    {
        using var fixture = new Fixture();
        fixture.Write("base.yaml", "services: {app: {image: fixture}}");
        var result = await fixture.Import(["base.yaml", "private-missing.yaml"]);
        Assert.False(result.Success);
        Assert.Contains("Compose files[2]", result.ErrorMessage);
        Assert.Contains("required file is missing", result.ErrorMessage);
        Assert.DoesNotContain("private-missing", result.ErrorMessage);
    }

    [Fact]
    public async Task InterpolationWarningsSurviveInnerOuterAndSerialization()
    {
        using var fixture = new Fixture();
        var variable = "WCD_UNSET_" + Guid.NewGuid().ToString("N");
        fixture.Write("base.yaml", $"services: {{app: {{image: fixture, environment: {{UNSET: '${{{variable}}}'}}}}}}");
        fixture.Write("override.yaml", "services: {app: {environment: {OTHER: value}}}");
        var result = await fixture.Import(["base.yaml", "override.yaml"]);
        Assert.True(result.Success, result.ErrorMessage);
        var warning = Assert.Single(result.Config!.Compose!.Project.Warnings);
        Assert.Contains(variable, warning);
        Assert.Contains(warning, result.Warnings);
        Assert.Contains(warning, result.Config.Warnings);
        var persisted = JsonSerializer.Deserialize<DevContainerConfig>(JsonSerializer.Serialize(result.Config))!;
        Assert.Contains(warning, persisted.Compose!.Project.Warnings);
        Assert.Contains(warning, persisted.Warnings);
    }

    [Fact]
    public async Task MalformedLaterOverrideReturnsItsOwnSafeSourceDiagnostic()
    {
        using var fixture = new Fixture();
        fixture.Write("base.yaml", "services: {app: {image: fixture}}");
        fixture.Write("private-override.yaml", "services: {app: {environment: [secret-value}");
        var result = await fixture.Import(["base.yaml", "private-override.yaml"]);
        Assert.False(result.Success);
        Assert.Contains("Compose files[2]", result.ErrorMessage);
        Assert.Contains("Invalid YAML", result.ErrorMessage);
        Assert.DoesNotContain("secret-value", result.ErrorMessage);
        Assert.DoesNotContain("private-override", result.ErrorMessage);
        Assert.DoesNotContain("JSONC", result.ErrorMessage);
    }

    [Fact]
    public async Task EmptySubstitutedListMemberCannotDisappear()
    {
        using var fixture = new Fixture();
        fixture.Write("base.yaml", "services: {app: {image: fixture}}");
        var variable = "WCD_UNSET_" + Guid.NewGuid().ToString("N");
        var result = await fixture.Import(["base.yaml", "${localEnv:" + variable + "}"]);
        Assert.False(result.Success);
        Assert.Contains("nonempty file paths", result.ErrorMessage);
    }

    [Fact]
    public void ExistingSavedProjectsWithoutWarningsRemainReadable()
    {
        var project = JsonSerializer.Deserialize<ComposeProject>("{}")!;
        Assert.Empty(project.Warnings);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"base.yaml\", \"\"]")]
    [InlineData("[\"base.yaml\", 5]")]
    public async Task InvalidExplicitFileListsFailRatherThanPartiallyLoading(string fileList)
    {
        using var fixture = new Fixture();
        fixture.Write("base.yaml", "services: {app: {image: fixture}}");
        fixture.Write("devcontainer.json", "{\"service\":\"app\",\"dockerComposeFile\":" + fileList + "}");
        var result = await fixture.Importer.ImportAsync(fixture.Root);
        Assert.False(result.Success);
        Assert.DoesNotContain("JSONC", result.ErrorMessage);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "dev-compose-" + Guid.NewGuid().ToString("N"));
        public DevContainerImporter Importer { get; } = new(NullLogger<DevContainerImporter>.Instance);
        public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, string contents)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public Task<DevContainerImportResult> Import(string[] files)
        {
            Write("devcontainer.json", JsonSerializer.Serialize(new { service = "app", dockerComposeFile = files }));
            return Importer.ImportAsync(Root);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
