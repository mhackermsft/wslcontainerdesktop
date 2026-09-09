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

public sealed class NativeHealthTests
{
    [Fact]
    public void ConsecutiveFailuresStartupGraceAndNativeThresholdAreIndependent()
    {
        var progress = new HealthProbeProgress();
        var start = DateTimeOffset.UtcNow;
        progress.Reset(start);
        Assert.False(progress.Record(false, false, 2, TimeSpan.FromSeconds(10), start.AddSeconds(1)));
        Assert.Equal(0, progress.ConsecutiveFailures);
        Assert.False(progress.Record(false, false, 2, TimeSpan.FromSeconds(10), start.AddSeconds(11)));
        Assert.True(progress.Record(false, false, 2, TimeSpan.FromSeconds(10), start.AddSeconds(12)));
        progress.Record(true, false, 2, TimeSpan.Zero, start.AddSeconds(13));
        Assert.Equal(0, progress.ConsecutiveFailures);
        Assert.False(progress.Record(false, false, 2, TimeSpan.Zero, start.AddSeconds(14)));
        progress.Reset(start);
        Assert.True(progress.Record(false, true, 100, TimeSpan.FromHours(1), start));
        progress.Reset(start);
        progress.Record(true, false, 1, TimeSpan.FromMinutes(1), start);
        Assert.True(progress.Record(false, false, 1, TimeSpan.FromMinutes(1), start.AddSeconds(1)));
    }

    private static WslcCapabilities Capabilities(WslcCapabilitySupport support, WslcFeature? exception = null,
        WslcCapabilitySupport exceptionSupport = WslcCapabilitySupport.Unsupported) =>
        new("wslc.exe", "test", Enum.GetValues<WslcFeature>().ToDictionary(x => x,
            x => new WslcCapability(x == exception ? exceptionSupport : support, "fixture diagnostic")));

    [Fact]
    public void RunAndCreateEmitExactHealthTokensBeforeImage()
    {
        var options = new RunContainerOptions
        {
            Image = "test:local",
            Health = new()
            {
                Test = ["CMD-SHELL", "test \"$VALUE\" = 'a b'"], Interval = "1m30s",
                Timeout = "500ms", StartPeriod = "0s", Retries = 4,
            },
        };
        var selection = NativeHealthPolicy.Select(options.Health, Capabilities(WslcCapabilitySupport.Supported));
        Assert.True(selection.Native);
        Assert.Equal(new[] { "--health-cmd", "test \"$VALUE\" = 'a b'", "--health-interval", "1m30s",
            "--health-timeout", "500ms", "--health-start-period", "0s", "--health-retries", "4" }, selection.Arguments);
        Assert.Equal("test:local", options.ToArguments(selection.Arguments)[^1]);
        var create = options.ToCreateArguments(selection.Arguments);
        Assert.Equal("create", create[0]);
        Assert.DoesNotContain("-d", create);
        Assert.Equal(1, create.Count(x => x == "--health-cmd"));
        Assert.DoesNotContain("--restart", create);
    }

    [Fact]
    public void CreateCapabilitiesAreIndependent()
    {
        var caps = Capabilities(WslcCapabilitySupport.Supported, WslcFeature.CreateHealthCmd);
        var options = new NativeHealthOptions { Test = ["CMD-SHELL", "true"] };
        Assert.True(NativeHealthPolicy.Select(options, caps).Native);
        var create = NativeHealthPolicy.Select(options, caps, forCreate: true);
        Assert.False(create.Native);
        Assert.Equal(new[] { "--no-healthcheck" }, create.Arguments);
    }

    [Fact]
    public void OlderEngineKeepsDesiredOptionsAndUsesAppFallback()
    {
        var health = new NativeHealthOptions { Test = ["CMD-SHELL", "false"], Retries = 6, Timeout = "250ms" };
        var before = JsonSerializer.Serialize(health);
        var result = NativeHealthPolicy.Select(health, Capabilities(WslcCapabilitySupport.Unsupported));
        Assert.False(result.Native);
        Assert.Empty(result.Arguments);
        Assert.Contains("app-owned", result.Diagnostic);
        Assert.Equal(before, JsonSerializer.Serialize(health));
    }

    [Fact]
    public void UnknownNeverSilentlyDowngrades()
    {
        var caps = Capabilities(WslcCapabilitySupport.Supported, WslcFeature.HealthCmd, WslcCapabilitySupport.Unknown);
        var error = Assert.Throws<InvalidOperationException>(() => NativeHealthPolicy.Select(
            new() { Test = ["CMD-SHELL", "true"] }, caps));
        Assert.Contains("fixture diagnostic", error.Message);
        Assert.True(NativeHealthPolicy.Select(null, caps).Native);
    }

