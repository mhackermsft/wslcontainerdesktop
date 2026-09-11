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
/// The files under <c>examples/compose</c> are documentation users follow literally, but nothing
/// executed them, so they drifted: the "supported" stack became unimportable when services without
/// an image started being rejected, and the "unsupported" stack kept listing replicas and
/// multi-network as unsupported after both were implemented. These tests import the real shipped
/// files so that drift fails the build instead of misleading a reader.
/// </summary>
public class ShippedComposeExampleTests
{
    private static string Example(params string[] segments) =>
        Path.Combine([AppContext.BaseDirectory, "Examples", "compose", .. segments]);

    private static ComposeProject Import(string path) =>
        ComposeImporter.ParseProject(File.ReadAllText(path), null, Path.GetDirectoryName(path));

    [Theory]
    [InlineData("fuller-stack.yml")]
    [InlineData("supported", "docker-compose.yml")]
    [InlineData("unsupported", "docker-compose.yml")]
    public void EveryShippedExampleIsPresentAndImports(params string[] segments)
    {
        var path = Example(segments);
        Assert.True(File.Exists(path), $"Shipped example is missing from the test output: {path}");

        // Import must not throw: an example a reader cannot import is worse than no example.
        var project = Import(path);
        Assert.NotEmpty(project.Services);
    }

    /// <summary>
    /// This file's header promises import completes with no unsupported-features dialog. A single
    /// warning here means either the example or that promise is wrong.
    /// </summary>
    [Fact]
    public void SupportedExampleImportsWithoutAnyWarning()
    {
        var project = Import(Example("supported", "docker-compose.yml"));

        Assert.Empty(project.Warnings);
        Assert.Equal("supported-demo", project.Name);
    }

    /// <summary>
    /// The example advertises include, extends, an auto-merged override file, env_file and
    /// interpolation. Asserting the merged result keeps a restructure from quietly dropping one.
    /// </summary>
    [Fact]
    public void SupportedExampleStillDemonstratesEveryFeatureItAdvertises()
    {
        var project = Import(Example("supported", "docker-compose.yml"));
        var web = Assert.Single(project.Services, s => s.Name == "web");

        // include: common.yml contributes the helper service.
        Assert.Contains(project.Services, s => s.Name == "logs");
        // extends: web-base.yml supplies shared environment and labels.
        Assert.Contains("SERVICE_TIER=web", web.Options.EnvironmentVariables);
        Assert.Contains("LOG_LEVEL=info", web.Options.EnvironmentVariables);
        Assert.Equal("frontend", web.Options.Labels["com.example.role"]);
        // docker-compose.override.yml is merged automatically.
        Assert.Contains("OVERRIDDEN=yes", web.Options.EnvironmentVariables);
        // env_file long form.
        Assert.Contains("FEATURE_FLAG=on", web.Options.EnvironmentVariables);
        // ${NGINX_TAG:-alpine} interpolation.
        Assert.Equal("nginx:alpine", web.Options.Image);
        // profiles: the debug service is active because .env sets COMPOSE_PROFILES=debug.
        Assert.Contains(project.Services, s => s.Name == "debug");
    }

    /// <summary>
    /// The extends base has no image on purpose. It must stay out of <c>include:</c>, because an
    /// included file becomes part of the project and a project service requires an image or build.
    /// </summary>
    [Fact]
    public void ExtendsBaseIsNotPulledInAsAProjectService()
    {
        var project = Import(Example("supported", "docker-compose.yml"));

        Assert.DoesNotContain(project.Services, s => s.Name == "web-base");
        Assert.All(project.Services, s => Assert.False(
            string.IsNullOrWhiteSpace(s.Options.Image) && s.Build is null,
            $"Service '{s.Name}' has neither image nor build and would be rejected on import."));
    }

    [Theory]
    [InlineData("privileged")]
    [InlineData("cap_add")]
    [InlineData("cap_drop")]
    [InlineData("read_only")]
    [InlineData("init")]
    [InlineData("devices")]
    [InlineData("sysctls")]
    [InlineData("logging")]
    [InlineData("mac_address")]
    [InlineData("pid")]
    [InlineData("ipc")]
    [InlineData("security_opt")]
    [InlineData("group_add")]
    [InlineData("userns_mode")]
    [InlineData("cgroup_parent")]
    public void UnsupportedExampleStillWarnsAboutEveryKeyItsHeaderLists(string key)
    {
        var project = Import(Example("unsupported", "docker-compose.yml"));

        Assert.Contains(project.Warnings, w => w.Contains($"'{key}'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedExampleStillWarnsAboutTheUnknownTopLevelKey() =>
        Assert.Contains(Import(Example("unsupported", "docker-compose.yml")).Warnings,
            w => w.Contains("unknown-top-level", StringComparison.Ordinal));

    /// <summary>
    /// Extensions and the obsolete <c>version</c> key are deliberately not flagged; the header
    /// says so, and warning about them would train readers to ignore the dialog.
    /// </summary>
    [Theory]
    [InlineData("x-notes")]
    [InlineData("version")]
    public void UnsupportedExampleDoesNotWarnAboutIgnoredKeys(string key)
    {
        var project = Import(Example("unsupported", "docker-compose.yml"));

        Assert.DoesNotContain(project.Warnings, w => w.Contains(key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Replicas and multi-network were both listed as unsupported long after they shipped. If
    /// either is ever flagged or dropped again, the example's "not flagged, because they are
    /// supported" list is wrong and must be corrected with it.
    /// </summary>
    [Fact]
    public void UnsupportedExampleActuallyHonorsTheFeaturesItListsAsSupported()
    {
        var project = Import(Example("unsupported", "docker-compose.yml"));
        var app = Assert.Single(project.Services, s => s.Name == "app");

        Assert.Equal(3, app.Replicas);
        Assert.Equal(["net1", "net2"], app.Options.GetNetworkAttachments().Select(a => a.Network));
        Assert.Equal("1.0", app.Options.CpuLimit);
        Assert.DoesNotContain(project.Warnings, w => w.Contains("replicas", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The multi-network warning is an older-engine fallback note, not a hard limitation.</summary>
    [Fact]
    public void MultiNetworkWarningDescribesTheLegacyFallbackRatherThanADroppedNetwork()
    {
        var warning = Assert.Single(Import(Example("unsupported", "docker-compose.yml")).Warnings,
            w => w.Contains("multiple networks", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("Older engines", warning, StringComparison.Ordinal);
        Assert.Contains("retained", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void FullerStackExampleImportsWithOnlyTheMultiNetworkNote()
    {
        var project = Import(Example("fuller-stack.yml"));

        Assert.Equal("fuller-demo", project.Name);
        var warning = Assert.Single(project.Warnings);
        Assert.Contains("multiple networks", warning, StringComparison.OrdinalIgnoreCase);
        // The build service must keep exercising build support.
        Assert.Contains(project.Services, s => s.Build is not null);
    }
}
