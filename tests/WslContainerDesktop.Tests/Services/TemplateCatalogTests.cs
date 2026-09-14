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
/// A template that fails to import is worse than no template, and a host port that collides with
/// another template turns Launch into an error the user cannot act on. These check the built-in
/// catalog itself rather than any particular feature.
/// </summary>
public class TemplateCatalogTests
{
    private static IReadOnlyList<StackTemplate> Templates =>
        new TemplateCatalog(NetworkTestProxy.Create<IUserTemplateStore>((method, _) => method.Name switch
        {
            "get_Templates" => new List<StackTemplate>(),
            // The catalog subscribes to change notifications on construction.
            "add_Changed" or "remove_Changed" => null,
            _ => throw new InvalidOperationException($"Unexpected user-template call: {method.Name}"),
        })).Templates;

    [Fact]
    public void EveryComposeTemplateImportsCleanly()
    {
        foreach (var template in Templates.Where(t => t.Kind == StackTemplateKind.Compose))
        {
            var project = ComposeImporter.ParseProject(template.ComposeYaml!);

            Assert.NotEmpty(project.Services);
            Assert.True(project.Warnings.Count == 0,
                $"Template '{template.Id}' imports with warnings: {string.Join("; ", project.Warnings)}");
        }
    }

    /// <summary>
    /// Launch publishes fixed host ports, so two templates claiming the same one means the second
    /// cannot start. Container-side ports may of course repeat.
    /// </summary>
    [Fact]
    public void NoTwoTemplatesClaimTheSameHostPort()
    {
        var claims = new List<(string Template, string Port)>();
        foreach (var template in Templates)
        {
            var mappings = template.Kind == StackTemplateKind.Compose
                ? ComposeImporter.ParseProject(template.ComposeYaml!).Services.SelectMany(s => s.Options.PortMappings)
                : template.RunOptions?.PortMappings ?? Enumerable.Empty<string>();
            foreach (var mapping in mappings)
            {
                // host[:container] or ip:host:container; the host port is the part before the last colon.
                var parts = mapping.Split(':');
                if (parts.Length >= 2)
                    claims.Add((template.Id, parts[^2]));
            }
        }

        var collisions = claims.GroupBy(c => c.Port)
            .Where(g => g.Select(c => c.Template).Distinct().Count() > 1)
            .Select(g => $"port {g.Key}: {string.Join(", ", g.Select(c => c.Template).Distinct())}")
            .ToArray();

        Assert.True(collisions.Length == 0, "Templates publish conflicting host ports: " + string.Join(" | ", collisions));
    }

    [Fact]
    public void TemplateIdsAreUnique()
    {
        var duplicates = Templates.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();

        Assert.True(duplicates.Length == 0, "Duplicate template ids: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// The Azure emulators were each verified against the real engine; these pin the details that
    /// verification established, so a later edit cannot silently break a working stack.
    /// </summary>
    [Theory]
    [InlineData("azurite")]
    [InlineData("cosmosdb")]
    [InlineData("servicebus")]
    [InlineData("eventhubs")]
    public void AzureEmulatorsAreGroupedTogether(string id) =>
        Assert.Equal("Azure", Assert.Single(Templates, t => t.Id == id).Category);

    /// <summary>
    /// Both messaging emulators exit when SQL Server is not accepting connections yet, so they must
    /// wait for a real query to succeed rather than for the container to merely exist.
    /// </summary>
    [Theory]
    [InlineData("servicebus")]
    [InlineData("eventhubs")]
    public void MessagingEmulatorsWaitForSqlToBeHealthyNotMerelyStarted(string id)
    {
        var project = ComposeImporter.ParseProject(Assert.Single(Templates, t => t.Id == id).ComposeYaml!);

        var emulator = Assert.Single(project.Services, s => s.Name == id);
        var sql = Assert.Single(emulator.DependsOn, d => d.ServiceName == "sql");
        Assert.Equal(DependencyCondition.ServiceHealthy, sql.Condition);

        // The gate is only real if SQL actually declares a health check.
        Assert.NotNull(Assert.Single(project.Services, s => s.Name == "sql").Health?.DesiredHealth);
    }

    /// <summary>The Event Hubs emulator reaches Azurite over the network, which the default
    /// loopback binding would refuse.</summary>
    [Fact]
    public void EventHubsAzuriteListensOnAllInterfaces()
    {
        var project = ComposeImporter.ParseProject(Assert.Single(Templates, t => t.Id == "eventhubs").ComposeYaml!);
        var azurite = Assert.Single(project.Services, s => s.Name == "azurite");

        Assert.Contains("--blobHost 0.0.0.0", azurite.Options.Command);
        Assert.Contains("--queueHost 0.0.0.0", azurite.Options.Command);
    }
}
