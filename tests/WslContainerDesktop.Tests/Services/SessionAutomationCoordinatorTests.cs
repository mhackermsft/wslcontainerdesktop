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

#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class SessionAutomationCoordinatorTests
{
    [Fact]
    public void StartupHistoryObservationDoesNotCreateWatchdogsOrComposeScopes()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: ["beta"], SupervisedSessionNames: []));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta) with { IsPartial = true });

        Assert.Equal(0, fixture.CreatedClients);
        Assert.True(fixture.Observed("beta"));
        Assert.False(fixture.Coordinator.HasFreshObservationInProgress(AutomationSessionOwner.Capture("engine", new("beta"))));
        Assert.Null(fixture.Coordinator.GetHealth(fixture.Beta, "engine"));
        Assert.Null(fixture.Coordinator.GetContainers(AutomationSessionOwner.Capture("engine", new("beta"))));
        Assert.False(fixture.Observed("Beta"));
        Assert.Null(fixture.Coordinator.Error);
    }

    [Fact]
    public async Task RemovingSupervisionDemandDisposesWorkerButRetainsStartupAvailability()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: ["beta"], SupervisedSessionNames: ["beta"]));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta) with { IsPartial = true });
        await fixture.WaitUntilAsync(() => fixture.Observed("beta"));
        Assert.Equal(1, fixture.CreatedClients);
        Assert.NotNull(fixture.Coordinator.GetContainers(AutomationSessionOwner.Capture("engine", new("beta"))));

        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: ["beta"], SupervisedSessionNames: []));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta) with { IsPartial = true });

        Assert.Equal(1, fixture.CreatedClients);
        Assert.True(fixture.Observed("beta"));
        Assert.Null(fixture.Coordinator.GetContainers(AutomationSessionOwner.Capture("engine", new("beta"))));
        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: [], SupervisedSessionNames: []));
        Assert.False(fixture.Observed("beta"));
    }

    [Fact]
    public void FailedOrExpiredStartupHistoryDoesNotAppearAvailable()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: ["beta"], SupervisedSessionNames: []));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta) with { IsPartial = true });
        Assert.True(fixture.Observed("beta"));
        fixture.Monitor.PublishAutomation(new("engine", [new(fixture.Beta, new(null, TimedOut: true))],
            SelectedSessionName: "alpha", IsPartial: true));
        Assert.False(fixture.Observed("beta"));
        Assert.Contains("beta", fixture.Coordinator.Error);
        fixture.Monitor.PublishAutomation(new("engine", [new(fixture.Beta, new(Array.Empty<ContainerInfo>()),
            ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-2))], SelectedSessionName: "alpha", IsPartial: true));
        Assert.False(fixture.Observed("beta"));
        Assert.Equal(0, fixture.CreatedClients);
    }

    [Fact]
    public async Task StaleSelectionHeaderCannotDisposeCurrentWorkers()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.Publish(Selected(fixture.Alpha));
        await fixture.WaitUntilAsync(() => fixture.Observed("alpha"));
        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "beta",
            IsPartial: true, ActiveSessionNames: [], SupervisedSessionNames: []));
        Assert.True(fixture.Observed("alpha"));
        Assert.Equal(1, fixture.CreatedClients);
    }

    [Fact]
    public async Task FreshObservationIsPendingUntilWorkerInitializationCompletes()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        var owner = AutomationSessionOwner.Capture("engine", new("beta"));

        Assert.False(fixture.Coordinator.IsObserved(owner));
        Assert.True(fixture.Coordinator.HasFreshObservationInProgress(owner));
        Assert.False(fixture.Coordinator.HasFreshObservationInProgress(
            AutomationSessionOwner.Capture("other-engine", new("beta"))));
        Assert.False(fixture.Coordinator.HasFreshObservationInProgress(
            AutomationSessionOwner.Capture("engine", new("Beta"))));

        await fixture.WaitUntilAsync(() => fixture.Coordinator.IsObserved(owner));
        Assert.False(fixture.Coordinator.HasFreshObservationInProgress(owner));
    }

    [Fact]
    public void FailedObservationStopsPendingTrackingAvailabilityImmediately()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        var owner = AutomationSessionOwner.Capture("engine", new("beta"));
        Assert.True(fixture.Coordinator.HasFreshObservationInProgress(owner));

        fixture.Monitor.PublishAutomation(new("engine",
            [new(fixture.Beta, new(null, Error: "unavailable"))],
            SelectedSessionName: "alpha", IsPartial: true));

        Assert.False(fixture.Coordinator.HasFreshObservationInProgress(owner));
        Assert.Contains("beta", fixture.Coordinator.Error);
    }

    [Fact]
    public void ExpiredObservationDoesNotCountAsPendingAvailability()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.PublishAutomation(new("engine",
            [new(fixture.Beta, new(Array.Empty<ContainerInfo>()), ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-2))],
            SelectedSessionName: "alpha"));
        var owner = AutomationSessionOwner.Capture("engine", new("beta"));

        Assert.False(fixture.Coordinator.HasFreshObservationInProgress(owner));
        Assert.False(fixture.Coordinator.IsObserved(owner));
    }

    [Fact]
    public async Task RemovalObservationFailureDoesNotHideAnUnrelatedSessionFailure()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.Publish(Selected(fixture.Alpha));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        await fixture.WaitUntilAsync(() => fixture.Observed("beta"));
        var owner = AutomationSessionOwner.Capture("engine", new("beta"));
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "beta");
        fixture.ManagedSessions.Add(definition);
        fixture.ManagedSessions.MarkForDeletion(definition, owner);
        fixture.RemovalGuard.ResumeRemoval(owner, fixture.Beta);
        fixture.Coordinator.SuspendForRemoval(owner);
        var gamma = new WslcSessionInfo { ID = 3, CreatorPid = 300, Name = "gamma" };
        fixture.Monitor.PublishAutomation(new("engine",
            [new(fixture.Beta, new(null, Error: "stopping")), new(gamma, new(null, Error: "unavailable"))],
            SelectedSessionName: "alpha", IsPartial: true));

        Assert.DoesNotContain("beta", fixture.Coordinator.Error);
        Assert.Contains("gamma", fixture.Coordinator.Error);
        fixture.RemovalGuard.EndRemovalAttempt(owner);
        fixture.Monitor.PublishAutomation(new("engine", [new(fixture.Beta, new(null, Error: "unavailable"))],
            SelectedSessionName: "alpha", IsPartial: true));
        Assert.Contains("beta", fixture.Coordinator.Error);
    }

    [Fact]
    public void StoppedDefaultObservationDoesNotHideUnrelatedOrLaterFailures()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
        var stopped = new WslcSessionInfo { ID = 9, CreatorPid = 90, Name = defaultName };
        var owner = AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default);
        fixture.RemovalGuard.BeginStop(owner, stopped);
        var unrelated = new WslcSessionInfo { ID = 3, CreatorPid = 300, Name = "gamma" };
        fixture.Monitor.PublishAutomation(new("engine",
            [new(stopped, new(null, Error: "stopping")), new(unrelated, new(null, Error: "unavailable"))],
            SelectedSessionName: "alpha", IsPartial: true));

        Assert.DoesNotContain(defaultName, fixture.Coordinator.Error);
        Assert.Contains("gamma", fixture.Coordinator.Error);

        fixture.RemovalGuard.ReleaseStoppedSession(owner);
        fixture.Monitor.PublishAutomation(new("engine",
            [new(stopped, new(null, Error: "unavailable"), ObservedAt: DateTimeOffset.UtcNow.AddSeconds(1))],
            SelectedSessionName: "alpha", IsPartial: true));
        Assert.Contains(defaultName, fixture.Coordinator.Error);
    }

    [Fact]
    public async Task RemovalFencesLateObservationsWithoutPausingAnotherSession()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.Publish(Selected(fixture.Alpha));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        await fixture.WaitUntilAsync(() => fixture.Observed("alpha") && fixture.Observed("beta"));
        var owner = AutomationSessionOwner.Capture("engine", new("beta"));
        fixture.RemovalGuard.ResumeRemoval(owner, fixture.Beta);
        fixture.Coordinator.SuspendForRemoval(owner);
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        Assert.False(fixture.Observed("beta"));
        Assert.True(fixture.Observed("alpha"));
        fixture.RemovalGuard.Complete(owner, fixture.Beta);
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        Assert.False(fixture.Observed("beta"));
        Assert.True(fixture.Observed("alpha"));
    }

    [Fact]
    public async Task SelectionChangesAndTemporaryObservationFailuresKeepSessionWorkers()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.Publish(Selected(fixture.Alpha));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        await fixture.WaitUntilAsync(() => fixture.Observed("alpha") && fixture.Observed("beta"));
        Assert.Equal(2, fixture.CreatedClients);

        fixture.Context.Select("beta");
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        Assert.True(fixture.Observed("alpha"));
        Assert.True(fixture.Observed("beta"));
        fixture.Monitor.Publish(Selected(fixture.Beta));
        fixture.Monitor.PublishAutomation(Batch("beta", fixture.Alpha));
        Assert.Equal(2, fixture.CreatedClients);

        fixture.Monitor.PublishAutomation(new("engine",
            [new(fixture.Alpha, new(null, Error: "unavailable"))], SelectedSessionName: "beta"));
        Assert.False(fixture.Observed("alpha"));
        Assert.True(fixture.Observed("beta"));
        Assert.NotNull(fixture.Coordinator.Error);

        fixture.Monitor.PublishAutomation(Batch("beta", fixture.Alpha));
        await fixture.WaitUntilAsync(() => fixture.Observed("alpha"));
        Assert.True(fixture.Observed("beta"));
        Assert.Equal(2, fixture.CreatedClients);
    }

    [Fact]
    public async Task PartialObservationsKeepOtherWorkersAndReportOnlyFailedSession()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.Publish(Selected(fixture.Alpha));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        await fixture.WaitUntilAsync(() => fixture.Observed("alpha") && fixture.Observed("beta"));

        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: ["beta", "gamma"]));
        Assert.True(fixture.Observed("beta"));
        var gamma = new WslcSessionInfo { ID = 3, CreatorPid = 300, Name = "gamma" };
        fixture.Monitor.PublishAutomation(new("engine", [new(gamma, new(null, TimedOut: true))],
            SelectedSessionName: "alpha", IsPartial: true));

        Assert.True(fixture.Observed("beta"));
        Assert.Contains("gamma", fixture.Coordinator.Error);
        fixture.Monitor.PublishAutomation(new("engine", [new(fixture.Beta, new(Array.Empty<ContainerInfo>()))],
            SelectedSessionName: "alpha", IsPartial: true));
        Assert.Contains("gamma", fixture.Coordinator.Error);

        fixture.Monitor.PublishAutomation(new("engine", [], SelectedSessionName: "alpha",
            IsPartial: true, ActiveSessionNames: ["beta"]));
        Assert.Null(fixture.Coordinator.Error);
        Assert.True(fixture.Observed("beta"));
    }

    [Fact]
    public async Task ExpiredPartialObservationPausesRatherThanRefreshingWorker()
    {
        using var fixture = new Fixture();
        fixture.Context.Select("alpha");
        fixture.Coordinator.Start();
        fixture.Monitor.Publish(Selected(fixture.Alpha));
        fixture.Monitor.PublishAutomation(Batch("alpha", fixture.Beta));
        await fixture.WaitUntilAsync(() => fixture.Observed("beta"));

        fixture.Monitor.PublishAutomation(new("engine",
            [new(fixture.Beta, new(Array.Empty<ContainerInfo>()), ObservedAt: DateTimeOffset.UtcNow.AddMinutes(-2))],
            SelectedSessionName: "alpha", IsPartial: true));

        Assert.False(fixture.Observed("beta"));
        Assert.Contains("beta", fixture.Coordinator.Error);
    }

    private static EngineStatusSnapshot Selected(WslcSessionInfo session) => new()
    {
        Health = EngineHealth.Healthy,
        Session = session,
        ExecutablePath = "engine",
        Containers = [],
    };

    private static WslcAutomationInventory Batch(string selected, WslcSessionInfo session) =>
        new("engine", [new(session, new(Array.Empty<ContainerInfo>()))], SelectedSessionName: selected);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wcd-session-coordinator-" + Guid.NewGuid().ToString("N"));
        public WslcSessionInfo Alpha { get; } = new() { ID = 1, CreatorPid = 100, Name = "alpha" };
        public WslcSessionInfo Beta { get; } = new() { ID = 2, CreatorPid = 200, Name = "beta" };
        public WslcSessionContext Context { get; } = new();
        public StatusMonitor Monitor { get; } = new();
        public SessionAutomationCoordinatorHarness Coordinator { get; }
        public SessionRemovalGuard RemovalGuard { get; }
        public ManagedSessionStore ManagedSessions { get; }
        public int CreatedClients { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_WslcPath" => "engine",
                "get_HasLoadError" => false,
                "get_HealthChecks" => new List<HealthCheckConfig>(),
                "get_RestartPolicies" => new List<RestartPolicyConfig>(),
                _ => throw new InvalidOperationException(method.Name),
            });
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
                method.Name == nameof(IWslcService.GetSystemInfoAsync)
                    ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = [Alpha, Beta] } })
                    : throw new InvalidOperationException(method.Name));
            var clients = new WslcSessionClientFactory((_, _, _) =>
            {
                CreatedClients++;
                return NetworkTestProxy.Create<IWslcService>((method, _) => throw new InvalidOperationException(method.Name));
            });
            var owners = new LegacyPolicyOwnershipStore(NullLogger<LegacyPolicyOwnershipStore>.Instance,
                Path.Combine(_root, "owners.json"));
            ManagedSessions = new ManagedSessionStore(NullLogger.Instance, Path.Combine(_root, "managed"));
            RemovalGuard = new SessionRemovalGuard(ManagedSessions);
            var compose = new ComposeNetworkSupervisorTests.Fixture();
            Coordinator = new SessionAutomationCoordinatorHarness(Monitor, settings, Context, clients, discovery,
                new RestartSuppressionState(), owners, NullLoggerFactory.Instance, compose.Supervisor, compose.Health, RemovalGuard);
        }

        public bool Observed(string name) =>
            Coordinator.IsObserved(AutomationSessionOwner.Capture("engine", new(name)));

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Monitor.Dispatcher.Drain();
                await Task.Delay(10);
            }
            Monitor.Dispatcher.Drain();
            Assert.True(condition());
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
#endif
