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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ComposeConformanceTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compose", "v1");

    public static IEnumerable<object[]> Cases() =>
        Directory.GetDirectories(Corpus).Where(d => File.Exists(Path.Combine(d, "expectations.json")))
            .Order(StringComparer.Ordinal).Select(d => new object[] { Path.GetFileName(d) });

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Category", "ComposeConfiguration")]
    public async Task ImportMatchesDocumentedProjection(string id)
    {
        var source = Path.Combine(Corpus, id);
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "expectations.json")))!.AsObject();
        var directory = Path.Combine(AppContext.BaseDirectory, "wslcd-compose-conformance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(directory, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(typeof(ComposeConformanceWorker).Assembly.Location);
            start.ArgumentList.Add("--compose-fixture");
            start.ArgumentList.Add(directory);
            start.Environment.Clear();
            // Windows needs SystemRoot; temporary and home/config paths must be disposable.
            if (Environment.GetEnvironmentVariable("SystemRoot") is { } systemRoot)
                start.Environment["SystemRoot"] = systemRoot;
            foreach (var key in new[] { "TEMP", "TMP", "HOME", "USERPROFILE", "DOTNET_CLI_HOME" })
                start.Environment[key] = directory;
            foreach (var (key, value) in expected["environment"]!.AsObject())
            {
                Assert.True(key.StartsWith("WCD_", StringComparison.Ordinal) || key == "COMPOSE_PROFILES",
                    $"{id}: fixture environment keys must be synthetic WCD_* variables or COMPOSE_PROFILES.");
                start.Environment[key] = value!.GetValue<string>();
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture worker.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException($"{id}/compose.yaml: fixture worker exceeded 30 seconds.");
            }
            Assert.True(process.ExitCode == 0, $"{id}/compose.yaml: worker failed: {await error}");
            var actual = JsonNode.Parse(await output)!;
            if (expected["appError"] is JsonArray errorParts)
            {
                Assert.NotNull(actual["error"]);
                foreach (var part in errorParts)
                    Assert.Contains(part!.GetValue<string>(), actual["error"]!.GetValue<string>());
                foreach (var secret in expected["forbiddenDiagnostics"]?.AsArray() ?? [])
                    Assert.DoesNotContain(secret!.GetValue<string>(), actual["error"]!.GetValue<string>());
            }
            else
                Assert.Null(actual["error"]);
            var checks = expected["checks"]!.AsObject();
            var divergences = expected["divergences"]!.AsObject();
            Assert.True(checks.ContainsKey("/serviceNames"), $"{id}: service inventory must be asserted.");
            foreach (var (pointer, _) in divergences)
                Assert.True(checks.ContainsKey(pointer), $"{id}: stale divergence {pointer} has no check.");

            foreach (var (pointer, referenceValue) in checks)
            {
                var actualValue = ComposeConformanceProjection.At(actual, pointer);
                var divergence = divergences[pointer];
                var wanted = divergence is null ? referenceValue : divergence["app"];
                if (divergence is not null)
                {
                    Assert.False(JsonNode.DeepEquals(referenceValue, wanted), $"{id}: remove resolved divergence {pointer}.");
                    Assert.False(string.IsNullOrWhiteSpace(divergence["reason"]?.GetValue<string>()));
                    Assert.True(divergence["issue"]?.GetValue<int>() > 0, $"{id}: divergence needs a tracking issue.");
                }
                Assert.True(JsonNode.DeepEquals(wanted, actualValue),
                    $"{id}/compose.yaml {pointer}: expected {wanted?.ToJsonString() ?? "null"}, " +
                    $"actual {actualValue?.ToJsonString() ?? "null"}; reference {referenceValue?.ToJsonString() ?? "null"}. " +
                    (divergence is null ? "" : $"Known difference: {divergence["reason"]} (#{divergence["issue"]})."));
            }
            Assert.True(JsonNode.DeepEquals(expected["warnings"], actual["warnings"]),
                $"{id}/compose.yaml diagnostics: expected {expected["warnings"]}, actual {actual["warnings"]}.");

            // A reference capture is optional, but once committed it is always checked.
            var capturePath = Path.Combine(source, "reference.json");
            if (File.Exists(capturePath))
            {
                var capture = JsonNode.Parse(await File.ReadAllTextAsync(capturePath))!;
                Assert.Equal("2.39.4", capture["cliVersion"]!.GetValue<string>());
                Assert.Equal("standalone-compose-config", capture["origin"]!.GetValue<string>());
                Assert.Equal("sha256-utf8-lf", capture["inputHashAlgorithm"]!.GetValue<string>());
                Assert.Matches("^[a-f0-9]{64}$", capture["executableSha256"]!.GetValue<string>());
                var provenance = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Corpus, "reference-provenance.json")))!;
                Assert.Equal(provenance["binarySha256"]!.GetValue<string>(), capture["executableSha256"]!.GetValue<string>());
                var hashes = capture["inputHashes"]!.AsObject();
                var inputs = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f) != "reference.json").ToArray();
                Assert.Equal(inputs.Length, hashes.Count);
                foreach (var input in inputs)
                {
                    var relative = Path.GetRelativePath(source, input).Replace('\\', '/');
                    var digest = InputHash(await File.ReadAllTextAsync(input));
                    Assert.True(hashes[relative]?.GetValue<string>() == digest,
                        $"{id}/{relative}: reference capture is stale; explicitly regenerate after reviewing input changes.");
                }
                if (expected["referenceError"] is { } referenceError)
                {
                    Assert.NotEqual(0, capture["exitCode"]!.GetValue<int>());
                    Assert.Contains(referenceError.GetValue<string>(), capture["stderr"]!.GetValue<string>());
                }
                else
                {
                    Assert.Equal(0, capture["exitCode"]!.GetValue<int>());
                    var reference = ComposeConformanceProjection.FromReference(capture["config"]!.AsObject());
                    foreach (var (pointer, value) in checks)
                        Assert.True(JsonNode.DeepEquals(value, ComposeConformanceProjection.At(reference, pointer)),
                            $"{id}/compose.yaml {pointer}: captured value " +
                            $"{ComposeConformanceProjection.At(reference, pointer)} differs from expected {value}.");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProjectionReportsMissingSourceKeyInsteadOfTreatingItAsNull()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            ComposeConformanceProjection.At(JsonNode.Parse("""{"services":{}}""")!, "/services/web/image"));
        Assert.Contains("/services/web/image", error.Message);
    }

    [Fact]
    public void CorpusRetainsRequiredCoverageAndHonestProvenance()
    {
        var cases = Cases().Select(c => (string)c[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
        {
            "interpolation", "yaml-anchors", "yaml-block", "environment", "override", "include",
            "extends", "profiles", "mounts-ports", "networks", "health-dependencies", "unsupported",
            "required-variable", "missing-env-file",
        })
            Assert.Contains(required, cases);
        var provenance = JsonNode.Parse(File.ReadAllText(Path.Combine(Corpus, "reference-provenance.json")))!;
        Assert.Equal(1, provenance["schemaVersion"]!.GetValue<int>());
        Assert.Equal("hand-authored-spec-projection", provenance["expectationOrigin"]!.GetValue<string>());
        Assert.Equal("none", provenance["runtimeCertification"]!.GetValue<string>());
        foreach (var capturedCase in provenance["capturedCases"]!.AsArray())
            Assert.True(File.Exists(Path.Combine(Corpus, capturedCase!.GetValue<string>(), "reference.json")),
                $"{capturedCase}: committed reference evidence must not silently disappear.");
        foreach (var id in cases)
        {
            var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(Corpus, id, "expectations.json")))!;
            if (expectations["referenceError"] is not null && expectations["appError"] is null)
                Assert.False(string.IsNullOrWhiteSpace(expectations["diagnosticLimitation"]?.GetValue<string>()),
                    $"{id}: a reference rejection must document the app's diagnostic gap.");
        }

    }

    internal static string InputHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    [Fact]
    public void InputHashesIgnoreCheckoutLineEndingsButNotContentChanges()
    {
        Assert.Equal(InputHash("a\nb\n"), InputHash("a\r\nb\r\n"));
        Assert.NotEqual(InputHash("a\nb\n"), InputHash("a\nc\n"));
    }

    [Fact]
    public void ReferenceProjectionNormalizesOnlyDocumentedRepresentations()
    {
        var config = JsonNode.Parse("""
            {"services":{"web":{"image":"fixture:1","command":["echo","two words"],"environment":{"LITERAL":"$$VAR","BARE":"$VAR"},
            "ports":[{"host_ip":"127.0.0.1","published":"8080","target":80,"protocol":"tcp"}],
            "volumes":[{"type":"volume","source":"data","target":"/data","read_only":true}],
            "depends_on":{"db":{"condition":"service_healthy","required":true}}}}}
            """)!.AsObject();
        var projection = ComposeConformanceProjection.FromReference(config);
        Assert.Equal("$VAR", projection["services"]!["web"]!["environment"]!["LITERAL"]!.GetValue<string>());
        Assert.Equal("$VAR", projection["services"]!["web"]!["environment"]!["BARE"]!.GetValue<string>());
        Assert.Equal("127.0.0.1:8080:80", projection["services"]!["web"]!["ports"]![0]!.GetValue<string>());
        Assert.Equal("data:/data:ro", projection["services"]!["web"]!["volumes"]![0]!.GetValue<string>());
        Assert.Equal("echo \"two words\"", projection["services"]!["web"]!["command"]!.GetValue<string>());
        Assert.Equal("service_healthy", projection["services"]!["web"]!["depends_on"]!["db"]!.GetValue<string>());
        Assert.IsType<JsonObject>(config["services"]!["web"]!["ports"]![0]);
    }
}
