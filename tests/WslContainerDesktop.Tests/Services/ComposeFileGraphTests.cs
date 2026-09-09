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
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeFileGraphTests
{
    private const string PrivateInput = "synthetic-private-input";

    [Fact]
    public void ReplicaDefaultsResolveThroughExtendsAndOverridesBeforeValidation()
    {
        using var files = new Fixture();
        files.Write("base.yaml", "services: {base: {image: fixture, scale: 2, deploy: {replicas: 2}}}");
        var project = files.Parse("""
            services:
              web:
                extends: {file: base.yaml, service: base}
                scale: 3
                deploy: {replicas: 3}
            """);
        Assert.Equal(3, Assert.Single(project.Services).Replicas);
    }

    [Fact]
    public void ReplicaMismatchInIncludedGraphRejectsEntireImport()
    {
        using var files = new Fixture();
        files.Write("child.yaml", "services: {worker: {image: fixture, scale: 2, deploy: {replicas: 3}}}");
        Assert.Throws<ComposeConfigurationException>(() =>
            files.Parse("include: [child.yaml]\nservices: {main: {image: fixture}}"));
    }

    [Theory]
    [InlineData("services")]
    [InlineData("networks")]
    [InlineData("volumes")]
    [InlineData("configs")]
    [InlineData("secrets")]
    public void IncludeResourceConflictsRejectInsteadOfMerging(string kind)
    {
        using var files = new Fixture();
        var basis = kind == "services" ? "{image: fixture:base}" : "{external: true, name: base}";
        var child = kind == "services" ? "{image: fixture:child}" : "{external: true, name: child}";
        files.Write("child.yaml", $"{kind}: {{shared: {child}}}");
        var error = files.Error($"include: [child.yaml]\n{kind}: {{shared: {basis}}}", kind, "duplicate");
        Assert.Contains("include", error.Message);
        Assert.Contains("does not merge", error.Message);
    }

    [Theory]
    [InlineData("services")]
    [InlineData("networks")]
    [InlineData("volumes")]
    [InlineData("configs")]
    [InlineData("secrets")]
    public void IdenticalIncludeResourcesAreAcceptedIdempotently(string kind)
    {
        using var files = new Fixture();
        var definition = kind == "services" ? "{image: fixture}" : "{external: true, name: shared}";
        var resource = $"{kind}: {{shared: {definition}}}";
        files.Write("child.yaml", resource);
        var project = files.Parse("include: [child.yaml]\n" + resource +
            (kind == "services" ? "" : "\nservices: {main: {image: fixture}}"));
        var count = kind switch
        {
            "services" => project.Services.Count,
            "networks" => project.Networks.Count,
            "volumes" => project.Volumes.Count,
            "configs" => project.Configs.Count,
            _ => project.Secrets.Count,
        };
        Assert.Equal(1, count);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void DiamondIncludesShareIdenticalLeafResourcesWithoutFalseCycleOrDuplicates()
    {
        using var files = new Fixture();
        files.Write(@"shared\leaf.yaml", """
            services: {shared: {image: fixture, volumes: ['./data:/data']}}
            networks: {shared: {external: true}}
            volumes: {shared: {external: true}}
            secrets: {shared: {external: true}}
            configs: {shared: {external: true}}
            """);
        files.Write(@"left\compose.yaml", """
            include: [../shared/leaf.yaml]
            services: {left: {image: fixture}}
            """);
        files.Write(@"right\compose.yaml", """
            include: [../shared/./leaf.yaml]
            services: {right: {image: fixture}}
            """);
        var project = files.Parse("include: [left/compose.yaml, right/compose.yaml]");
        Assert.Equal(["left", "right", "shared"], project.Services.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal(files.PathOf(@"shared\data") + ":/data",
            Assert.Single(project.Services.Single(s => s.Name == "shared").Options.Volumes));
        Assert.Single(project.Networks);
        Assert.Single(project.Volumes);
        Assert.Single(project.Secrets);
        Assert.Single(project.Configs);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void SiblingIncludesConflictEvenWhenParentDoesNotDeclareTheResource()
    {
        using var files = new Fixture();
        files.Write("a.yaml", "services: {shared: {image: fixture:a}}");
        files.Write("b.yaml", "services: {shared: {image: fixture:b}}");
        files.Error("include: [a.yaml, b.yaml]", "include[2]", "duplicate");
    }

    [Theory]
    [InlineData("services")]
    [InlineData("networks")]
    [InlineData("volumes")]
    [InlineData("configs")]
    [InlineData("secrets")]
    public void IncludedResourceListsRejectInsteadOfDisappearing(string kind)
    {
        using var files = new Fixture();
        files.Write(PrivateInput, $"{kind}: [synthetic-private-resource]");
        var error = files.Error($"include: [{PrivateInput}]\nservices: {{main: {{image: fixture}}}}",
            "include[1]", kind);
        Assert.DoesNotContain("synthetic-private-resource", error.ToString());
    }

    [Fact]
    public void IncludePathListUsesOverrideMergingAndFirstProjectPathOwnership()
    {
        using var files = new Fixture();
        files.Write(@"child\base.yaml", """
            services:
              worker:
                image: fixture:base
                environment: {KEEP: base, WINNER: base}
                volumes: ['./old:/data:ro']
            """);
        files.Write(@"layers\override.yaml", """
            services:
              worker:
                image: fixture:override
                environment: {WINNER: override}
                build: ./build
                volumes: [{type: bind, source: ./new, target: /data, read_only: false}]
            """);
        files.Write(@"child\compose.override.yaml", "services: {unexpected: {image: must-not-load}}");
        var worker = Assert.Single(files.Parse("""
            include:
              - path: [child/base.yaml, layers/override.yaml]
            """).Services);
        Assert.Equal("fixture:override", worker.Options.Image);
        Assert.Equal(["KEEP=base", "WINNER=override"], worker.Options.EnvironmentVariables);
        Assert.Equal(files.PathOf(@"child\build"), worker.Build!.Context);
        Assert.Equal(files.PathOf(@"child\new") + ":/data", Assert.Single(worker.Options.Volumes));
    }

    [Fact]
    public void RepeatedIncludePathIsAnotherOverrideLayerNotARecursiveCycle()
    {
        using var files = new Fixture();
        files.Write("first.yaml", """
            services:
              child:
                image: fixture:first
                environment: {WINNER: first}
                dns: [192.0.2.1]
            """);
        files.Write("second.yaml", """
            services:
              child:
                image: fixture:second
                environment: {WINNER: second, KEEP: second}
                dns: [192.0.2.2]
            """);
        var child = Assert.Single(files.Parse("""
            include: [{path: [first.yaml, second.yaml, ./first.yaml]}]
            """).Services);
        Assert.Equal("fixture:first", child.Options.Image);
        Assert.Equal(["WINNER=first", "KEEP=second"], child.Options.EnvironmentVariables);
        Assert.Equal(["192.0.2.1", "192.0.2.2", "192.0.2.1"], child.Options.Dns);
    }

    [Fact]
    public void IncludedUnknownTopLevelOptionsWarnWithLogicalSourceContext()
    {
        using var files = new Fixture();
        files.Write(PrivateInput, """
            services: {child: {image: fixture}}
            unsupported_option: synthetic-private-value
            """);
        var warning = Assert.Single(files.Parse($"include: [{PrivateInput}]").Warnings);
        Assert.Contains("Compose input include[1]", warning);
        Assert.Contains("unsupported_option", warning);
        Assert.DoesNotContain(PrivateInput, warning);
        Assert.DoesNotContain("synthetic-private-value", warning);
    }

    [Fact]
    public void NestedIncludesOwnBindBuildEnvironmentAndFileResources()
    {
        using var files = new Fixture();
        files.Write(@"child\compose.yaml", """
            include: [nested/compose.yaml]
            services: {child: {image: fixture}}
            """);
        files.Write(@"child\nested\compose.yaml", """
            services:
              nested:
                image: fixture
                build: ./build
                volumes: ['./data:/data']
                env_file: ./service.env
                secrets: [token]
                configs: [settings]
            secrets: {token: {file: ./token.bin}}
            configs: {settings: {file: ./settings.bin}}
            """);
        files.Write(@"child\nested\service.env", "WCD_GRAPH_VALUE=nested");
        files.WriteBytes(@"child\nested\token.bin", [0xff, 0x00, 0x80]);
        files.WriteBytes(@"child\nested\settings.bin", [0x00, 0xfe]);
        files.Write(@"child\nested\compose.override.yaml", "not: [valid");
        var project = files.Parse("include: [child/compose.yaml]\nservices: {main: {image: fixture}}");
        Assert.Equal(["main", "child", "nested"], project.Services.Select(s => s.Name));
        var nested = project.Services.Single(s => s.Name == "nested");
        Assert.Equal(files.PathOf(@"child\nested\build"), nested.Build!.Context);
        Assert.Equal(files.PathOf(@"child\nested\data") + ":/data", Assert.Single(nested.Options.Volumes));
        Assert.Equal(["WCD_GRAPH_VALUE=nested"], nested.Options.EnvironmentVariables);
        Assert.Equal(files.PathOf(@"child\nested\token.bin"), Assert.Single(project.Secrets).File);
        Assert.Equal(files.PathOf(@"child\nested\settings.bin"), Assert.Single(project.Configs).File);
    }

    [Fact]
    public void IncludeProjectDirectoryIsRelativeToParentProjectNotIncludedFile()
    {
        using var files = new Fixture();
        files.Write(@"definitions\child.yaml", """
            include: [nested.yaml]
            services: {worker: {image: fixture, build: ./build, volumes: ['./data:/data'], env_file: ./values.env}}
            """);
        files.Write(@"project\nested.yaml", "services: {nested: {image: fixture}}");
        files.Write(@"project\values.env", "WCD_GRAPH_VALUE=project");
        var project = files.Parse("include: [{path: definitions/child.yaml, project_directory: project}]");
        var worker = project.Services.Single(s => s.Name == "worker");
        Assert.Equal(files.PathOf(@"project\build"), worker.Build!.Context);
        Assert.Equal(files.PathOf(@"project\data") + ":/data", Assert.Single(worker.Options.Volumes));
        Assert.Equal(["WCD_GRAPH_VALUE=project"], worker.Options.EnvironmentVariables);
        Assert.Contains(project.Services, s => s.Name == "nested");
    }

    [Fact]
    public void ExplicitIncludeEnvFilesAreRelativeToParentAndParentValuesWin()
    {
        using var files = new Fixture();
        files.Write(".env", "WCD_GRAPH_PARENT=parent\nWCD_GRAPH_EMPTY=parent");
        files.Write("first.env", "WCD_GRAPH_DEFAULT=first\nWCD_GRAPH_PARENT=must-lose\nWCD_GRAPH_EMPTY=must-lose");
        files.Write("second.env", "WCD_GRAPH_DEFAULT=second");
        files.Write(@"child\.env", "malformed-default-must-not-be-read");
        files.Write(@"child\compose.yaml", """
            services:
              child:
                image: fixture
                environment:
                  DEFAULT: '${WCD_GRAPH_DEFAULT}'
                  PARENT: '${WCD_GRAPH_PARENT}'
                  EMPTY: '${WCD_GRAPH_EMPTY-default}'
            """);
        var child = Assert.Single(files.Parse("""
            include: [{path: child/compose.yaml, env_file: [first.env, second.env]}]
            """, new Dictionary<string, string> { ["WCD_GRAPH_EMPTY"] = "" }).Services);
        Assert.Equal(["DEFAULT=second", "PARENT=parent", "EMPTY="], child.Options.EnvironmentVariables);
    }

    [Fact]
    public void DefaultChildDotenvHasLowerPriorityAndDoesNotLeakToSiblingOrParent()
    {
        using var files = new Fixture();
        files.Write(".env", "WCD_GRAPH_PARENT=parent");
        files.Write(@"one\.env", "WCD_GRAPH_CHILD=one\nWCD_GRAPH_PARENT=must-lose");
        files.Write(@"two\.env", "WCD_GRAPH_CHILD=two");
        var template = """
            services:
              SERVICE_NAME:
                image: fixture
                environment: {CHILD: '${WCD_GRAPH_CHILD}', PARENT: '${WCD_GRAPH_PARENT}'}
            """;
        files.Write(@"one\compose.yaml", template.Replace("SERVICE_NAME:", "one:"));
        files.Write(@"two\compose.yaml", template.Replace("SERVICE_NAME:", "two:"));
        var project = files.Parse("""
            include: [one/compose.yaml, two/compose.yaml]
            services: {main: {image: fixture, environment: {CHILD: '${WCD_GRAPH_CHILD:-absent}'}}}
            """);
        Assert.Equal(["CHILD=absent"], project.Services.Single(s => s.Name == "main").Options.EnvironmentVariables);
        Assert.Equal(["CHILD=one", "PARENT=parent"], project.Services.Single(s => s.Name == "one").Options.EnvironmentVariables);
        Assert.Equal(["CHILD=two", "PARENT=parent"], project.Services.Single(s => s.Name == "two").Options.EnvironmentVariables);
    }

    [Fact]
    public void ExternalExtendsRebasesEachSourceAndKeepsCallingInterpolationEnvironment()
    {
        using var files = new Fixture();
        files.Write(".env", "WCD_GRAPH_IMAGE=fixture:parent");
        files.Write(@"bases\.env", "WCD_GRAPH_IMAGE=must-not-load\nmalformed");
        files.Write(@"bases\compose.override.yaml", "services: [must-not-load");
        files.Write(@"bases\base.yaml", """
            services:
              base:
                image: '${WCD_GRAPH_IMAGE}'
                build: ./build
                env_file: ./base.env
                volumes: ['./base-data:/base']
                environment: {ESCAPED: '$$WCD_GRAPH_IMAGE'}
            configs: {unused: {file: missing-do-not-import.bin}}
            """);
        files.Write(@"bases\base.env", "WCD_GRAPH_VALUE=base");
        var child = Assert.Single(files.Parse("""
            services:
              child:
                extends: {file: bases/base.yaml, service: base}
                volumes: ['./child-data:/child']
            """).Services);
        Assert.Equal("fixture:parent", child.Options.Image);
        Assert.Equal(files.PathOf(@"bases\build"), child.Build!.Context);
        Assert.Equal([files.PathOf(@"bases\base-data") + ":/base", files.PathOf("child-data") + ":/child"],
            child.Options.Volumes);
        Assert.Equal(["WCD_GRAPH_VALUE=base", "ESCAPED=$WCD_GRAPH_IMAGE"], child.Options.EnvironmentVariables);
    }

    [Fact]
    public void ExternalInheritanceHopsKeepTheirOwnDefaultBuildAndExplicitPaths()
    {
        using var files = new Fixture();
        files.Write(@"one\base.yaml", """
            services:
              base:
                extends: {file: ../two/grandparent.yaml, service: grandparent}
                env_file: ./one.env
                volumes: ['./one:/one']
            """);
        files.Write(@"one\one.env", "WCD_GRAPH_ORDER=one");
        files.Write(@"two\grandparent.yaml", """
            services:
              grandparent:
                image: fixture
                build: {dockerfile: Containerfile}
                env_file: ./two.env
                volumes: ['./two:/two']
            """);
        files.Write(@"two\two.env", "WCD_GRAPH_ORDER=two");
        var child = Assert.Single(files.Parse("""
            services:
              child:
                extends: {file: one/base.yaml, service: base}
                environment: {INLINE: last}
            """).Services);
        Assert.Equal(files.PathOf("two"), child.Build!.Context);
        Assert.Equal([files.PathOf(@"two\two") + ":/two", files.PathOf(@"one\one") + ":/one"], child.Options.Volumes);
        Assert.Equal(["WCD_GRAPH_ORDER=one", "INLINE=last"], child.Options.EnvironmentVariables);
    }

    [Theory]
    [InlineData("args: {CHILD: child}")]
    [InlineData("dockerfile: Child.Containerfile")]
    public void ChildBuildWithoutContextPreservesExternalInheritedExplicitContext(string childBuild)
    {
        using var files = new Fixture();
        files.Write(@"external\base.yaml", """
            services:
              base:
                image: fixture
                build:
                  context: ./custom-context
                  args: {BASE: base}
                  dockerfile: Base.Containerfile
            """);
        var build = Assert.Single(files.Parse($$$"""
            services:
              child:
                extends: {file: external/base.yaml, service: base}
                build: { {{{childBuild}}} }
            """).Services).Build!;
        Assert.Equal(files.PathOf(@"external\custom-context"), build.Context);
        Assert.Contains("BASE=base", build.Args);
        if (childBuild.StartsWith("args", StringComparison.Ordinal))
        {
            Assert.Equal(["BASE=base", "CHILD=child"], build.Args);
            Assert.Equal("Base.Containerfile", build.Dockerfile);
        }
        else
            Assert.Equal("Child.Containerfile", build.Dockerfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyInheritedBuildDefaultsToExternalSourceEvenWhenChildAddsArguments(bool included)
    {
        using var files = new Fixture();
        files.Write(@"external\base.yaml", "services: {base: {image: fixture, build: {}}}");
        const string child = """
            services:
              child:
                extends: {file: external/base.yaml, service: base}
                build: {args: {CHILD: child}}
            """;
        files.Write("child.yaml", child);
        var build = Assert.Single(files.Parse(included ? "include: [child.yaml]" : child).Services).Build!;
        Assert.Equal(files.PathOf("external"), build.Context);
        Assert.Equal(["CHILD=child"], build.Args);
    }

    [Fact]
    public void OverrideBuildArgumentsDoNotInjectAContextOverTheBaseContext()
    {
        using var files = new Fixture();
        files.Write("compose.override.yaml", """
            services: {child: {build: {args: {LAYER: override}}}}
            """);
        var build = Assert.Single(files.Parse("""
            services: {child: {image: fixture, build: {context: ./custom-context, args: {BASE: base}}}}
            """).Services).Build!;
        Assert.Equal(files.PathOf("custom-context"), build.Context);
        Assert.Equal(["BASE=base", "LAYER=override"], build.Args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinuxEngineBindAndBuildPathsRemainUnchangedAcrossGraphBoundaries(bool extends)
    {
        using var files = new Fixture();
        files.Write(@"child\base.yaml", """
            services:
              base:
                image: fixture
                build: /synthetic-engine/build
                volumes: ['/synthetic-engine/data:/data']
            """);
        var yaml = extends
            ? "services: {child: {extends: {file: child/base.yaml, service: base}}}"
            : "include: [child/base.yaml]";
        var service = Assert.Single(files.Parse(yaml).Services);
        Assert.Equal("/synthetic-engine/build", service.Build!.Context);
        Assert.Equal(["/synthetic-engine/data:/data"], service.Options.Volumes);
    }

    [Fact]
    public void HostFilePathsCanonicalizeLexicalParentSegmentsBeforeReading()
    {
        using var files = new Fixture();
        files.Write("values.env", "WCD_GRAPH_VALUE=canonical");
        files.WriteBytes("resource.bin", [0xff, 0x00]);
        var project = files.Parse("""
            services:
              child:
                image: fixture
                env_file: ./absent-directory/../values.env
            secrets: {token: {file: ./absent-directory/../resource.bin}}
            configs: {settings: {file: ./absent-directory/../resource.bin}}
            """);
        Assert.Equal(["WCD_GRAPH_VALUE=canonical"], Assert.Single(project.Services).Options.EnvironmentVariables);
        Assert.Equal(files.PathOf("resource.bin"), Assert.Single(project.Secrets).File);
        Assert.Equal(files.PathOf("resource.bin"), Assert.Single(project.Configs).File);
    }

    [Theory]
    [InlineData("secrets")]
    [InlineData("configs")]
    public void ExtendsDoesNotImportExternalTopLevelResources(string kind)
    {
        using var files = new Fixture();
        files.Write("base.yaml", $$$"""
            services: {base: {image: fixture, {{{kind}}}: [token]}}
            {{{kind}}}: {token: {external: true}}
            networks: {unused: {}}
            volumes: {unused: {}}
            """);
        var yaml = "services: {child: {extends: {file: base.yaml, service: base}}}";
        files.Error(yaml, kind, "not declared");
        var project = files.Parse(yaml + $"\n{kind}: {{token: {{external: true}}}}");
        Assert.Single(project.Services);
        Assert.Empty(project.Networks);
        Assert.Empty(project.Volumes);
    }

    [Theory]
    [InlineData("services: {child: {extends: {service: missing}}}")]
    [InlineData("services: {child: {extends: {file: base.yaml, service: missing}}}")]
    public void MissingInheritedServiceIsAnError(string yaml)
    {
        using var files = new Fixture();
        files.Write("base.yaml", "services: {base: {image: fixture}}");
        files.Error(yaml, "extends.service", "missing");
    }

    [Fact]
    public void IncludedServicesDoNotSatisfyParentLocalExtends()
    {
        using var files = new Fixture();
        files.Write("child.yaml", "services: {base: {image: fixture}}");
        files.Error("include: [child.yaml]\nservices: {child: {extends: {service: base}}}",
            "extends.service", "missing");
    }

    [Theory]
    [InlineData("local")]
    [InlineData("external")]
    [InlineData("mixed")]
    [InlineData("alias")]
    public void ExtendsCyclesAreRejected(string mode)
    {
        using var files = new Fixture();
        string yaml;
        if (mode == "local")
            yaml = "services: {a: {extends: {service: b}}, b: {extends: {service: a}}}";
        else
        {
            yaml = "services: {child: {extends: {file: a.yaml, service: a}}}";
            files.Write("a.yaml", mode switch
            {
                "mixed" => "services: {a: {extends: {service: b}}, b: {extends: {file: b.yaml, service: b}}}",
                "alias" => "services: {a: {extends: {file: nested/../a.yaml, service: a}}}",
                _ => "services: {a: {extends: {file: b.yaml, service: b}}}",
            });
            files.Write("b.yaml", "services: {b: {extends: {file: a.yaml, service: a}}}");
        }
        files.Error(yaml, "extends", "cycle");
    }

    [Theory]
    [InlineData("a.yaml")]
    [InlineData("./a.yaml")]
    [InlineData("nested/../a.yaml")]
    public void RecursiveIncludeAliasesAreRejected(string alias)
    {
        using var files = new Fixture();
        files.Write("a.yaml", $"include: ['{alias}']");
        files.Error("include: [a.yaml]", "include", "cycle");
    }

    [Fact]
    public void WindowsPathCaseAliasesAreDetectedWithoutFollowingLiveShares()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new Fixture();
        files.Write("a.yaml", "include: [A.YAML]");
        files.Error("include: [a.yaml]", "include", "cycle");
        files.Write("a.yaml", "services: {a: {extends: {file: A.YAML, service: a}}}");
        files.Error("services: {child: {extends: {file: a.yaml, service: a}}}", "extends", "cycle");
    }

    [Fact]
    public void IncludeAndExternalExtendsCycleCannotEscapeTracking()
    {
        using var files = new Fixture();
        files.Write("a.yaml", "include: [b.yaml]");
        files.Write("b.yaml", "services: {child: {extends: {file: base.yaml, service: base}}}");
        files.Write("base.yaml", "services: {base: {extends: {file: b.yaml, service: child}}}");
        files.Error("include: [a.yaml]", "include", "cycle");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverrideMergesVolumeDetailsWhileExtendsReplacesByTarget(bool extends)
    {
        using var files = new Fixture();
        var basis = """
            image: fixture
            volumes: [{type: volume, source: old, target: /data, read_only: true}]
            """;
        string yaml;
        if (extends)
        {
            files.Write("base.yaml", "services:\n  base:\n" + Indent(basis, 4));
            yaml = """
                services:
                  child:
                    extends: {file: base.yaml, service: base}
                    volumes: [{type: volume, source: new, target: /data}]
                """;
        }
        else
        {
            yaml = "services:\n  child:\n" + Indent(basis, 4);
            files.Write("compose.override.yaml", "services: {child: {volumes: [{source: new, target: /data}]}}");
        }
        Assert.Equal(extends ? "new:/data" : "new:/data:ro",
            Assert.Single(Assert.Single(files.Parse(yaml).Services).Options.Volumes));
    }

    [Theory]
    [InlineData("!reset", false)]
    [InlineData("!override", false)]
    [InlineData("!reset", true)]
    [InlineData("!override", true)]
    public void PendingTagsSurviveUnrelatedOverrideUntilInheritanceResolves(string tag, bool included)
    {
        using var files = new Fixture();
        files.Write("inherited.yaml", """
            services:
              inherited:
                image: fixture
                command: [echo, inherited]
                environment: {INHERITED: must-not-return}
                ports: ['8080:80']
            """);
        var child = $$"""
            services:
              child:
                extends: {file: inherited.yaml, service: inherited}
                command: {{tag}} [echo, child]
                environment: {{tag}} {CHILD: child}
                ports: {{tag}} ['9090:90']
            """;
        const string unrelated = "services: {child: {labels: {LAYER: unrelated}}}";
        string yaml;
        if (included)
        {
            files.Write("child.yaml", child);
            files.Write("layer.yaml", unrelated);
            yaml = "include: [{path: [child.yaml, layer.yaml]}]";
        }
        else
        {
            files.Write("compose.override.yaml", unrelated);
            yaml = child;
        }
        var service = Assert.Single(files.Parse(yaml).Services);
        Assert.Equal("fixture", service.Options.Image);
        Assert.Equal("unrelated", service.Options.Labels["LAYER"]);
        if (tag == "!reset")
        {
            Assert.Null(service.Options.Command);
            Assert.Empty(service.Options.EnvironmentVariables);
            Assert.Empty(service.Options.PortMappings);
        }
        else
        {
            Assert.Equal("echo child", service.Options.Command);
            Assert.Equal(["CHILD=child"], service.Options.EnvironmentVariables);
            Assert.Equal(["9090:90"], service.Options.PortMappings);
        }
    }

    [Fact]
    public void OverrideTagOnCollectionsSurvivesAdditionalCollectionMergesBeforeInheritance()
    {
        using var files = new Fixture();
        files.Write("inherited.yaml", """
            services:
              inherited:
                image: fixture
                environment: {INHERITED: must-not-return}
                ports: ['8080:80']
            """);
        files.Write("compose.override.yaml", """
            services:
              child:
                environment: {LAYER: extra}
                ports: ['10000:100']
            """);
        var service = Assert.Single(files.Parse("""
            services:
              child:
                extends: {file: inherited.yaml, service: inherited}
                environment: !override {CHILD: child}
                ports: !override ['9090:90']
            """).Services);
        Assert.Equal(["CHILD=child", "LAYER=extra"], service.Options.EnvironmentVariables);
        Assert.Equal(["9090:90", "10000:100"], service.Options.PortMappings);
    }

    [Fact]
    public void ExtendsDeduplicatesUniqueSequencesButRetainsDnsAndEnvFileDuplicates()
    {
        using var files = new Fixture();
        files.Write("first.env", "WCD_GRAPH_WINNER=first");
        files.Write("second.env", "WCD_GRAPH_WINNER=second");
        files.Write("base.yaml", """
            services:
              base:
                image: fixture
                ports: ['8080:80']
                secrets: [token]
                configs: [settings]
                dns: [192.0.2.1]
                dns_search: [example.invalid]
                env_file: [first.env, second.env]
            """);
        var service = Assert.Single(files.Parse("""
            services:
              child:
                extends: {file: base.yaml, service: base}
                ports: [{target: 80, published: 8080, host_ip: '0.0.0.0'}, '9090:90']
                secrets: [{source: token, target: /run/secrets/token}]
                configs: [{source: settings, target: /settings}]
                dns: [192.0.2.1]
                dns_search: [example.invalid]
                env_file: [first.env]
            secrets: {token: {external: true}}
            configs: {settings: {external: true}}
            """).Services);
        Assert.Equal(["8080:80", "9090:90"], service.Options.PortMappings);
        Assert.Single(service.Secrets);
        Assert.Single(service.Configs);
        Assert.Equal(["192.0.2.1", "192.0.2.1"], service.Options.Dns);
        Assert.Equal(["example.invalid", "example.invalid"], service.Options.DnsSearch);
        Assert.Equal(["WCD_GRAPH_WINNER=first"], service.Options.EnvironmentVariables);
    }

    [Fact]
    public void ExtendsExtraHostsKeepsAllChildAddressesWhileReplacingInheritedHostname()
    {
        using var files = new Fixture();
        files.Write("base.yaml", """
            services:
              base:
                image: fixture
                extra_hosts: [dual:192.0.2.1, keep:192.0.2.2]
            """);
        var child = Assert.Single(files.Parse("""
            services:
              child:
                extends: {file: base.yaml, service: base}
                extra_hosts:
                  dual: [192.0.2.3, '2001:db8::3']
            """).Services);
        Assert.Equal(["keep:192.0.2.2", "dual:192.0.2.3", "dual:2001:db8::3"], child.ExtraHosts);
    }

    [Theory]
    [InlineData("secrets")]
    [InlineData("configs")]
    public void ExtendsRetainsDifferentSequenceEntriesEvenWhenTargetsMatch(string kind)
    {
        using var files = new Fixture();
        files.Write("base.yaml", $$$"""
            services: {base: {image: fixture, {{{kind}}}: [{source: first, target: /shared}]}}
            """);
        var service = Assert.Single(files.Parse($$$"""
            services:
              child:
                extends: {file: base.yaml, service: base}
                {{{kind}}}: [{source: second, target: /shared}]
            {{{kind}}}: {first: {external: true}, second: {external: true}}
            """).Services);
        Assert.Equal(["first", "second"], (kind == "secrets" ? service.Secrets : service.Configs).Select(m => m.Source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExtendsCannotDisableHealthcheckUnlessBaseDisablesIt(bool basis, bool child)
    {
        using var files = new Fixture();
        files.Write("base.yaml",
            $"services: {{base: {{image: fixture, healthcheck: {{disable: {basis.ToString().ToLowerInvariant()}}}}}}}");
        var yaml = $"services: {{child: {{extends: {{file: base.yaml, service: base}}, healthcheck: {{disable: {child.ToString().ToLowerInvariant()}}}}}}}";
        if (child && !basis)
            files.Error(yaml, "extends.healthcheck.disable", "cannot disable");
        else
            Assert.Single(files.Parse(yaml).Services);
    }

    [Theory]
    [InlineData("services: {web: {image: fixture, env_file: synthetic-private-input}}", "env_file")]
    [InlineData("services: {web: {image: fixture, env_file: [{path: synthetic-private-input}]}}", "env_file")]
    [InlineData("services: {web: {image: fixture, env_file: [{path: synthetic-private-input, required: true}]}}", "env_file")]
    [InlineData("include: [synthetic-private-input]", "include")]
    [InlineData("include: [{path: child.yaml, env_file: synthetic-private-input}]", "env_file")]
    [InlineData("services: {web: {extends: {file: synthetic-private-input, service: base}}}", "extends.file")]
    public void RequiredMissingInputsRejectWithValueFreeBreadcrumbs(string yaml, string key)
    {
        using var files = new Fixture();
        files.Write("child.yaml", "services: {child: {image: fixture}}");
        files.Error(yaml, key, "required file is missing");
    }

    [Fact]
    public void OnlyExplicitlyOptionalMissingServiceEnvFileIsSkipped()
    {
        using var files = new Fixture();
        files.Write("present.env", "WCD_GRAPH_VALUE=present");
        var service = Assert.Single(files.Parse("""
            services:
              web:
                image: fixture
                env_file:
                  - {path: missing/optional.env, required: false}
                  - present.env
            """).Services);
        Assert.Equal(["WCD_GRAPH_VALUE=present"], service.Options.EnvironmentVariables);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableEnvFilesFailEvenWhenOptional(bool required)
    {
        using var files = new Fixture();
        files.Write(PrivateInput, "WCD_GRAPH_PRIVATE=do-not-print");
        using var locked = new FileStream(files.PathOf(PrivateInput), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        files.Error($"services: {{web: {{image: fixture, env_file: [{{path: {PrivateInput}, required: {required.ToString().ToLowerInvariant()}}}]}}}}",
            "env_file", "unreadable");
    }

    [Theory]
    [InlineData("include")]
    [InlineData("extends")]
    [InlineData("dotenv")]
    [InlineData("include-env")]
    [InlineData("override")]
    public void PresentUnreadableGraphInputsAreNeverSkipped(string kind)
    {
        using var files = new Fixture();
        var filename = kind == "dotenv" ? ".env" : kind == "override" ? "compose.override.yaml" : PrivateInput;
        files.Write(filename, "WCD_GRAPH_PRIVATE=do-not-print");
        files.Write("child.yaml", "services: {child: {image: fixture}}");
        using var locked = new FileStream(files.PathOf(filename), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var yaml = kind switch
        {
            "include" => $"include: [{PrivateInput}]",
            "extends" => $"services: {{child: {{extends: {{file: {PrivateInput}, service: base}}}}}}",
            "include-env" => $"include: [{{path: child.yaml, env_file: {PrivateInput}}}]",
            _ => "services: {web: {image: fixture}}",
        };
        files.Error(yaml, kind == "dotenv" ? ".env" : kind == "include-env" ? "env_file" : kind, "unreadable");
    }

    [Theory]
    [InlineData("include")]
    [InlineData("extends")]
    [InlineData("env_file")]
    [InlineData("dotenv")]
    [InlineData("override")]
    public void InvalidUtf8InTextInputsFailsWithoutLeakingBytes(string kind)
    {
        using var files = new Fixture();
        var filename = kind == "dotenv" ? ".env" : kind == "override" ? "compose.override.yaml" : PrivateInput;
        files.WriteBytes(filename, [0x57, 0x43, 0x44, 0x3d, 0xff, 0xfe, 0x80]);
        var yaml = kind switch
        {
            "include" => $"include: [{PrivateInput}]",
            "extends" => $"services: {{child: {{extends: {{file: {PrivateInput}, service: base}}}}}}",
            "env_file" => $"services: {{child: {{image: fixture, env_file: [{{path: {PrivateInput}, required: false}}]}}}}",
            _ => "services: {web: {image: fixture}}",
        };
        files.Error(yaml, kind == "dotenv" ? ".env" : kind, "encoding");
    }

    [Theory]
    [InlineData("include")]
    [InlineData("extends")]
    public void MalformedReferencedYamlRetainsSourceBreadcrumbWithoutContent(string kind)
    {
        using var files = new Fixture();
        files.Write(PrivateInput, "services: {secret-service: {image: synthetic-private-value, command: [broken}");
        var yaml = kind == "include" ? $"include: [{PrivateInput}]"
            : $"services: {{child: {{extends: {{file: {PrivateInput}, service: base}}}}}}";
        var error = files.Error(yaml, kind, "Invalid YAML");
        Assert.DoesNotContain("synthetic-private-value", error.ToString());
        Assert.DoesNotContain("secret-service", error.ToString());
    }

    [Theory]
    [InlineData("MISSING_EQUALS_SYNTHETIC_PRIVATE")]
    [InlineData("=synthetic-private-value")]
    [InlineData("BAD KEY=synthetic-private-value")]
    [InlineData("KEY='synthetic-private-value")]
    [InlineData("KEY=\"synthetic-private-value")]
    public void MalformedDotenvRejectsEvenForOptionalExistingFile(string contents)
    {
        using var files = new Fixture();
        files.Write(PrivateInput, contents);
        var error = files.Error($"services: {{web: {{image: fixture, env_file: [{{path: {PrivateInput}, required: false}}]}}}}",
            "env_file", "line 1");
        Assert.DoesNotContain("synthetic-private-value", error.ToString());
        Assert.DoesNotContain("MISSING_EQUALS_SYNTHETIC_PRIVATE", error.ToString());
    }

    [Theory]
    [InlineData("synthetic-private-input")]
    [InlineData("[{path: synthetic-private-input, required: true}]")]
    public void RequiredEnvFilesRejectUnsupportedBareKeysWithOrdinalBreadcrumbs(string form)
    {
        using var files = new Fixture();
        files.Write(PrivateInput, "WCD_GRAPH_VALID=present\nSYNTHETIC_PRIVATE_BARE_KEY");
        var error = files.Error($"services: {{synthetic-private-service: {{image: fixture, env_file: {form}}}}}",
            "services[1]", "env_file[1]", "line 2", "expected KEY=VALUE");
        Assert.DoesNotContain("synthetic-private-service", error.ToString());
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_BARE_KEY", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildDefaultDotenvIsOptionalOnlyWhenAbsent(bool malformed)
    {
        using var files = new Fixture();
        files.Write(@"child\compose.yaml", "services: {child: {image: fixture}}");
        Assert.Single(files.Parse("include: [child/compose.yaml]").Services);
        if (malformed)
        {
            files.Write(@"child\.env", "malformed-synthetic-private-value");
            files.Error("include: [child/compose.yaml]", "include[1]", ".env", "line 1");
        }
        else
        {
            files.Write(@"child\.env", "WCD_GRAPH_PRIVATE=do-not-print");
            using var locked = new FileStream(files.PathOf(@"child\.env"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            files.Error("include: [child/compose.yaml]", "include[1]", ".env", "unreadable");
        }
    }

    [Theory]
    [InlineData("secrets", false)]
    [InlineData("secrets", true)]
    [InlineData("configs", false)]
    [InlineData("configs", true)]
    public void FileResourcesMustBePresentAndReadable(string kind, bool unreadable)
    {
        using var files = new Fixture();
        FileStream? locked = null;
        try
        {
            if (unreadable)
            {
                files.WriteBytes(PrivateInput, [0xff, 0x00]);
                locked = new FileStream(files.PathOf(PrivateInput), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            files.Error($"services: {{web: {{image: fixture}}}}\n{kind}: {{private: {{file: {PrivateInput}}}}}",
                kind, unreadable ? "unreadable" : "missing");
        }
        finally
        {
            locked?.Dispose();
        }
    }

    [Theory]
    [InlineData("secrets")]
    [InlineData("configs")]
    public void FileResourceBreadcrumbOrdinalsRestartForEachResourceKind(string kind)
    {
        using var files = new Fixture();
        var error = files.Error($$$"""
            services: {synthetic-private-service: {image: fixture}}
            networks: {synthetic-private-network: {external: true}}
            volumes: {synthetic-private-volume: {external: true}}
            {{{kind}}}:
              synthetic-private-first: {external: true}
              synthetic-private-second: {file: synthetic-private-input}
            """, $"{kind}[2]", ".file", "missing");
        foreach (var name in new[] { "service", "network", "volume", "first", "second" })
            Assert.DoesNotContain("synthetic-private-" + name, error.ToString());
    }

    [Theory]
    [InlineData("include: child.yaml", "include")]
    [InlineData("include: [{path: []}]", "include")]
    [InlineData("include: [{path: child.yaml, required: false}]", "include")]
    [InlineData("include: [{path: child.yaml, project_directory: []}]", "project_directory")]
    [InlineData("include: [{path: child.yaml, env_file: {path: child.env, required: false}}]", "env_file")]
    [InlineData("services: {web: {image: fixture, extends: base}}", "extends")]
    [InlineData("services: {web: {image: fixture, extends: {service: base, unknown: synthetic-private-input}}}", "extends")]
    [InlineData("services: {web: {image: fixture, env_file: [{path: child.env, required: maybe}]}}", "env_file")]
    [InlineData("services: {web: {image: fixture, env_file: [{path: child.env, format: raw}]}}", "env_file")]
    [InlineData("secrets: {token: {environment: synthetic-private-input}}", "secrets")]
    [InlineData("configs: {settings: {content: synthetic-private-input}}", "configs")]
    public void UnsupportedGraphFormsRejectWithSafeKeyContext(string yaml, string key)
    {
        using var files = new Fixture();
        files.Write("child.yaml", "services: {child: {image: fixture}}");
        files.Error(yaml, key);
    }

    [Theory]
    [InlineData("include: ['https://synthetic-private-input/compose.yaml']", "include")]
    [InlineData("include: ['git@synthetic-private-input:compose.yaml']", "include")]
    [InlineData("services: {web: {extends: {file: 'https://synthetic-private-input/base.yaml', service: base}}}", "extends.file")]
    [InlineData("services: {web: {image: fixture, env_file: 'https://synthetic-private-input/values.env'}}", "env_file")]
    [InlineData("secrets: {token: {file: 'https://synthetic-private-input/token'}}", "secrets")]
    [InlineData("configs: {settings: {file: 'https://synthetic-private-input/settings'}}", "configs")]
    public void RemoteRequiredInputsRejectWithoutNetworkAccess(string yaml, string key)
    {
        using var files = new Fixture();
        files.Error(yaml, key, "remote");
    }

    [Theory]
    [InlineData("include: [child.yaml]", "include")]
    [InlineData("services: {child: {extends: {file: base.yaml, service: base}}}", "extends.file")]
    [InlineData("services: {child: {image: fixture, env_file: values.env}}", "env_file")]
    [InlineData("secrets: {token: {file: token.bin}}", "secrets")]
    [InlineData("configs: {settings: {file: settings.bin}}", "configs")]
    public void RelativeRequiredFileWithoutSourceDirectoryRejects(string yaml, string key)
    {
        var error = Assert.Throws<ComposeConfigurationException>(() => ComposeImporter.ParseProject(yaml));
        Assert.Contains(key, error.Message);
        Assert.Contains("source project directory", error.Message);
    }

    [Theory]
    [InlineData(@"C:\synthetic-private-input\data")]
    [InlineData(@"\\synthetic-private-input.invalid\share\data")]
    public void WindowsAbsoluteBindPathsAreLexicalOnly(string path)
    {
        var service = Assert.Single(ComposeImporter.ParseProject(
            $"services: {{web: {{image: fixture, volumes: ['{path}:/data']}}}}").Services);
        Assert.Equal(path + ":/data", Assert.Single(service.Options.Volumes));
    }

    [Theory]
    [InlineData(@"C:synthetic-private-input")]
    [InlineData(@"\synthetic-private-input")]
    public void DriveRelativeBindPathsRejectWithoutResolvingAgainstCurrentDrive(string path)
    {
        using var files = new Fixture();
        files.Error($"services: {{web: {{image: fixture, volumes: [{{type: bind, source: '{path}', target: /data}}]}}}}",
            "path");
    }

    [Fact]
    public void GraphDepthAndDocumentCountAreBounded()
    {
        using var files = new Fixture();
        for (var i = 0; i < 67; i++)
            files.Write($"deep{i}.yaml", $"include: [deep{i + 1}.yaml]");
        files.Error("include: [deep0.yaml]", "64", "limit");
        for (var i = 0; i < 256; i++)
            files.Write($"wide{i}.yaml", $"services: {{service{i}: {{image: fixture}}}}");
        files.Error("include: [" + string.Join(", ", Enumerable.Range(0, 256).Select(i => $"wide{i}.yaml")) + "]",
            "256", "limit");
    }

    [Fact]
    public void RequiredTextInputSizeIsBounded()
    {
        using var files = new Fixture();
        files.Write(PrivateInput, new string(' ', 8_000_001));
        files.Error($"include: [{PrivateInput}]", "include", "8 MB");
    }

    [Fact]
    public void ParsingBeforeSavingPreservesPriorProjectOnGraphFailure()
    {
        using var files = new Fixture();
        var previous = files.Parse("name: saved\nservices: {web: {image: fixture:original}}");
        var snapshot = JsonSerializer.Serialize(previous);
        var saves = 0;
        var store = NetworkTestProxy.Create<IComposeProjectStore>((method, _) => method.Name switch
        {
            nameof(IComposeProjectStore.Get) => previous,
            nameof(IComposeProjectStore.GetAll) => new List<ComposeProject> { previous },
            nameof(IComposeProjectStore.Save) => ++saves,
            _ => null,
        });
        // Mirrors the inspected view-model boundary; this is not a WinUI integration test.
        Assert.Throws<ComposeConfigurationException>(() =>
        {
            var replacement = files.Parse("name: saved\ninclude: [missing.yaml]\nservices: {web: {image: fixture:new}}");
            store.Save(replacement);
        });
        Assert.Equal(0, saves);
        Assert.Equal(snapshot, JsonSerializer.Serialize(Assert.Single(store.GetAll())));
    }

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.Split('\n').Select(line => new string(' ', spaces) + line));

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "compose-graph-" + Guid.NewGuid().ToString("N"));

        public Fixture() => Directory.CreateDirectory(_directory);

        public string PathOf(string relative) => Path.Combine(_directory, relative.Replace('\\', Path.DirectorySeparatorChar));

        public void Write(string relative, string contents)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void WriteBytes(string relative, byte[] contents)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }

        public ComposeProject Parse(string yaml, IReadOnlyDictionary<string, string>? environment = null) =>
            ComposeImporter.ParseProject(yaml, environment, _directory);

        public ComposeConfigurationException Error(string yaml, params string[] fragments)
        {
            var error = Assert.Throws<ComposeConfigurationException>(() => Parse(yaml));
            foreach (var fragment in fragments) Assert.Contains(fragment, error.Message);
            Assert.Contains("Compose input", error.Message);
            Assert.DoesNotContain(PrivateInput, error.ToString());
            Assert.DoesNotContain(_directory, error.ToString());
            Assert.DoesNotContain("do-not-print", error.ToString());
            Assert.Null(error.InnerException);
            return error;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
