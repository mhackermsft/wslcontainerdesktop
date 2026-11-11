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

namespace WslContainerDesktop.Tests.Services;

public sealed class RestartPolicySessionTests
{
    [Fact]
    public void OldSessionObservationCannotScheduleRestart()
    {
        using var fixture = new Fixture();
        fixture.ObserveStoppedContainer();
        Assert.True(fixture.Context.TrySelect("another"));

        fixture.Watchdog.Tick(default);

        Assert.Empty(fixture.Starts);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("engine", "another")]
    [InlineData("other-engine", "first")]
    public void UnassignedOrForeignPolicyDoesNotRestart(string? executable, string? session)
    {
        using var fixture = new Fixture
        {
            Owner = executable is null ? null : AutomationSessionOwner.Capture(executable, new(session)),
        };
        fixture.ObserveStoppedContainer();

        fixture.Watchdog.Tick(default);

        Assert.Empty(fixture.Starts);
    }

    [Fact]
    public void ExplicitlyAssignedLegacyPolicyResumesForItsOwner()
    {
        using var fixture = new Fixture { Owner = null };
        fixture.LegacyOwners.Assign(fixture.Policy(), AutomationSessionOwner.Capture("engine", new("first")));
        fixture.ObserveStoppedContainer();

        fixture.Watchdog.Tick(default);

        Assert.Equal(["container-id"], fixture.Starts);
    }

    [Fact]
    public async Task InFlightRestartPinsSessionAndExecutable()
    {
        using var fixture = new Fixture { BlockStart = true };
        fixture.ObserveStoppedContainer();
        fixture.Watchdog.Tick(default);
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
        var unlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Context.SelectionUnlocked += (_, _) => unlocked.TrySetResult();
        fixture.Release.SetResult(new CommandResult());
        await unlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["container-id"], fixture.Starts);
        Assert.True(fixture.Context.TrySelect("another"));
    }

    [Fact]
    public void QueuedRestartNotificationIsDiscardedAfterSessionChange()
    {
        using var fixture = new Fixture();
        var notifications = new List<string>();
        fixture.Watchdog.NotificationRequested += (title, _) => notifications.Add(title);
        fixture.ObserveStoppedContainer();
        fixture.Watchdog.Tick(default);
        Assert.True(fixture.Context.TrySelect("another"));

        fixture.Monitor.Dispatcher.Drain();

        Assert.Single(fixture.Starts);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task ReplacedScopedSessionIsNeverRestarted()
    {
        using var fixture = new Fixture(scoped: true) { ReplaceAfterFirstVerify = true };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Watchdog.EvaluationCompleted += () => completed.TrySetResult();
        fixture.Watchdog.UpdateScopedObservation([new ContainerInfo
        {
            Id = "container-id", Name = "web", StateValue = (int)ContainerState.Stopped,
        }]);

        fixture.Watchdog.Tick(default);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(fixture.Starts);
        Assert.Equal(2, fixture.VerificationCount);
    }

    [Fact]
    public async Task TemporaryObservationLossDoesNotResetRestartBudget()
    {
        using var fixture = new Fixture(scoped: true) { MaximumRestarts = 1 };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Watchdog.EvaluationCompleted += () => completed.TrySetResult();
        var stopped = new ContainerInfo { Id = "container-id", Name = "web", StateValue = (int)ContainerState.Stopped };
        fixture.Watchdog.UpdateScopedObservation([stopped]);
        fixture.Watchdog.Tick(default);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Watchdog.PauseScopedObservation();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        fixture.Watchdog.UpdateScopedObservation([stopped]);
        fixture.Watchdog.Tick(default);

        Assert.Single(fixture.Starts);
    }

    private sealed class Fixture : IDisposable
    {
        public WslcSessionContext Context { get; } = new();
        public StatusMonitor Monitor { get; } = new();
        private readonly string _ownerRoot = Path.Combine(Path.GetTempPath(), "wcd-restart-owners-" + Guid.NewGuid().ToString("N"));
        public LegacyPolicyOwnershipStore LegacyOwners { get; }
        public List<string> Starts { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CommandResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockStart { get; set; }
        public bool ReplaceAfterFirstVerify { get; set; }
        public int MaximumRestarts { get; set; } = 3;
        public ManualClock Clock { get; } = new();
        public int VerificationCount { get; private set; }
        public AutomationSessionOwner? Owner { get; set; } = AutomationSessionOwner.Capture("engine", new("first"));
        public RestartPolicyWatchdog Watchdog { get; }

        public Fixture(bool scoped = false)
        {
            Context.Select("first");
            LegacyOwners = new LegacyPolicyOwnershipStore(NullLogger<LegacyPolicyOwnershipStore>.Instance,
                Path.Combine(_ownerRoot, "owners.json"));
            var engine = NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.StartContainerAsync) => StartAsync((string)args[0]!),
                _ => throw new InvalidOperationException(method.Name),
            });
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_RestartPolicies" => new List<RestartPolicyConfig> { Policy() },
                "get_HealthChecks" => new List<HealthCheckConfig>(),
                "get_WslcPath" => "engine",
                _ => throw new InvalidOperationException(method.Name),
            });
            var generation = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "first" };
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
                method.Name == nameof(IWslcService.GetSystemInfoAsync)
                    ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo
                        { Sessions = [++VerificationCount > 1 && ReplaceAfterFirstVerify ? generation with { ID = 2 } : generation] } })
                    : throw new InvalidOperationException(method.Name));
            Watchdog = new RestartPolicyWatchdog(engine, Monitor, settings,
                NullLogger<RestartPolicyWatchdog>.Instance, new RestartSuppressionState(), Context, LegacyOwners,
                scoped ? generation : null, scoped ? "engine" : null, scoped ? discovery : null, Clock);
        }

        public RestartPolicyConfig Policy() => new()
        {
            ContainerName = "web", Policy = RestartPolicyKind.Always, MaxRestarts = MaximumRestarts, SessionOwner = Owner,
        };

        public void ObserveStoppedContainer() => Watchdog.OnStatusChanged(Monitor, new EngineStatusSnapshot
        {
            Containers = [new ContainerInfo
            {
                Id = "container-id", Name = "web", StateValue = (int)ContainerState.Stopped,
            }],
        });

        private Task<CommandResult> StartAsync(string id)
        {
            Starts.Add(id);
            Started.TrySetResult();
            return BlockStart ? Release.Task : Task.FromResult(new CommandResult());
        }

        public void Dispose()
        {
            Watchdog.Dispose();
            if (Directory.Exists(_ownerRoot))
                Directory.Delete(_ownerRoot, recursive: true);
        }

        public sealed class ManualClock : TimeProvider
        {
            private DateTimeOffset _now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan amount) => _now += amount;
        }
    }
}