    [Fact]
    public void CmdUsesLiteralArgvNotShellSource()
    {
        var health = new NativeHealthOptions { Test = ["CMD", "printf", "%s", "$HOME; exit 1", "", "a b"] };
        var selection = NativeHealthPolicy.Select(health, Capabilities(WslcCapabilitySupport.Supported));
        Assert.False(selection.Native);
        Assert.Contains("CMD argv", selection.Diagnostic);
        Assert.Equal(new[] { "exec", "id", "printf", "%s", "$HOME; exit 1", "", "a b" },
            NativeHealthPolicy.ExecArguments("id", health));
        Assert.Equal(new[] { "exec", "id", "sh", "-c", "exit 0" },
            NativeHealthPolicy.ExecArguments("id", new() { Test = ["CMD-SHELL", "exit 0"] }));
    }

    [Theory]
    [InlineData("starting", NativeHealthState.Starting)]
    [InlineData("healthy", NativeHealthState.Healthy)]
    [InlineData("unhealthy", NativeHealthState.Unhealthy)]
    [InlineData("future-state", NativeHealthState.Unknown)]
    public void ParsesNativeStateWithoutChangingRunningState(string status, NativeHealthState expected)
    {
        var container = new ContainerInfo { StateValue = (int)ContainerState.Running };
        container.NativeHealth = NativeHealthParser.Parse("""[{"Config":{"Healthcheck":{"Test":["CMD-SHELL","exit 1"],"Retries":2,"Interval":1000000000}},"State":{"Status":"running","Health":{"Status":"STATUS"}}}]""".Replace("STATUS", status, StringComparison.Ordinal));
        Assert.Equal(expected, container.NativeHealth.State);
        Assert.Equal(ContainerState.Running, container.State);
        Assert.Equal("1s", container.NativeHealth.Configuration?.Interval);
    }

    [Theory]
    [InlineData("""{"State":{},"Config":{}}""", NativeHealthState.Absent)]
    [InlineData("{}", NativeHealthState.Unknown)]
    [InlineData("""{"Config":{"Healthcheck":{"Test":["NONE"]}},"State":{}}""", NativeHealthState.Disabled)]
    [InlineData("""{"Config":{"Healthcheck":{"Test":["CMD","true"]}},"State":{}}""", NativeHealthState.Unknown)]
    [InlineData("not json", NativeHealthState.Unknown)]
    [InlineData("[]", NativeHealthState.Unknown)]
    public void DistinguishesAbsentDisabledAndUnreadable(string json, NativeHealthState expected) =>
        Assert.Equal(expected, NativeHealthParser.Parse(json).State);

    [Fact]
    public void ComposePreservesTestSemanticsTimingAndSeparateBudget()
    {
        var project = ComposeImporter.ParseProject("""
            services:
              app:
                image: test:local
                restart: on-failure
                healthcheck:
                  test: ["CMD", "printf", "%s", "$$HOME; exit 1", "a b"]
                  interval: 500ms
                  timeout: 250ms
                  start_period: 1m30s
                  start_interval: 100ms
                  retries: 7
            """);
        var service = Assert.Single(project.Services);
        Assert.Equal(3, service.Health?.MaxRestarts);
        Assert.Equal(7, service.Options.Health?.Retries);
        Assert.Equal(new[] { "CMD", "printf", "%s", "$HOME; exit 1", "a b" }, service.Options.Health?.Test);
        Assert.Equal("100ms", service.Options.Health?.StartInterval);
        Assert.Equal("1m30s", service.Options.Health?.StartPeriod);
        var result = NativeHealthPolicy.Select(service.Options.Health, Capabilities(WslcCapabilitySupport.Unsupported));
        Assert.False(result.Native);
    }

    [Theory]
    [InlineData("test: [NONE]")]
    [InlineData("disable: true")]
    public void ComposeDisableIsNotLost(string disable)
    {
        var service = Assert.Single(ComposeImporter.ParseProject(
            "services:\n  app:\n    image: local\n    healthcheck:\n      " + disable).Services);
        Assert.True(service.Options.Health?.IsDisabled);
        var selected = NativeHealthPolicy.Select(service.Options.Health, Capabilities(WslcCapabilitySupport.Supported));
        Assert.Equal(new[] { "--no-healthcheck" }, selected.Arguments);
    }

