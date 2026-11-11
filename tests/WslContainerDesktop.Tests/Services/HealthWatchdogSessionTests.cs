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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services
{
    public sealed class HealthWatchdogSessionTests
    {
        [Fact]
        public void OldSessionObservationCannotScheduleAProbe()
        {
            using var fixture = new Fixture();
            fixture.ObserveRunningContainer();
            Assert.True(fixture.Context.TrySelect("another"));

            fixture.Watchdog.Tick(default);

            Assert.Empty(fixture.Calls);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("engine", "another")]
        [InlineData("other-engine", "first")]
        public void UnassignedOrForeignPolicyDoesNotRun(string? executable, string? session)
        {
            using var fixture = new Fixture
            {
                Owner = executable is null ? null : AutomationSessionOwner.Capture(executable, new(session)),
            };
            fixture.ObserveRunningContainer();

            fixture.Watchdog.Tick(default);

            Assert.Empty(fixture.Calls);
        }

        [Fact]
        public void ExplicitlyAssignedLegacyPolicyResumesForItsOwner()
        {
            using var fixture = new Fixture { Owner = null };
            fixture.LegacyOwners.Assign(fixture.Policy(), AutomationSessionOwner.Capture("engine", new("first")));
            fixture.ObserveRunningContainer();

            fixture.Watchdog.Tick(default);

            Assert.Equal(["probe", "stop", "start"], fixture.Calls);
        }

        [Fact]
        public async Task SelectionChangeAfterProbePreventsAutoheal()
        {
            using var fixture = new Fixture { BlockProbe = true };
            fixture.ObserveRunningContainer();
            fixture.Watchdog.Tick(default);
            await fixture.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fixture.Context.TrySelect("another"));

            var switched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Context.SelectionUnlocked += (_, _) =>
            {
                if (fixture.Context.TrySelect("another"))
                    switched.TrySetResult();
            };
            fixture.ReleaseProbe.SetResult(new CommandResult { ExitCode = 1 });
            await switched.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["probe"], fixture.Calls);
        }

        [Fact]
        public async Task AutohealPinsSessionUntilStopAndStartFinish()
        {
            using var fixture = new Fixture { BlockStop = true };
            fixture.ObserveRunningContainer();
            fixture.Watchdog.Tick(default);
            await fixture.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(fixture.Context.TrySelect("another"));
            Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
            var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Context.SelectionUnlocked += (_, _) => unlocked.TrySetResult();
            fixture.ReleaseStop.SetResult(new CommandResult());
            await unlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["probe", "stop", "start"], fixture.Calls);
            Assert.True(fixture.Context.TrySelect("another"));
        }

        [Fact]
        public void QueuedHealthNotificationsAreDiscardedAfterSessionChange()
        {
            using var fixture = new Fixture();
            var notifications = new List<string>();
            fixture.Watchdog.NotificationRequested += (title, _) => notifications.Add(title);
            fixture.ObserveRunningContainer();
            fixture.Watchdog.Tick(default);
            Assert.True(fixture.Context.TrySelect("another"));

            fixture.Monitor.Dispatcher.Drain();

            Assert.Equal(["probe", "stop", "start"], fixture.Calls);
            Assert.Empty(notifications);
        }

        [Fact]
        public void SessionChangeClearsPriorHealthObservations()
        {
            using var fixture = new Fixture();
            fixture.ObserveRunningContainer();
            fixture.Watchdog.Tick(default);
            Assert.NotEmpty(fixture.Watchdog.Latest.Containers);

            Assert.True(fixture.Context.TrySelect("another"));

            Assert.Empty(fixture.Watchdog.Latest.Containers);
        }

        [Fact]
        public async Task HostProbeDoesNotBlockSelectionAndCannotAutohealAfterSwitch()
        {
            using var fixture = new Fixture { UseTcp = true };
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Watchdog.EvaluationCompleted += () => completed.TrySetResult();
            fixture.ObserveRunningContainer();
            fixture.Watchdog.Tick(default);
            await fixture.TcpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(fixture.Context.TrySelect("another"));
            fixture.ReleaseTcp.SetResult(false);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(fixture.Calls);
        }

        [Fact]
        public async Task ReplacedScopedSessionCannotBeAutohealed()
        {
            using var fixture = new Fixture(scoped: true) { ReplaceAfterFirstVerify = true };
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Watchdog.EvaluationCompleted += () => completed.TrySetResult();
            fixture.Watchdog.UpdateScopedObservation([new ContainerInfo
            {
                Id = "container-id", Name = "web", StateValue = (int)ContainerState.Running,
                NativeHealth = new NativeHealthObservation(NativeHealthState.Absent),
            }]);

            fixture.Watchdog.Tick(default);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(["probe"], fixture.Calls);
            Assert.Equal(2, fixture.VerificationCount);
        }

        private sealed class Fixture : IDisposable
        {
            public WslcSessionContext Context { get; } = new();
            public StatusMonitor Monitor { get; } = new();
            private readonly string _ownerRoot = Path.Combine(Path.GetTempPath(), "wcd-health-owners-" + Guid.NewGuid().ToString("N"));
            public LegacyPolicyOwnershipStore LegacyOwners { get; }
            public List<string> Calls { get; } = [];
            public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<CommandResult> ReleaseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<CommandResult> ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource TcpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> ReleaseTcp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool BlockProbe { get; set; }
            public bool BlockStop { get; set; }
            public bool UseTcp { get; set; }
            public bool ReplaceAfterFirstVerify { get; set; }
            public int VerificationCount { get; private set; }
            public AutomationSessionOwner? Owner { get; set; } = AutomationSessionOwner.Capture("engine", new("first"));
            public HealthWatchdogSessionHarness Watchdog { get; }
            private RestartPolicyWatchdog Restart { get; }

            public Fixture(bool scoped = false)
            {
                Context.Select("first");
                LegacyOwners = new LegacyPolicyOwnershipStore(NullLogger<LegacyPolicyOwnershipStore>.Instance,
                    Path.Combine(_ownerRoot, "owners.json"));
                var engine = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
                {
                    nameof(IWslcService.ExecAsync) => ProbeAsync(),
                    nameof(IWslcService.StopContainerAsync) => StopAsync(),
                    nameof(IWslcService.StartContainerAsync) => StartAsync(),
                    _ => throw new InvalidOperationException(method.Name),
                });
                var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
                {
                    "get_HealthChecks" => new List<HealthCheckConfig> { Policy() },
                    "get_RestartPolicies" => new List<RestartPolicyConfig>(),
                    "get_WslcPath" => "engine",
                    _ => throw new InvalidOperationException(method.Name),
                });
                Restart = new RestartPolicyWatchdog(engine, Monitor, settings,
                    NullLogger<RestartPolicyWatchdog>.Instance, new RestartSuppressionState(), Context, LegacyOwners);
                var generation = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
                var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
                    method.Name == nameof(IWslcService.GetSystemInfoAsync)
                        ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo
                            { Sessions = [++VerificationCount > 1 && ReplaceAfterFirstVerify ? generation with { ID = 2 } : generation] } })
                        : throw new InvalidOperationException(method.Name));
                Watchdog = new HealthWatchdogSessionHarness(engine, Monitor, settings,
                    NullLogger<HealthWatchdogSessionHarness>.Instance, Restart, Context, LegacyOwners, ProbeTcpAsync,
                    scoped ? generation : null, scoped ? "engine" : null, scoped ? discovery : null);
            }

            public HealthCheckConfig Policy() => UseTcp
                ? new() { ContainerName = "web", Kind = HealthProbeKind.Tcp, TcpPort = 12345, MaxRestarts = 1, SessionOwner = Owner }
                : new() { ContainerName = "web", Kind = HealthProbeKind.Command, Command = "probe", MaxRestarts = 1, SessionOwner = Owner };

            public void ObserveRunningContainer() => Watchdog.OnStatusChanged(Monitor, new EngineStatusSnapshot
            {
                Containers = [new ContainerInfo
                {
                    Id = "container-id", Name = "web", StateValue = (int)ContainerState.Running,
                    NativeHealth = new NativeHealthObservation(NativeHealthState.Absent),
                }],
            });

            private Task<CommandResult> ProbeAsync()
            {
                Calls.Add("probe");
                ProbeStarted.TrySetResult();
                return BlockProbe ? ReleaseProbe.Task : Task.FromResult(new CommandResult { ExitCode = 1 });
            }

            private Task<CommandResult> StopAsync()
            {
                Calls.Add("stop");
                StopStarted.TrySetResult();
                return BlockStop ? ReleaseStop.Task : Task.FromResult(new CommandResult());
            }

            private Task<CommandResult> StartAsync()
            {
                Calls.Add("start");
                return Task.FromResult(new CommandResult());
            }

            private async Task<bool> ProbeTcpAsync(int port, CancellationToken token)
            {
                TcpStarted.TrySetResult();
                return await ReleaseTcp.Task.WaitAsync(token);
            }

            public void Dispose()
            {
                Watchdog.Dispose();
                Restart.Dispose();
                if (Directory.Exists(_ownerRoot))
                    Directory.Delete(_ownerRoot, recursive: true);
            }
        }
    }
}
