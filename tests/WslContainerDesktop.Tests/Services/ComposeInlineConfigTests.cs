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

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Covers inline config text, persisted sources, fingerprints and payload exclusion.</summary>
public sealed class ComposeInlineConfigTests
{
    [Fact]
    public void Issue147ImportsInlinePgAdminJsonWithoutASourceDirectory()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              pgadmin:
                image: dpage/pgadmin4:latest
                ports:
                  - "5050:80"
                configs:
                  - source: pgadmin_servers.json
                    target: /pgadmin4/servers.json
            configs:
              pgadmin_servers.json:
                content: |
                  {
                    "Servers": {
                      "1": {
                        "Group": "Servers",
                        "Name": "local docker",
                        "Host": "postgres",
                        "Port": 5432,
                        "MaintenanceDB": "postgres",
                        "Username": "rds",
                        "UseSSHTunnel": 0,
                        "ConnectionParameters": {
                          "sslmode": "prefer"
                        }
                      }
                    }
                  }
            """ + "\n");

        var config = Assert.Single(project.Configs);
        Assert.Null(config.File);
        Assert.EndsWith("}\n", config.Content!);
        using var json = JsonDocument.Parse(config.Content!);
        Assert.Equal("postgres", json.RootElement.GetProperty("Servers").GetProperty("1").GetProperty("Host").GetString());
        var mount = Assert.Single(Assert.Single(project.Services).Configs);
        Assert.Equal("pgadmin_servers.json", mount.Source);
        Assert.Equal("/pgadmin4/servers.json", mount.Target);
        Assert.Empty(project.Warnings);
    }

    [Theory]
    [InlineData("''", "")]
    [InlineData("'  '", "  ")]
    [InlineData("'snowman \u2603'", "snowman \u2603")]
    [InlineData("|\n      first\n      second\n", "first\nsecond\n")]
    [InlineData("|-\n      first\n      second\n", "first\nsecond")]
    [InlineData("|+\n      first\n\n", "first\n\n")]
    [InlineData(">-\n      first\n      second\n", "first second")]
    [InlineData("'${WCD_INLINE_VALUE} $$WCD_INLINE_VALUE'", "resolved $WCD_INLINE_VALUE")]
    public void ResolvedTextHasExactUtf8Bytes(string yamlValue, string expected)
    {
        var project = ComposeImporter.ParseProject(
            "services: {web: {image: fixture, configs: [settings]}}\nconfigs:\n  settings:\n    content: " + yamlValue,
            new Dictionary<string, string> { ["WCD_INLINE_VALUE"] = "resolved" });
        var config = Assert.Single(project.Configs);
        Assert.Equal(expected, config.Content);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), ComposeConfigContent.GetBytes(config.Content!));
        Assert.Equal("/settings", Assert.Single(project.Services[0].Configs).Target);
    }

    [Theory]
    [InlineData("configs: {settings: {content: null}}")]
    [InlineData("configs: {settings: {content: [synthetic-private-inline]}}")]
    [InlineData("configs: {settings: {content: {key: synthetic-private-inline}}}")]
    [InlineData("configs: {settings: {content: synthetic-private-inline, file: null}}")]
    [InlineData("configs: {settings: {content: synthetic-private-inline, external: true}}")]
    [InlineData("configs: {settings: {content: synthetic-private-inline, external: {name: settings}}}")]
    [InlineData("configs: {settings: {content: synthetic-private-inline, external: invalid}}")]
    [InlineData("configs: {settings: {content: synthetic-private-inline, name: []}}")]
    [InlineData("configs: {settings: {environment: synthetic-private-inline}}")]
    [InlineData("secrets: {settings: {content: synthetic-private-inline}}")]
    public void InvalidSourceShapesRejectWithoutEchoingContent(string resources)
    {
        var error = Assert.Throws<ComposeConfigurationException>(() =>
            ComposeImporter.ParseProject("services: {web: {image: fixture}}\n" + resources));
        Assert.DoesNotContain("synthetic-private-inline", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void InlineSourcesRoundTripAndLegacyDefinitionsKeepTheirJsonShape()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "inline-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = InlineProject("");
            var store = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, directory);
            store.Save(project);
            var loaded = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, directory).Get(project.Name)!;
            Assert.Equal("", Assert.Single(loaded.Configs).Content);
            Assert.Equal(ComposeReconciliationPlanner.Fingerprint(project, project.Services[0]),
                ComposeReconciliationPlanner.Fingerprint(loaded, loaded.Services[0]));

            var legacy = new ComposeSecret { Name = "legacy", File = "settings.bin" };
            Assert.DoesNotContain("Content", JsonSerializer.Serialize(legacy));
            Assert.Null(JsonSerializer.Deserialize<ComposeSecret>("""{"Name":"legacy","File":"settings.bin"}""")!.Content);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ContentAndTargetChangesHaveSelectiveStableFingerprints()
    {
        var project = InlineProject("original");
        var web = project.Services[0];
        var worker = new ComposeService { Name = "worker", Options = new() { Image = "fixture" } };
        project.Services.Add(worker);
        var before = ComposeReconciliationPlanner.Fingerprint(project, web);
        var unaffected = ComposeReconciliationPlanner.Fingerprint(project, worker);
        project.Configs[0].Content = "changed";
        Assert.NotEqual(before, ComposeReconciliationPlanner.Fingerprint(project, web));
        Assert.Equal(unaffected, ComposeReconciliationPlanner.Fingerprint(project, worker));
        before = ComposeReconciliationPlanner.Fingerprint(project, web);
        Assert.Equal(before, ComposeReconciliationPlanner.Fingerprint(project, web));
        web.Configs[0].Target = "/different";
        Assert.NotEqual(before, ComposeReconciliationPlanner.Fingerprint(project, web));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("external")]
    [InlineData("secret")]
    [InlineData("oversize")]
    [InlineData("aggregate")]
    [InlineData("invalid-unicode")]
    public void PersistedInlineDefinitionsCannotBypassValidation(string invalid)
    {
        var project = InlineProject("synthetic-private-inline");
        switch (invalid)
        {
            case "file": project.Configs[0].File = "file"; break;
            case "external": project.Configs[0].External = true; break;
            case "secret": project.Secrets.Add(new() { Name = "token", Content = "private" }); break;
            case "oversize": project.Configs[0].Content = new string('x', 8_000_001); break;
            case "aggregate":
                project.Configs[0].Content = new string('x', 4_000_001);
                project.Configs.Add(new() { Name = "second", Content = project.Configs[0].Content });
                break;
            case "invalid-unicode": project.Configs[0].Content = "\ud800"; break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => ComposeConfigContent.Validate(project));
        Assert.DoesNotContain("synthetic-private-inline", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Throws<InvalidOperationException>(() =>
            ComposeReconciliationPlanner.Fingerprint(project, project.Services[0]));
        var plan = ComposeReconciliationPlanner.Plan(project, new(), [], new Dictionary<string, ContainerNetworkState>());
        Assert.False(plan.CanApply);
        Assert.Equal(ComposeServiceAction.Blocked, Assert.Single(plan.Services).Action);
    }

    [Fact]
    public void InlineValidationCoversAllReplicasButDoesNotCacheAcrossPlans()
    {
        var project = InlineProject("original");
        project.Services[0].Replicas = 3;
        project.Services.Add(new() { Name = "worker", Replicas = 2, Options = new() { Image = "fixture" } });
        var request = new ComposeOperationRequest();
        var inventory = Array.Empty<ContainerInfo>();
        var inspections = new Dictionary<string, ContainerNetworkState>();
        var initial = ComposeReconciliationPlanner.Plan(project, request, inventory, inspections);
        Assert.True(initial.CanApply);
        Assert.Equal(5, initial.Services.Count);

        project.Configs.Add(new() { Name = "ungranted", Content = "\ud800" });
        var invalid = ComposeReconciliationPlanner.Plan(project, request, inventory, inspections);
        Assert.False(invalid.CanApply);
        Assert.Equal(5, invalid.Services.Count);
        Assert.All(invalid.Services, entry => Assert.Equal(ComposeServiceAction.Blocked, entry.Action));

        project.Configs.RemoveAt(1);
        project.Configs[0].Content = "changed";
        var repaired = ComposeReconciliationPlanner.Plan(project, request, inventory, inspections);
        Assert.True(repaired.CanApply);
        Assert.Equal(5, repaired.Services.Count);
        Assert.All(repaired.Services, entry =>
        {
            var before = initial.Services.Single(prior => prior.InstanceKey == entry.InstanceKey);
            if (entry.Service.Name == "web") Assert.NotEqual(before.Fingerprint, entry.Fingerprint);
            else Assert.Equal(before.Fingerprint, entry.Fingerprint);
            Assert.Equal(ComposeReconciliationPlanner.Fingerprint(project, entry.Service), entry.Fingerprint);
        });
    }

    [Fact]
    public void InlinePayloadIsExcludedStructurallyFromPreview()
    {
        const string content = """{"ordinary_key":"synthetic-private-inline"}""";
        var project = InlineProject(content);
        var plan = ComposeReconciliationPlanner.Plan(project, new(), [], new Dictionary<string, ContainerNetworkState>());
        var preview = new ComposePreviewProjection(project).Create(project, new(), plan);
        var serialized = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("synthetic-private-inline", serialized);
        Assert.DoesNotContain("ordinary_key", serialized);
        Assert.Contains(preview.Settings, row => row.Setting == "configs" && row.EffectiveValue.Contains("/settings"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("web")]
    [InlineData("settings")]
    public void InlineValuesDoNotRedactUnrelatedPreviewFields(string content)
    {
        var project = InlineProject(content);
        project.Services[0].Options.PortMappings = ["8080:80"];
        project.Services[0].Options.CpuLimit = "0.25";
        project.Services[0].Configs[0].Target = "/directory with spaces/settings";
        var request = new ComposeOperationRequest();
        var plan = ComposeReconciliationPlanner.Plan(project, request, [], new Dictionary<string, ContainerNetworkState>());
        var preview = new ComposePreviewProjection(project).Create(project, request, plan);

        project.Configs[0].Content = "different-inline-payload";
        var baseline = new ComposePreviewProjection(project).Create(project, request, plan);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(preview));
        Assert.Contains(preview.Settings, row => row.Setting == "ports" && row.EffectiveValue == "8080:80");
        Assert.Contains(preview.Settings, row => row.Setting == "configs" &&
            row.EffectiveValue.Contains("settings \u2192 /directory with spaces/settings", StringComparison.Ordinal));
    }

    internal static ComposeProject InlineProject(string content) => new()
    {
        Name = "inline-demo",
        Configs = [new() { Name = "settings", Content = content }],
        Services = [new()
        {
            Name = "web", Options = new() { Image = "fixture" },
            Configs = [new() { Source = "settings", Target = "/settings" }],
        }],
    };
}