    [Fact]
    public void PersistenceAndCloneKeepDesiredHealthAndOldDefaults()
    {
        var old = JsonSerializer.Deserialize<HealthCheckConfig>("""{"ContainerName":"old","Command":"true","MaxRestarts":8}""")!;
        Assert.Equal(8, old.MaxRestarts);
        Assert.Null(old.DesiredHealth);
        var options = new RunContainerOptions { Image = "local", Health = new() { Test = ["CMD", "true"], Retries = 5 } };
        var restored = JsonSerializer.Deserialize<RunContainerOptions>(JsonSerializer.Serialize(options))!;
        var clone = restored.Clone();
        clone.Health!.Test[1] = "false";
        Assert.Equal("true", restored.Health!.Test[1]);
        Assert.Equal(5, clone.Health.Retries);
    }

    [Theory]
    [InlineData(ContainerState.Running, ContainerHealthState.Healthy, true)]
    [InlineData(ContainerState.Stopped, ContainerHealthState.Healthy, false)]
    [InlineData(ContainerState.Running, ContainerHealthState.Unknown, false)]
    [InlineData(ContainerState.Running, ContainerHealthState.Down, false)]
    public void DependenciesRequireRunningAndHealthy(ContainerState state, ContainerHealthState health, bool expected) =>
        Assert.Equal(expected, NativeHealthPolicy.IsDependencyReady(state, health));

