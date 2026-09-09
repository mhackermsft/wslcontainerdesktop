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

public sealed class ComposeSemanticsTests
{
    [Theory]
    [InlineData("", 1)]
    [InlineData("scale: 0", 0)]
    [InlineData("scale: 3", 3)]
    [InlineData("deploy: {replicas: 2}", 2)]
    [InlineData("deploy: {mode: replicated, replicas: 4}", 4)]
    [InlineData("scale: 2, deploy: {replicas: 2}", 2)]
    [InlineData("scale: '${COUNT}'", 3)]
    public void LocalReplicaCountSupportsComposeSyntax(string fields, int expected)
    {
        var project = ComposeImporter.ParseProject(
            $"services: {{web: {{image: fixture{(fields.Length == 0 ? "" : ", " + fields)}}}}}",
            new Dictionary<string, string> { ["COUNT"] = "3" });
        Assert.Equal(expected, Assert.Single(project.Services).Replicas);
        Assert.Empty(project.Warnings);
        var saved = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(project))!;
        Assert.Equal(expected, Assert.Single(saved.Services).Replicas);
    }

    [Theory]
    [InlineData("scale: -1")]
    [InlineData("scale: 1.5")]
    [InlineData("scale: true")]
    [InlineData("scale: []")]
    [InlineData("scale: 2147483648")]
    [InlineData("deploy: {replicas: -1}")]
    [InlineData("deploy: {replicas: {secret: synthetic-secret}}")]
    [InlineData("scale: 2, deploy: {replicas: 3}")]
    [InlineData("deploy: {mode: global}")]
    [InlineData("deploy: {mode: replicated-job}")]
    public void InvalidReplicaConfigurationFailsBeforeImport(string fields)
    {
        var error = Assert.Throws<ComposeConfigurationException>(() =>
            ComposeImporter.ParseProject($"services: {{web: {{image: fixture, {fields}}}}}"));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public void LegacyProjectsDefaultToOneReplicaAndOverridesRoundTrip()
    {
        var legacy = JsonSerializer.Deserialize<ComposeProject>(
            """{"Name":"project","Services":[{"Name":"web"}]}""")!;
        Assert.Equal(1, Assert.Single(legacy.Services).Replicas);
        Assert.Empty(legacy.ReplicaOverrides);
        legacy.ReplicaOverrides["web"] = 0;
        var restored = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(legacy))!;
        Assert.Equal(0, restored.ReplicaOverrides["web"]);
    }

    [Fact]
    public void SwarmSettingsWarnRatherThanClaimingLocalSupport()
    {
        var project = ComposeImporter.ParseProject(
            "services: {web: {image: fixture, deploy: {replicas: 3, placement: {constraints: [node.role==manager]}}}}");
        Assert.Equal(3, Assert.Single(project.Services).Replicas);
        Assert.Contains("Swarm", Assert.Single(project.Warnings));
    }

    private static readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal)
    {
        ["WCD_SET"] = "value",
        ["WCD_EMPTY"] = "",
        ["WCD_SECRET"] = "synthetic-secret: [not yaml] # private",
    };

    [Theory]
    [InlineData("${WCD_SET}", "value")]
    [InlineData("$WCD_SET/x", "value/x")]
    [InlineData("${WCD_EMPTY-default}", "")]
    [InlineData("${WCD_EMPTY:-default}", "default")]
    [InlineData("${WCD_MISSING-default}", "default")]
    [InlineData("${WCD_MISSING:-default}", "default")]
    [InlineData("${WCD_SET:+alt}", "alt")]
    [InlineData("${WCD_SET+alt}", "alt")]
    [InlineData("${WCD_EMPTY:+alt}", "")]
    [InlineData("${WCD_EMPTY+alt}", "alt")]
    [InlineData("${WCD_MISSING:+alt}", "")]
    [InlineData("${WCD_MISSING+alt}", "")]
    [InlineData("${WCD_SET:?ignored}", "value")]
    [InlineData("${WCD_EMPTY?ignored}", "")]
    [InlineData("${WCD_MISSING:-${WCD_EMPTY:-${WCD_SET:+nested}}}", "nested")]
    [InlineData("${WCD_SET:-${WCD_MISSING:?unused}}", "value")]
    [InlineData("${WCD_MISSING:+${WCD_MISSING:?unused}}", "")]
    [InlineData("${WCD_SET:+${WCD_MISSING-default}}", "default")]
    [InlineData("$$WCD_SET $${WCD_SET} $$$WCD_SET", "$WCD_SET ${WCD_SET} $value")]
    [InlineData("${WCD_MISSING:-$${LITERAL}}", "${LITERAL}")]
    [InlineData("${WCD_MISSING:-$${LITERAL}-suffix}", "${LITERAL}-suffix")]
    [InlineData("${WCD_MISSING:-$${LITERAL:-${WCD_SET}}-suffix}", "${LITERAL:-value}-suffix")]
    [InlineData("$9 / $ / $- / $é", "$9 / $ / $- / $é")]
    public void InterpolationUsesComposeOperators(string expression, string expected) =>
        Assert.Equal(expected, ComposeInterpolation.Expand(expression, Variables));

    [Theory]
    [InlineData("${WCD_MISSING:?synthetic-secret}", "Required variable 'WCD_MISSING'")]
    [InlineData("${WCD_EMPTY:?synthetic-secret}", "unset or empty")]
    [InlineData("${WCD_MISSING?${WCD_SECRET}}", "unset")]
    [InlineData("${WCD_SET", "Unclosed")]
    [InlineData("${WCD_SET/secret/replacement}", "Unsupported")]
    [InlineData("${WCD_SET:=synthetic-secret}", "Unsupported")]
    [InlineData("${}", "Malformed")]
    [InlineData("${9BAD}", "Malformed")]
    [InlineData("${WCD_SET:-${BAD}", "Unclosed")]
    public void InterpolationErrorsAreActionableAndValueFree(string expression, string fragment)
    {
        var error = Assert.Throws<ComposeConfigurationException>(() => ComposeInterpolation.Expand(expression, Variables));
        Assert.Contains(fragment, error.Message);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ValuesAreInterpolatedAfterYamlAndKeysAreNot()
    {
        var service = Assert.Single(ComposeImporter.ParseProject("""
            services:
              web:
                image: fixture:1
                environment:
                  "${WCD_SET}": '${WCD_SECRET}'
                  EMPTY: ""
                  UNSET:
                  LITERAL: "'kept'"
                  HASH: before#after
                  ESCAPES: "quote=\" slash=\\ tab=\t unicode=\u263A"
                labels:
                  - "${WCD_SET}=${WCD_SECRET}"
            """, Variables).Services);
        Assert.Contains("${WCD_SET}=" + Variables["WCD_SECRET"], service.Options.EnvironmentVariables);
        Assert.Contains("EMPTY=", service.Options.EnvironmentVariables);
        Assert.Contains("UNSET", service.Options.EnvironmentVariables);
        Assert.Contains("LITERAL='kept'", service.Options.EnvironmentVariables);
        Assert.Contains("HASH=before#after", service.Options.EnvironmentVariables);
        Assert.Contains("ESCAPES=quote=\" slash=\\ tab=\t unicode=☺", service.Options.EnvironmentVariables);
        Assert.Equal(Variables["WCD_SECRET"], service.Options.Labels["value"]);
    }

    [Fact]
    public void CommandArgvPreservesEmptyTokensQuotesNewlinesAndWindowsPaths()
    {
        var options = Assert.Single(ComposeImporter.ParseProject("""
            services:
              web:
                image: fixture:1
                command: [echo, "", "it's \"quoted\"", 'C:\path with spaces\', "line\nnext"]
            """).Services).Options;
        var args = options.ToArguments();
        Assert.Equal(["echo", "", "it's \"quoted\"", @"C:\path with spaces\", "line\nnext"],
            args.Skip(args.IndexOf("fixture:1") + 1));
        var saved = JsonSerializer.Deserialize<RunContainerOptions>(JsonSerializer.Serialize(options))!;
        Assert.Equal(args, saved.ToArguments());
    }

    [Theory]
    [InlineData("services: [synthetic-secret]", "services")]
    [InlineData("services:\n  web: synthetic-secret", "services")]
    [InlineData("services: {web: {image: fixture, environment: [ {BAD: synthetic-secret} ]}}", "environment")]
    [InlineData("services: {web: {image: fixture, ports: [ {published: 80} ]}}", "ports")]
    [InlineData("services: {web: {image: fixture, command: {bad: synthetic-secret}}}", "command")]
    [InlineData("services: {web: {image: fixture, healthcheck: {test: {bad: synthetic-secret}}}}", "healthcheck.test")]
    [InlineData("services: {web: {image: fixture, environment: {A: 1, A: synthetic-secret}}}", "Duplicate")]
    [InlineData("services: {web: {image: fixture, command: [unterminated, synthetic-secret}}", "Invalid YAML")]
    [InlineData("services: {web: *missing}", "alias")]
    [InlineData("services: &cycle {web: *cycle}", "alias")]
    [InlineData("x-old: &cycle {value: old}\nservices: &cycle {web: *cycle}", "alias")]
    [InlineData("services: {web: {<<: [synthetic-secret], image: fixture}}", "merge keys")]
    [InlineData("services: {web: {image: !custom synthetic-secret}}", "Unsupported YAML tag")]
    [InlineData("services: {web: {image: fixture, ports: [!reset 80]}}", "sequence items")]
    [InlineData("services: {}\n---\nservices: {}", "Multiple YAML documents")]
    [InlineData("services:\n\tweb: {image: fixture}", "Invalid YAML")]
    [InlineData("services: {web: {command: synthetic-secret}}", "neither image nor build")]
    public void MalformedConfigurationFailsWithoutEchoingSource(string yaml, string fragment)
    {
        var error = Assert.Throws<ComposeConfigurationException>(() => ComposeImporter.ParseProject(yaml));
        Assert.Contains(fragment, error.Message);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void AnchorsWorkForFlowSequencesScalarsAndMergePrecedence()
    {
        var service = Assert.Single(ComposeImporter.ParseProject("""
            x-first: &first {WINNER: first, KEEP: one}
            x-second: &second {WINNER: second, OTHER: two}
            x-command: &command [echo, "two words"]
            x-image: &image fixture:1
            services:
              web:
                image: *image
                command: *command
                environment:
                  WINNER: explicit
                  <<: [*first, *second]
                  AFTER: last
            """).Services);
        Assert.Equal("fixture:1", service.Options.Image);
        Assert.Equal("echo \"two words\"", service.Options.Command);
        Assert.Equal(["WINNER=explicit", "KEEP=one", "OTHER=two", "AFTER=last"], service.Options.EnvironmentVariables);
    }

    [Theory]
    [InlineData("'<<'")]
    [InlineData("!!str <<")]
    [InlineData("! <<")]
    public void LiteralMergeKeyNamesRemainOrdinaryKeys(string key)
    {
        var project = ComposeImporter.ParseProject(
            $"services: {{web: {{image: fixture, environment: {{{key}: literal}}}}}}");
        Assert.Equal(["<<=literal"], Assert.Single(project.Services).Options.EnvironmentVariables);
    }

    [Theory]
    [InlineData("|", "first\n\nsecond\n")]
    [InlineData("|-", "first\n\nsecond")]
    [InlineData("|+", "first\n\nsecond\n\n")]
    [InlineData(">", "first\nsecond\n")]
    [InlineData(">-", "first\nsecond")]
    [InlineData(">+", "first\nsecond\n\n")]
    public void BlockScalarsPreserveBlankLinesAndChomping(string style, string expected)
    {
        var yaml = "services:\n  web:\n    image: fixture\n    environment:\n      BLOCK: " + style +
            "\n        first\n\n        second\n\n";
        var service = Assert.Single(ComposeImporter.ParseProject(yaml).Services);
        Assert.Equal("BLOCK=" + expected, Assert.Single(service.Options.EnvironmentVariables));
    }

    [Fact]
    public void FoldedScalarsPreserveMoreIndentedLinesAndExplicitIndent()
    {
        var service = Assert.Single(ComposeImporter.ParseProject(
            "services:\r  web:\r    image: fixture\r    environment:\r      BLOCK: >2-\r        first\r          indented\r        last\r").Services);
        Assert.Equal("BLOCK=first\n  indented\nlast", Assert.Single(service.Options.EnvironmentVariables));
    }

    [Fact]
    public void AliasExpansionAndDepthAreBounded()
    {
        var yaml = "x-0: &a0 [one, two]\n" +
            string.Concat(Enumerable.Range(1, 19).Select(n => $"x-{n}: &a{n} [*a{n - 1}, *a{n - 1}]\n"));
        Assert.Throws<ComposeConfigurationException>(() => ComposeImporter.ParseProject(yaml));
        Assert.Throws<ComposeConfigurationException>(() => ComposeImporter.ParseProject("x: " + new string('[', 70) + new string(']', 70)));
        Assert.Throws<ComposeConfigurationException>(() => ComposeImporter.ParseProject(new string(' ', 2_000_001)));
    }

    [Fact]
    public void SavedSchemaRoundTripsWithoutParserMetadata()
    {
        var original = ComposeImporter.ParseProject("""
            services: {web: {image: fixture, environment: {EMPTY: "", VALUE: "one"}, ports: ["8080:80"]}}
            """);
        var json = JsonSerializer.Serialize(original);
        var saved = JsonSerializer.Deserialize<ComposeProject>(json)!;
        Assert.Equal(original.Services[0].Options.EnvironmentVariables, saved.Services[0].Options.EnvironmentVariables);
        Assert.Equal(original.Services[0].Options.PortMappings, saved.Services[0].Options.PortMappings);
        Assert.DoesNotContain("Yaml", json);
        Assert.DoesNotContain("ScalarNode", json);
    }

    [Fact]
    public void MixedBuildArgumentFormsMergeByKey()
    {
        WithFiles("""
            services:
              web:
                build: {context: ., args: [KEEP=base, WINNER=base], labels: {keep: base, winner: base}}
            """, """
            services:
              web:
                build: {args: {WINNER: override, EMPTY: ""}, labels: [winner=override]}
            """, directory =>
        {
            var build = Assert.Single(ParseFiles(directory).Services).Build!;
            Assert.Equal(["KEEP=base", "WINNER=override", "EMPTY="], build.Args);
            Assert.Equal("base", build.Labels["keep"]);
            Assert.Equal("override", build.Labels["winner"]);
        });
    }

    [Fact]
    public void RangedAndIpv6PortsUseCanonicalResourceKeys()
    {
        WithFiles("""
            services:
              web:
                image: fixture
                ports: ["8080-8081:80-81", "[::1]:9090:90", "8080:080"]
                volumes: ["/anonymous:ro"]
            """, """
            services:
              web:
                ports: [{target: 80, published: 8080}, {host_ip: "::1", published: 9090, target: 90}]
            """, directory =>
        {
            var options = Assert.Single(ParseFiles(directory).Services).Options;
            Assert.Equal(["8080:80", "8081:81", "[::1]:9090:90"], options.PortMappings);
            Assert.Equal(["/anonymous:ro"], options.Volumes);
        });
    }

    [Fact]
    public void ExtraHostsRetainsMultipleAddressesAcrossMixedForms()
    {
        WithFiles("""
            services:
              web:
                image: fixture
                extra_hosts: ["dual=192.0.2.1", "dual:2001:db8::1"]
            """, """
            services:
              web:
                extra_hosts: {dual: ["192.0.2.1", "192.0.2.2"], other: "192.0.2.3"}
            """, directory =>
        {
            var service = Assert.Single(ParseFiles(directory).Services);
            Assert.Equal(["dual:192.0.2.1", "dual:2001:db8::1", "dual:192.0.2.2", "other:192.0.2.3"],
                service.ExtraHosts);
        });
    }

    [Theory]
    [InlineData("secrets", "token", "/run/secrets/token")]
    [InlineData("configs", "settings", "/settings")]
    public void DefaultFileMountTargetsShareAbsoluteTargetIdentity(string kind, string source, string target)
    {
        WithFiles($$"""
            services:
              web:
                image: fixture
                {{kind}}: [{{source}}]
            {{kind}}: { {{source}}: {external: true}, replacement: {external: true} }
            """, $$"""
            services:
              web:
                {{kind}}: [{source: replacement, target: {{target}}}]
            """, directory =>
        {
            var service = Assert.Single(ParseFiles(directory).Services);
            var mount = Assert.Single(kind == "secrets" ? service.Secrets : service.Configs);
            Assert.Equal("replacement", mount.Source);
            Assert.Equal(target, mount.Target);
        });
    }

    [Fact]
    public void ImplicitPortHostMatchesExplicitDefault()
    {
        WithFiles("""
            services: {web: {image: fixture, ports: ["8080:80"]}}
            """, """
            services: {web: {ports: [{host_ip: "0.0.0.0", published: 8080, target: 80}]}}
            """, directory =>
        {
            Assert.Equal(["0.0.0.0:8080:80"], Assert.Single(ParseFiles(directory).Services).Options.PortMappings);
        });
    }

    [Fact]
    public void TopLevelResourceLabelsMergeAcrossListAndMappingForms()
    {
        WithFiles("""
            services: {web: {image: fixture}}
            volumes: {data: {labels: [keep=base, winner=base]}}
            networks: {app: {labels: {keep: base, winner: base}}}
            """, """
            volumes: {data: {labels: {winner: override}}}
            networks: {app: {labels: [winner=override]}}
            """, directory =>
        {
            var project = ParseFiles(directory);
            Assert.Equal("base", Assert.Single(project.Volumes).Labels["keep"]);
            Assert.Equal("override", Assert.Single(project.Volumes).Labels["winner"]);
            Assert.Equal("base", Assert.Single(project.Networks).Labels["keep"]);
            Assert.Equal("override", Assert.Single(project.Networks).Labels["winner"]);
        });
    }

    [Theory]
    [InlineData("services: {web: {image: fixture, command: [synthetic-secret}}", "Invalid YAML")]
    [InlineData("services: {web: {image: '${WCD_MISSING:?synthetic-secret}'}}", "Required variable")]
    [InlineData("services: {web: {ports: ['65536:80']}}", "ports")]
    public void BadOverrideIsNeverSilentlySkipped(string overlay, string diagnostic)
    {
        WithFiles("services: {web: {image: fixture}}", overlay, directory =>
        {
            var error = Assert.Throws<ComposeConfigurationException>(() => ParseFiles(directory));
            Assert.Contains(diagnostic, error.Message);
            Assert.DoesNotContain("synthetic-secret", error.ToString());
        });
    }

    [Fact]
    public void ExplicitEmptyInterpolationValuesOutrankDotenvAndProcess()
    {
        WithFiles("""
            services:
              web:
                image: fixture
                environment:
                  DOTENV: "${WCD_DOTENV}"
                  EMPTY: "${WCD_EMPTY-default}"
                  EXPLICIT: "${WCD_SET}"
            """, "services: {}", directory =>
        {
            File.WriteAllText(Path.Combine(directory, ".env"), "WCD_DOTENV=from-file\nWCD_EMPTY=must-lose\nWCD_SET=must-lose\n");
            var project = ComposeImporter.ParseProject(File.ReadAllText(Path.Combine(directory, "compose.yaml")), Variables, directory);
            Assert.Equal(["DOTENV=from-file", "EMPTY=", "EXPLICIT=value"], Assert.Single(project.Services).Options.EnvironmentVariables);
        });
    }

    [Fact]
    public void ValuesInEachFileAreExpandedOnlyOnceBeforeMerge()
    {
        WithFiles("""
            services:
              web:
                image: fixture
                environment: {ESCAPED: "$$WCD_SET", RAW: "${WCD_RAW}"}
            """, """
            services:
              web:
                environment: {ADDED: "${WCD_SET}"}
            """, directory =>
        {
            var environment = new Dictionary<string, string>(Variables) { ["WCD_RAW"] = "${WCD_SET}" };
            var project = ComposeImporter.ParseProject(File.ReadAllText(Path.Combine(directory, "compose.yaml")), environment, directory);
            Assert.Equal(["ESCAPED=$WCD_SET", "RAW=${WCD_SET}", "ADDED=value"], Assert.Single(project.Services).Options.EnvironmentVariables);
        });
    }

    [Fact]
    public void UnsupportedResourceDetailsAreDiagnosedInsteadOfSilentlyDropped()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              web:
                image: fixture
                ports: [{target: 80, published: 8080, mode: host}]
                volumes: [{source: data, target: /data, volume: {nocopy: true}}]
                secrets: [{source: token, target: token, uid: "1000"}]
            secrets: {token: {external: true}}
            """);
        Assert.Equal([
            "Service 'web': 'ports.mode' is not supported and was ignored.",
            "Service 'web': 'volumes.volume' is not supported and was ignored.",
            "Service 'web': 'secrets.uid' is not supported and was ignored.",
        ], project.Warnings);
    }

    private static ComposeProject ParseFiles(string directory) =>
        ComposeImporter.ParseProject(File.ReadAllText(Path.Combine(directory, "compose.yaml")), baseDirectory: directory);

    private static void WithFiles(string basis, string overlay, Action<string> test)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "compose-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "compose.yaml"), basis);
            File.WriteAllText(Path.Combine(directory, "compose.override.yaml"), overlay);
            test(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
