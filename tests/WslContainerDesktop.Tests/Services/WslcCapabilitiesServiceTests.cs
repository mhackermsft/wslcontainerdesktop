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

using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class WslcCapabilitiesServiceTests
{
    [Fact]
    public async Task CurrentHelp_RecordsEachFeatureAndOnlyLaunchesNonmutatingProbes()
    {
        using var fixture = new Fixture();
        var snapshot = await fixture.Service.GetAsync();

        Assert.Equal("2.9.11.0", snapshot.Version);
        Assert.False(snapshot.HasProbeFailures);
        foreach (var feature in Enum.GetValues<WslcFeature>())
        {
            Assert.Equal(feature is WslcFeature.HealthStartInterval or WslcFeature.CreateHealthStartInterval
                ? WslcCapabilitySupport.Unsupported : WslcCapabilitySupport.Supported, snapshot[feature].Support);
        }

        Assert.Equal(["--version", "network --help", "container --help", "run --help", "create --help"],
            fixture.Calls.Select(call => call.Arguments));
        Assert.All(fixture.Calls, call => Assert.Equal(fixture.Path, call.Path));
        Assert.Empty(fixture.Warnings);
    }

    [Theory]
    [InlineData("2.9.9.0")]
    [InlineData("2.9.11.0")]
    [InlineData("99.0.0.0")]
    public async Task LegacyHelp_DoesNotInferSupportFromVersion(string version)
    {
        using var fixture = new Fixture(legacy: true);
        fixture.Responses["--version"] = Ok($"wslc {version}");
        var snapshot = await fixture.Service.GetAsync();

        Assert.False(snapshot.HasProbeFailures);
        Assert.All(Enum.GetValues<WslcFeature>(), feature =>
            Assert.Equal(WslcCapabilitySupport.Unsupported, snapshot[feature].Support));
    }

    [Fact]
    public async Task RunAndCreateFlagsAndNetworkCommands_AreIndependentExactTokens()
    {
        using var fixture = new Fixture();
        fixture.Responses["network --help"] = Ok(Help("current", "network")
            .Replace("  connect     Connect a container to a network.\n", ""));
        fixture.Responses["run --help"] = Ok(Help("current", "run")
            .Replace("      --health-cmd           Command to run to check container health\n", "")
            .Replace("      --health-interval      Time", "      --health-interval-extra  Time"));
        var snapshot = await fixture.Service.GetAsync();

        Assert.Equal(WslcCapabilitySupport.Unsupported, snapshot[WslcFeature.NetworkConnect].Support);
        Assert.True(snapshot.IsSupported(WslcFeature.NetworkDisconnect));
        Assert.Equal(WslcCapabilitySupport.Unsupported, snapshot[WslcFeature.HealthCmd].Support);
        Assert.Equal(WslcCapabilitySupport.Unsupported, snapshot[WslcFeature.HealthInterval].Support);
        Assert.True(snapshot.IsSupported(WslcFeature.HealthTimeout));
        Assert.True(snapshot.IsSupported(WslcFeature.CreateHealthCmd));
        Assert.True(snapshot.IsSupported(WslcFeature.CreateHealthInterval));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Usage: wslc [<command>] [<options>]\nCommands:\n  cp  Copy.\n")]
    [InlineData("Usage: wslc container [<command>]\nCommands:\n  cp  Copy.")]
    [InlineData("An error mentions cp and --health-cmd but is not command help.")]
    public async Task EmptyWrongOrTruncatedHelp_IsUnknown(string help)
    {
        using var fixture = new Fixture();
        fixture.Responses["container --help"] = Ok(help);
        var snapshot = await fixture.Service.GetAsync();

        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.ContainerCp].Support);
        Assert.Contains("Check the executable path", snapshot[WslcFeature.ContainerCp].Diagnostic);
        Assert.Single(fixture.Warnings);
        Assert.True(snapshot.IsSupported(WslcFeature.NetworkConnect));
    }

    [Fact]
    public async Task FailedHelp_DoesNotTrustEvenRecognizableOutput()
    {
        using var fixture = new Fixture();
        fixture.Responses["network --help"] = new()
        {
            ExitCode = 1,
            StandardOutput = Help("current", "network"),
            StandardError = "Access denied",
        };
        var snapshot = await fixture.Service.GetAsync();

        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.NetworkConnect].Support);
        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.NetworkDisconnect].Support);
        Assert.Contains("Access denied", snapshot[WslcFeature.NetworkConnect].Diagnostic);
        Assert.True(snapshot.IsSupported(WslcFeature.ContainerCp));
    }

    [Theory]
    [InlineData("<COMMAND>")]
    [InlineData("[COMMAND]")]
    public async Task OptionArgumentPlaceholder_PreservesExactFeatureEvidence(string placeholder)
    {
        using var fixture = new Fixture();
        fixture.Responses["run --help"] = Ok(Help("current", "run")
            .Replace("--health-cmd           ", $"--health-cmd {placeholder}  "));

        var snapshot = await fixture.Service.GetAsync();

        Assert.True(snapshot.IsSupported(WslcFeature.HealthCmd));
        Assert.False(snapshot.HasProbeFailures);
    }

    [Theory]
    [InlineData("run", "Options:\n  --help  Show help.\n  --health-cmd")]
    [InlineData("run", "Options:\n  --help  Show help.\n  --health-cmd <COMMAND")]
    [InlineData("run", "Options:\n  --help  Show help.\n  --health-cmd COMMAND  Check health.")]
    [InlineData("network", "Commands:\n  ls  List networks.\n  connect\n\nOptions:\n  --help  Show help.")]
    public async Task UnparsedHelpRows_AreUnknownInsteadOfConfirmedAbsence(string command, string section)
    {
        using var fixture = new Fixture();
        fixture.Responses[$"{command} --help"] = Ok($"Usage: wslc {command} [<options>]\n{section}");

        var snapshot = await fixture.Service.GetAsync();

        var feature = command == "run" ? WslcFeature.HealthCmd : WslcFeature.NetworkConnect;
        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[feature].Support);
        Assert.NotNull(snapshot[feature].Diagnostic);
        Assert.Single(fixture.Warnings);
        Assert.True(snapshot.IsSupported(WslcFeature.ContainerCp));
    }

    [Fact]
    public async Task VersionFailure_DoesNotEraseIndependentHelpEvidence()
    {
        using var fixture = new Fixture();
        fixture.Responses["--version"] = new() { ExitCode = -1, StandardError = "Could not launch" };
        var snapshot = await fixture.Service.GetAsync();

        Assert.Null(snapshot.Version);
        Assert.Contains("Could not launch", snapshot.VersionDiagnostic);
        Assert.True(snapshot.HasProbeFailures);
        Assert.True(snapshot.IsSupported(WslcFeature.ContainerCp));
    }

    [Fact]
    public async Task SuccessfulHelpOnStderr_IsRecognized()
    {
        using var fixture = new Fixture();
        fixture.Responses["container --help"] = new() { StandardError = Help("current", "container") };
        Assert.True((await fixture.Service.GetAsync()).IsSupported(WslcFeature.ContainerCp));
    }

    [Fact]
    public async Task ProbeTimeout_IsUnknownAndDoesNotPoisonUnrelatedFeatures()
    {
        using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(50));
        fixture.BeforeResponse = async (_, arguments, ct) =>
        {
            if (arguments == "network --help")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var snapshot = await fixture.Service.GetAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.NetworkConnect].Support);
        Assert.Contains("timed out", snapshot[WslcFeature.NetworkConnect].Diagnostic);
        Assert.True(snapshot.IsSupported(WslcFeature.ContainerCp));
    }

    [Fact]
    public async Task ProcessIoFailure_IsDiagnosticUnknown()
    {
        using var fixture = new Fixture();
        fixture.BeforeResponse = (_, arguments, _) =>
            arguments == "container --help"
                ? Task.FromException(new IOException("Output pipe was closed"))
                : Task.CompletedTask;
        var snapshot = await fixture.Service.GetAsync();
        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.ContainerCp].Support);
        Assert.Contains("Output pipe was closed", snapshot[WslcFeature.ContainerCp].Diagnostic);
        Assert.Single(fixture.Warnings);
    }

    [Fact]
    public async Task UnexpectedProbeCancellation_IsNotMisreportedAsUnsupportedOrTimeout()
    {
        using var fixture = new Fixture();
        fixture.BeforeResponse = (_, arguments, _) =>
            arguments == "container --help"
                ? Task.FromCanceled(new CancellationToken(true))
                : Task.CompletedTask;
        var snapshot = await fixture.Service.GetAsync();
        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.ContainerCp].Support);
        Assert.Contains("cancelled", snapshot[WslcFeature.ContainerCp].Diagnostic);
        Assert.DoesNotContain("timed out", snapshot[WslcFeature.ContainerCp].Diagnostic);
    }

    [Fact]
    public async Task ConcurrentCallersAndCache_ShareOneProbeSequence()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeResponse = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var calls = Enumerable.Range(0, 20).Select(_ => fixture.Service.GetAsync()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Calls);
        release.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, snapshot => Assert.Same(results[0], snapshot));
        Assert.Same(results[0], await fixture.Service.GetAsync());
        Assert.Equal(5, fixture.Calls.Count);
    }

    [Fact]
    public async Task CallerCancellation_CancelsOnlyItsWait()
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeResponse = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var cancelled = fixture.Service.GetAsync(caller.Token);
        var other = fixture.Service.GetAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();

        Assert.True((await other).IsSupported(WslcFeature.ContainerCp));
        Assert.Equal(5, fixture.Calls.Count);
    }

    [Fact]
    public async Task AlreadyCancelledCaller_DoesNotLaunchProbe()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.GetAsync(new CancellationToken(true)));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task PathChangesDuringProbe_DoNotPublishMixedOrStaleEvidence()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = fixture.Path;
        fixture.BeforeResponse = async (path, _, ct) =>
        {
            if (path == original)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var first = fixture.Service.GetAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Path = @"C:\different\wslc.exe";
        var second = await fixture.Service.GetAsync();
        release.SetResult();

        Assert.Same(second, await first);
        Assert.Equal(fixture.Path, second.ExecutablePath);
        Assert.Equal(5, fixture.Calls.Count(call => call.Path == original));
        Assert.Equal(5, fixture.Calls.Count(call => call.Path == fixture.Path));
    }

    [Fact]
    public async Task BinaryReplacementAndExplicitInvalidation_RefreshEvidence()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.GetAsync();
        fixture.Revision++;
        var replacement = await fixture.Service.GetAsync();
        Assert.NotSame(first, replacement);
        fixture.Service.Invalidate();
        Assert.NotSame(replacement, await fixture.Service.GetAsync());
        Assert.Equal(15, fixture.Calls.Count);
    }

    [Fact]
    public async Task ExplicitInvalidationDuringProbe_DiscardsOldResult()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeResponse = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var first = fixture.Service.GetAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Service.Invalidate();
        release.SetResult();

        var result = await first;
        Assert.Same(result, await fixture.Service.GetAsync());
        Assert.Equal(10, fixture.Calls.Count);
    }

    [Fact]
    public async Task UnknownCache_RetriesAfterShortExpiry()
    {
        using var fixture = new Fixture();
        fixture.Responses["container --help"] = new() { ExitCode = -1, StandardError = "Temporary failure" };
        var unknown = await fixture.Service.GetAsync();
        fixture.Responses["container --help"] = Ok(Help("current", "container"));
        Assert.Same(unknown, await fixture.Service.GetAsync());
        fixture.Clock.Advance(TimeSpan.FromSeconds(16));
        Assert.True((await fixture.Service.GetAsync()).IsSupported(WslcFeature.ContainerCp));
        Assert.Equal(10, fixture.Calls.Count);
    }

    [Fact]
    public async Task SlowFailedProbe_CacheLifetimeStartsAfterCompletion()
    {
        using var fixture = new Fixture();
        fixture.Responses["network --help"] = new() { ExitCode = -1, StandardError = "Temporary failure" };
        fixture.BeforeResponse = (_, _, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        };
        var snapshot = await fixture.Service.GetAsync();
        Assert.True(snapshot.HasProbeFailures);
        Assert.Same(snapshot, await fixture.Service.GetAsync());
        Assert.Equal(5, fixture.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOnlyWaiter_DoesNotDelayCompletedProbeExpiry(bool failedProbe)
    {
        using var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (failedProbe)
        {
            fixture.Responses["container --help"] = new() { ExitCode = -1, StandardError = "Temporary failure" };
        }
        fixture.BeforeResponse = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var waiter = fixture.Service.GetAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        // Observe the shared task directly: another GetAsync waiter would mask the regression.
        var entry = typeof(WslcCapabilitiesService)
            .GetField("_entry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Service)!;
        var probe = Assert.IsAssignableFrom<Task>(entry.GetType().GetProperty("Task")!.GetValue(entry));
        release.SetResult();
        await probe.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, fixture.Calls.Count);
        fixture.Responses["container --help"] = Ok(Help("current", "container"));
        fixture.Clock.Advance(failedProbe ? TimeSpan.FromSeconds(16) : TimeSpan.FromMinutes(6));

        var snapshot = await fixture.Service.GetAsync();

        Assert.False(snapshot.HasProbeFailures);
        Assert.Equal(10, fixture.Calls.Count);
    }

    [Fact]
    public async Task SuccessfulCache_ExpiresEvenWhenMetadataIsUnchanged()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.GetAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Same(first, await fixture.Service.GetAsync());
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotSame(first, await fixture.Service.GetAsync());
        Assert.Equal(10, fixture.Calls.Count);
    }

    [Fact]
    public async Task MissingBinary_IsUnknownWithoutLaunchingOrThrowing()
    {
        using var fixture = new Fixture();
        fixture.IdentityError = "WSLC executable not found. Check Settings.";
        var snapshot = await fixture.Service.GetAsync();
        Assert.All(Enum.GetValues<WslcFeature>(), feature =>
        {
            Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[feature].Support);
            Assert.Contains("not found", snapshot[feature].Diagnostic);
        });
        Assert.Same(snapshot, await fixture.Service.GetAsync());
        Assert.Empty(fixture.Calls);
        Assert.Single(fixture.Warnings);
    }

    [Fact]
    public void Snapshot_IsImmutableAndMissingFeatureIsUnknown()
    {
        var features = new Dictionary<WslcFeature, WslcCapability>
        {
            [WslcFeature.ContainerCp] = new(WslcCapabilitySupport.Supported),
        };
        var snapshot = new WslcCapabilities("wslc.exe", null, features);
        features.Clear();
        Assert.True(snapshot.IsSupported(WslcFeature.ContainerCp));
        Assert.Equal(WslcCapabilitySupport.Unknown, snapshot[WslcFeature.NetworkConnect].Support);
        Assert.NotNull(snapshot[WslcFeature.NetworkConnect].Diagnostic);
    }

    [Fact]
    public void IdentityRead_MissingAndInvalidPathsAreDiagnostic()
    {
        Assert.NotNull(WslcExecutableIdentity.Read(@"Z:\no-such-wslc-fixture\wslc.exe").Diagnostic);
        Assert.NotNull(WslcExecutableIdentity.Read("").Diagnostic);
        Assert.NotNull(WslcExecutableIdentity.Read("invalid\0path").Diagnostic);
    }

    [Fact]
    public async Task SettingsEvents_InvalidateOnlyWhenConfiguredPathChanges()
    {
        var settings = DispatchProxy.Create<ISettingsService, SettingsProxy>();
        settings.WslcPath = @"Z:\missing-capability-test-engine-a\wslc.exe";
        using var service = new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance);
        var first = await service.GetAsync();
        settings.Save();
        Assert.Same(first, await service.GetAsync());

        settings.WslcPath = @"Z:\missing-capability-test-engine-b\wslc.exe";
        settings.Save();
        settings.WslcPath = first.ExecutablePath;
        Assert.NotSame(first, await service.GetAsync());
        Assert.Equal(1, ((SettingsProxy)(object)settings).SubscriberCount);
        service.Dispose();
        Assert.Equal(0, ((SettingsProxy)(object)settings).SubscriberCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetAsync());
    }

    [Fact]
    public void IdentityRead_DetectsBinaryReplacementAtSamePath()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wslc-capabilities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = System.IO.Path.Combine(directory, "wslc.exe");
        try
        {
            File.Copy(typeof(WslcCapabilitiesServiceTests).Assembly.Location, file);
            var first = WslcExecutableIdentity.Read(file);
            Assert.Null(first.Diagnostic);
            File.SetLastWriteTimeUtc(file, new DateTime(first.LastWriteTicks, DateTimeKind.Utc).AddMinutes(-1));
            var modified = WslcExecutableIdentity.Read(file);
            Assert.NotEqual(first, modified);
            File.Copy(typeof(CommandResult).Assembly.Location, file, overwrite: true);
            File.SetLastWriteTimeUtc(file, new DateTime(first.LastWriteTicks, DateTimeKind.Utc).AddMinutes(1));
            Assert.NotEqual(modified, WslcExecutableIdentity.Read(file));
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }

    [InstalledEngineFact]
    public async Task InstalledEngine_HelpOnlySmoke_UsesProductionServiceAndSharedCache()
    {
        var settings = DispatchProxy.Create<ISettingsService, SettingsProxy>();
        settings.WslcPath = Environment.GetEnvironmentVariable("WSLCD_CAPABILITY_SMOKE_EXE")!;
        using var service = new WslcCapabilitiesService(settings, NullLogger<WslcCapabilitiesService>.Instance);
        var snapshot = await service.GetAsync().WaitAsync(TimeSpan.FromSeconds(25));

        Assert.NotNull(snapshot.Version);
        Assert.False(snapshot.HasProbeFailures, string.Join("\n", Enum.GetValues<WslcFeature>()
            .Select(feature => snapshot[feature].Diagnostic).Prepend(snapshot.VersionDiagnostic)));
        Assert.All(Enum.GetValues<WslcFeature>(), feature =>
            Assert.NotEqual(WslcCapabilitySupport.Unknown, snapshot[feature].Support));
        Assert.Same(snapshot, await service.GetAsync());
    }

    public sealed class InstalledEngineFactAttribute : FactAttribute
    {
        public InstalledEngineFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSLCD_CAPABILITY_SMOKE_EXE")))
            {
                Skip = "Opt in to nonmutating installed-engine smoke with WSLCD_CAPABILITY_SMOKE_EXE.";
            }
        }
    }

    public class SettingsProxy : DispatchProxy
    {
        private string _path = "";
        private EventHandler? _changed;
        public int SubscriberCount => _changed?.GetInvocationList().Length ?? 0;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_WslcPath":
                    return _path;
                case "set_WslcPath":
                    _path = (string)args![0]!;
                    return null;
                case "add_Changed":
                    _changed += (EventHandler)args![0]!;
                    return null;
                case "remove_Changed":
                    _changed -= (EventHandler)args![0]!;
                    return null;
                case "Save":
                    _changed?.Invoke(this, EventArgs.Empty);
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    private static CommandResult Ok(string output) => new() { StandardOutput = output };

    // Current fixtures are recorded help sections from 2.9.11.0; legacy fixtures model
    // the 2.9.9 baseline without optional commands, not a claimed recording of an old binary.
    private static string Help(string variant, string command) =>
        File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Capabilities",
            $"{variant}-{command}.txt")).Replace("\r", "");

    private sealed class Fixture : IDisposable
    {
        internal string Path = @"C:\fixture\wslc.exe";
        internal int Revision;
        internal string? IdentityError;
        internal readonly ConcurrentQueue<(string Path, string Arguments)> Calls = new();
        internal readonly ConcurrentQueue<string> Warnings = new();
        internal readonly Dictionary<string, CommandResult> Responses;
        internal readonly TestClock Clock = new();
        internal Func<string, string, CancellationToken, Task>? BeforeResponse;
        internal readonly WslcCapabilitiesService Service;

        internal Fixture(bool legacy = false, TimeSpan? timeout = null)
        {
            var variant = legacy ? "legacy" : "current";
            Responses = new()
            {
                ["--version"] = Ok(legacy ? "wslc 2.9.9.0" : "wslc 2.9.11.0"),
                ["network --help"] = Ok(Help(variant, "network")),
                ["container --help"] = Ok(Help(variant, "container")),
                ["run --help"] = Ok(Help(variant, "run")),
                ["create --help"] = Ok(Help(variant, "run").Replace("Usage: wslc run ", "Usage: wslc create ")),
            };
            Service = new(() => Path,
                path => new(path, path, LastWriteTicks: Revision, Diagnostic: IdentityError),
                async (path, arguments, ct) =>
                {
                    var key = string.Join(" ", arguments);
                    Calls.Enqueue((path, key));
                    if (BeforeResponse is { } before)
                    {
                        await before(path, key, ct);
                    }
                    return Responses[key];
                }, Warnings.Enqueue, Clock, timeout);
        }

        public void Dispose() => Service.Dispose();
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