    [Fact]
    public void DependencyCannotUsePreviousContainerGenerationOrStaleHealth()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new ContainerInfo { Id = "new-container", StateValue = (int)ContainerState.Running, StateChangedAt = 10 };
        ContainerHealthSnapshot Snapshot(string id, ulong generation, DateTimeOffset observed) => new()
        {
            ContainerId = id, ContainerGeneration = generation, ObservedAt = observed, State = ContainerHealthState.Healthy,
        };
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot("old-container", 10, now), row.Id, now.AddSeconds(-1)));
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot(row.Id, 9, now), row.Id, now.AddSeconds(-1)));
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot(row.Id, 10, now.AddSeconds(-20)), row.Id, now.AddSeconds(-30)));
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot(row.Id, 10, now), "old-container", now.AddSeconds(-1)));
        Assert.True(NativeHealthPolicy.IsDependencyReady(row, Snapshot(row.Id, 10, now), row.Id, now.AddSeconds(-1)));
    }

    [Fact]
    public void AppDependencyEvidenceRemainsFreshUntilItsNextScheduledProbe()
    {
        var now = DateTimeOffset.UtcNow;
        var row = new ContainerInfo { Id = "container", StateValue = (int)ContainerState.Running, StateChangedAt = 10 };
        ContainerHealthSnapshot Snapshot(int ageSeconds, ulong generation = 10) => new()
        {
            ContainerId = row.Id, ContainerGeneration = generation, ObservedAt = now.AddSeconds(-ageSeconds),
            ObservationMaxAge = TimeSpan.FromMinutes(5), State = ContainerHealthState.Healthy,
        };
        Assert.True(NativeHealthPolicy.IsDependencyReady(row, Snapshot(20), row.Id, now.AddMinutes(-1)));
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot(301), row.Id, now.AddMinutes(-6)));
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot(20, 9), row.Id, now.AddMinutes(-1)));
        Assert.False(NativeHealthPolicy.IsDependencyReady(row, Snapshot(20), row.Id, now.AddSeconds(-10)));
    }

    [Fact]
    public void UnsupportedStartIntervalSelectsWholeAppBackendNotPartialNativeFlags()
    {
        var options = new NativeHealthOptions { Test = ["CMD-SHELL", "true"], StartInterval = "100ms", StartPeriod = "10s" };
        var result = NativeHealthPolicy.Select(options, Capabilities(WslcCapabilitySupport.Supported, WslcFeature.HealthStartInterval));
        Assert.False(result.Native);
        Assert.Equal(new[] { "--no-healthcheck" }, result.Arguments);
        Assert.Contains("1s resolution", result.Diagnostic);
    }

    [Fact]
    public void InheritedChecksAndDisabledChecksAreDeliberate()
    {
        var caps = Capabilities(WslcCapabilitySupport.Supported);
        Assert.Empty(NativeHealthPolicy.Select(null, caps).Arguments);
        var inherited = NativeHealthPolicy.Select(new() { Interval = "2s" }, caps);
        Assert.Equal(new[] { "--health-interval", "2s" }, inherited.Arguments);
        Assert.Throws<InvalidOperationException>(() => NativeHealthPolicy.Select(
            new() { Interval = "2s" }, Capabilities(WslcCapabilitySupport.Unsupported)));
        Assert.Equal(new[] { "--no-healthcheck" }, NativeHealthPolicy.Select(new() { Disabled = true }, caps).Arguments);
    }

    [Fact]
    public void ExistingNativeCheckMustMatchDesiredSettingsBeforeAutoheal()
    {
        var config = new HealthCheckConfig
        {
            Command = "true",
            DesiredHealth = new() { Test = ["CMD-SHELL", "true"], Interval = "1m", Retries = 4 },
        };
        Assert.True(NativeHealthPolicy.MatchesDesiredCheck(config,
            new() { Test = ["CMD-SHELL", "true"], Interval = "60s", Retries = 4 }));
        Assert.False(NativeHealthPolicy.MatchesDesiredCheck(config,
            new() { Test = ["CMD-SHELL", "false"], Interval = "60s", Retries = 4 }));
        Assert.False(NativeHealthPolicy.MatchesDesiredCheck(config,
            new() { Test = ["CMD-SHELL", "true"], Interval = "60s", Retries = 3 }));
    }

    [Fact]
    public async Task LiveObservationsHaveBoundedConcurrencyAndValidateIdentity()
    {
        var monitor = new NativeHealthMonitor();
        var rows = Enumerable.Range(0, 9).Select(i => new ContainerInfo
        {
            Id = "id-" + i, Name = "test-" + i, StateValue = (int)ContainerState.Running,
        }).ToArray();
        var active = 0;
        var maximum = 0;
        await monitor.RefreshAsync(rows, async (id, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            await Task.Delay(10, ct);
            Interlocked.Decrement(ref active);
            return new CommandResult { StandardOutput = """{"Id":"IDENTITY","State":{"Health":{"Status":"healthy"}}}""".Replace("IDENTITY", id) };
        }, _ => { });
        Assert.InRange(maximum, 1, 4);
        Assert.All(rows, row => Assert.Equal(NativeHealthState.Healthy, row.NativeHealth.State));
        await monitor.RefreshAsync(rows, (_, _) => Task.FromResult(new CommandResult
        {
            StandardOutput = """{"Id":"wrong","State":{"Health":{"Status":"healthy"}}}""",
        }), _ => { });
        Assert.All(rows, row => Assert.Equal(NativeHealthState.Unknown, row.NativeHealth.State));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("0s")]
    [InlineData("-1s")]
    public void InvalidTimingIsNotSilentlyDropped(string interval) =>
        Assert.Throws<InvalidOperationException>(() => NativeHealthPolicy.Select(
            new() { Test = ["CMD-SHELL", "true"], Interval = interval }, Capabilities(WslcCapabilitySupport.Supported)));

    [Fact]
    public void ComposeRestartCountDoesNotUseHealthRetries()
    {
        var service = Assert.Single(ComposeImporter.ParseProject("""
            services:
              app:
                image: local
                restart: on-failure:4
                healthcheck:
                  test: "true"
                  retries: 9
            """).Services);
        Assert.Equal(4, service.Health?.MaxRestarts);
        Assert.Equal(9, service.Options.Health?.Retries);
    }

    [Theory]
    [InlineData("500ms", .5)]
    [InlineData("1m30s", 90)]
    [InlineData("0s", 0)]
    public void DurationsPreservePrecision(string value, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), NativeHealthPolicy.Duration(value, allowZero: true));

    [Fact]
    public async Task ObservationFailureDoesNotReuseHealthy()
    {
        var monitor = new NativeHealthMonitor();
        var rows = new[] { new ContainerInfo { Id = "id", Name = "test", StateValue = (int)ContainerState.Running } };
        await monitor.RefreshAsync(rows, (_, _) => Task.FromResult(new CommandResult
        {
            StandardOutput = """{"Id":"id","State":{"Health":{"Status":"healthy"}}}""",
        }), _ => { });
        Assert.Equal(NativeHealthState.Healthy, rows[0].NativeHealth.State);
        await monitor.RefreshAsync(rows, (_, _) => Task.FromResult(new CommandResult { ExitCode = 1, StandardError = "failure" }), _ => { });
        Assert.Equal(NativeHealthState.Unknown, rows[0].NativeHealth.State);
    }

    [Fact]
    public async Task AbsentChecksAreCachedUntilRefreshOrRestart()
    {
        var monitor = new NativeHealthMonitor();
        var rows = new[] { new ContainerInfo { Id = "id", Name = "test", StateValue = (int)ContainerState.Running } };
        var calls = 0;
        Task<CommandResult> Inspect(string _, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(new CommandResult { StandardOutput = """{"Id":"id","Config":{},"State":{}}""" });
        }
        await monitor.RefreshAsync(rows, Inspect, _ => { });
        await monitor.RefreshAsync(rows, Inspect, _ => { });
        Assert.Equal(1, calls);
        rows[0].StateChangedAt++;
        await monitor.RefreshAsync(rows, Inspect, _ => { });
        Assert.Equal(2, calls);
        monitor.Invalidate();
        await monitor.RefreshAsync(rows, Inspect, _ => { });
        Assert.Equal(3, calls);
    }
}
