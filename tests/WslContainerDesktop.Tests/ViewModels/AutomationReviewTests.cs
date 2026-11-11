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
using WslContainerDesktop.Tests.Services;
using WslContainerDesktop.Tray;
using WslContainerDesktop.ViewModels;
using Xunit;

namespace WslContainerDesktop.Tests.ViewModels;

[Collection("UiText")]
public sealed class AutomationReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumingSessionDoesNotNotifyBeforeItsFirstObservationAndReconciliation(bool startupOnly)
    {
        using var fixture = new Fixture("[]");
        fixture.Context.Select("alpha");
        var clients = new WslcSessionClientFactory((_, _, _) => fixture.Discovery);
        using var coordinator = new SessionAutomationCoordinatorHarness(fixture.Monitor, fixture.Settings,
            fixture.Context, clients, fixture.Discovery, new RestartSuppressionState(), fixture.Owners,
            NullLoggerFactory.Instance, fixture.ComposeFixture.Supervisor, fixture.ComposeFixture.Health,
            fixture.RemovalGuard);
        fixture.Coordinator.ObservedProvider = coordinator.IsObserved;
        fixture.Coordinator.InitializingProvider = coordinator.HasFreshObservationInProgress;
        fixture.Coordinator.ErrorProvider = () => coordinator.Error;
        coordinator.Changed += (_, _) => fixture.Coordinator.RaiseChanged();
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        var old = new WslcSessionInfo { ID = 7, CreatorPid = 100, Name = "SDK" };
        fixture.RemovalGuard.BeginStop(owner, old);
        fixture.RemovalGuard.CompleteStop(owner);
        fixture.AutostartStore.EnableTracking(owner);
        if (!startupOnly)
            fixture.Restart.Add(new RestartPolicyConfig
                { ContainerName = "web", Policy = RestartPolicyKind.Always, SessionOwner = owner });
        coordinator.Start();
        fixture.Start();
        Assert.False(fixture.Review.NeedsAttention);

        fixture.RemovalGuard.ReleaseStoppedSession(owner);
        fixture.Coordinator.RaiseChanged();
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Equal(0, fixture.NotificationCount);
        fixture.Monitor.PublishAutomation(new("engine", [
            new(old with { ID = 8 }, new([]), ObservedAt: DateTimeOffset.UtcNow)],
            SelectedSessionName: "alpha", IsPartial: true,
            SupervisedSessionNames: startupOnly ? [] : ["SDK"]));
        Assert.False(fixture.Review.NeedsAttention);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!coordinator.IsObserved(owner) && DateTime.UtcNow < deadline)
        {
            fixture.Monitor.Dispatcher.Drain();
            await Task.Delay(10);
        }
        fixture.Monitor.Dispatcher.Drain();
        Assert.True(coordinator.IsObserved(owner));
        Assert.False(fixture.RemovalGuard.IsResuming(owner));
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Equal(0, fixture.NotificationCount);

        fixture.Monitor.PublishAutomation(new("engine", [
            new(old with { ID = 8 }, new(null, Error: "unavailable"), ObservedAt: DateTimeOffset.UtcNow)],
            SelectedSessionName: "alpha", IsPartial: true,
            SupervisedSessionNames: startupOnly ? [] : ["SDK"]));
        Assert.True(fixture.Review.NeedsAttention);
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public void FreshRestartFailureEndsResumeSuppressionButStaleFailureDoesNot()
    {
        using var fixture = new Fixture("[]");
        var owner = AutomationSessionOwner.Capture("engine", new("work"));
        fixture.RemovalGuard.BeginStop(owner, new() { ID = 1, CreatorPid = 100, Name = "work" });
        fixture.RemovalGuard.CompleteStop(owner);
        fixture.AutostartStore.EnableTracking(owner);
        fixture.Start();
        var stale = DateTimeOffset.UtcNow.AddSeconds(-1);
        fixture.RemovalGuard.ReleaseStoppedSession(owner);
        fixture.RemovalGuard.CompleteResumeObservation(owner, stale);
        fixture.Coordinator.RaiseChanged();
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Equal(0, fixture.NotificationCount);
        fixture.AutostartStore.EnableTracking(AutomationSessionOwner.Capture("engine", new("other")));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("work (engine)", StringComparison.Ordinal));
        fixture.RemovalGuard.CompleteResumeObservation(owner, DateTimeOffset.UtcNow);
        fixture.Coordinator.RaiseChanged();
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("work (engine)", StringComparison.Ordinal));
    }

    [Fact]
    public void StoppedNamedSessionDoesNotNeedAttentionButUnrelatedProblemsRemainVisible()
    {
        using var fixture = new Fixture("[]");
        var stopped = AutomationSessionOwner.Capture("engine", new("work"));
        fixture.AutostartStore.EnableTracking(stopped);
        fixture.Restart.Add(new RestartPolicyConfig
            { ContainerName = "web", Policy = RestartPolicyKind.Always, SessionOwner = stopped });
        fixture.ComposeStore.Save(new ComposeProject { Name = "work-project", SessionOwner = stopped });
        fixture.RemovalGuard.BeginStop(stopped, new() { ID = 1, CreatorPid = 100, Name = "work" });
        fixture.RemovalGuard.CompleteStop(stopped);
        fixture.Start();
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Empty(fixture.Review.UnavailableTracking);
        Assert.Equal(0, fixture.NotificationCount);
        fixture.AutostartStore.EnableTracking(AutomationSessionOwner.Capture("engine", new("other")));
        Assert.True(fixture.Review.NeedsAttention);
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("work (engine)", StringComparison.Ordinal));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealCoordinatorAndAutostartDoNotNotifyWhileNewSessionInitializes()
    {
        using var fixture = new Fixture("""[{"TracksSession":true,"SessionOwner":{"ExecutablePath":"engine","SessionName":"alpha"}}]""");
        fixture.Context.Select("alpha");
        var clients = new WslcSessionClientFactory((_, _, _) => fixture.Discovery);
        using var coordinator = new SessionAutomationCoordinatorHarness(fixture.Monitor, fixture.Settings,
            fixture.Context, clients, fixture.Discovery, new RestartSuppressionState(), fixture.Owners,
            NullLoggerFactory.Instance, fixture.ComposeFixture.Supervisor, fixture.ComposeFixture.Health,
            fixture.RemovalGuard);
        fixture.Coordinator.ObservedProvider = coordinator.IsObserved;
        fixture.Coordinator.InitializingProvider = coordinator.HasFreshObservationInProgress;
        fixture.Coordinator.ErrorProvider = () => coordinator.Error;
        coordinator.Changed += (_, _) => fixture.Coordinator.RaiseChanged();
        coordinator.Start();
        fixture.Start();
        await fixture.Autostart.RestoreThenAttachAsync();
        var session = new WslcSessionInfo { ID = 7, CreatorPid = 100, Name = "SDK" };
        var container = new ContainerInfo { Id = "new-id", Name = "web", StateValue = (int)ContainerState.Running };
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));

        fixture.Monitor.PublishAutomation(new("engine", [new(session, new([container]), ObservedAt: DateTimeOffset.UtcNow)],
            SelectedSessionName: "alpha", IsPartial: true));

        Assert.False(coordinator.IsObserved(owner));
        Assert.True(coordinator.HasFreshObservationInProgress(owner));
        Assert.True(fixture.AutostartStore.Tracks(owner));
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Equal(0, fixture.NotificationCount);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!coordinator.IsObserved(owner) && DateTime.UtcNow < deadline)
        {
            fixture.Monitor.Dispatcher.Drain();
            await Task.Delay(10);
        }
        fixture.Monitor.Dispatcher.Drain();
        Assert.True(coordinator.IsObserved(owner));
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Equal(0, fixture.NotificationCount);

        fixture.Monitor.PublishAutomation(new("engine", [new(session, new(null, Error: "unavailable"))],
            SelectedSessionName: "alpha", IsPartial: true));
        Assert.True(fixture.Review.NeedsAttention);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("SDK", StringComparison.Ordinal));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public async Task TrackingNewlyObservedSessionDoesNotNotifyWhileItsWorkerInitializes()
    {
        using var fixture = new Fixture("[]");
        fixture.Start();
        await fixture.Autostart.RestoreThenAttachAsync();
        fixture.Coordinator.InitializingSession = "SDK";
        var session = new WslcSessionInfo { ID = 7, CreatorPid = 100, Name = "SDK" };
        var container = new ContainerInfo { Id = "new-id", Name = "web", StateValue = (int)ContainerState.Running };

        fixture.Monitor.PublishAutomation(new("engine", [new(session, new([container]), ObservedAt: DateTimeOffset.UtcNow)],
            SelectedSessionName: "default", IsPartial: true));

        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        Assert.True(fixture.AutostartStore.Tracks(owner));
        Assert.Contains(fixture.AutostartStore.Snapshot(), entry => entry.Id == "new-id" && entry.SessionOwner == owner);
        Assert.False(fixture.Review.NeedsAttention);
        Assert.Empty(fixture.Review.Issues);
        Assert.Equal(0, fixture.NotificationCount);

        fixture.Coordinator.InitializingSession = null;
        fixture.Coordinator.Error = "Session observation failed";
        fixture.Coordinator.RaiseChanged();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("Session observation failed", StringComparison.Ordinal));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("SDK (engine)", StringComparison.Ordinal));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public void InitializingOneTrackedSessionDoesNotHideAnotherUnavailableSession()
    {
        using var fixture = new Fixture("[]");
        var pending = AutomationSessionOwner.Capture("engine", new("SDK"));
        var unavailable = AutomationSessionOwner.Capture("engine", new("other"));
        fixture.Coordinator.InitializingSession = "SDK";
        fixture.Start();
        fixture.AutostartStore.EnableTracking(unavailable);
        var notifications = fixture.NotificationCount;

        fixture.AutostartStore.EnableTracking(pending);

        Assert.True(fixture.Review.NeedsAttention);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("SDK (engine)", StringComparison.Ordinal));
        Assert.Equal(notifications, fixture.NotificationCount);
    }

    [Fact]
    public void FreshTrackingObservationDoesNotHideWaitingHealthPolicies()
    {
        using var fixture = new Fixture("[]");
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Coordinator.InitializingSession = "SDK";
        fixture.Health.Add(new HealthCheckConfig { ContainerName = "web", Command = "probe", SessionOwner = owner });
        fixture.AutostartStore.EnableTracking(owner);
        fixture.Start();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.Contains(fixture.Review.Issues, issue =>
            issue.Contains("Health and restart actions for SDK", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("Automatic-start tracking for SDK", StringComparison.Ordinal));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public void ExpectedRemovalWarningsAreSuppressedWithoutHidingOtherProblems()
    {
        using var fixture = new Fixture();
        var removing = AutomationSessionOwner.Capture("engine", new("work"));
        var other = AutomationSessionOwner.Capture("engine", new("other"));
        fixture.Context.Select("work");
        fixture.AutostartStore.EnableTracking(removing);
        fixture.AutostartStore.EnableTracking(other);
        fixture.Restart.Add(new RestartPolicyConfig { ContainerName = "redis", Policy = RestartPolicyKind.Always, SessionOwner = removing });
        fixture.Restart.Add(new RestartPolicyConfig { ContainerName = "redis", Policy = RestartPolicyKind.Always, SessionOwner = other });
        fixture.ComposeStore.Save(new ComposeProject { Name = "work-project", SessionOwner = removing });
        fixture.ComposeStore.Save(new ComposeProject { Name = "other-project", SessionOwner = other });
        fixture.Start();
        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Healthy });
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("selected session", StringComparison.OrdinalIgnoreCase));

        Assert.True(fixture.Removal.TryBegin("work"));
        fixture.RemovalGuard.ResumeRemoval(removing, null);
        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Down });
        fixture.Coordinator.RaiseChanged();

        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("selected session", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("work (engine)", StringComparison.Ordinal));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Review.UnavailableTracking, issue => issue.Owner == removing);
        Assert.Contains(fixture.Review.UnavailableTracking, issue => issue.Owner == other);

        fixture.RemovalGuard.EndRemovalAttempt(removing);
        fixture.Removal.Complete("disk error");
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("selected session", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovalSuppressesOnlyItsOwnUnavailableAutostartProblem()
    {
        using var fixture = new Fixture { SessionAvailable = false };
        var removing = AutomationSessionOwner.Capture("engine", new("work"));
        var other = AutomationSessionOwner.Capture("engine", new("other"));
        foreach (var owner in new[] { removing, other })
        {
            fixture.AutostartStore.EnableTracking(owner);
            fixture.AutostartStore.ReplaceOwned(owner, [new AutostartEntry { Id = "redis", Name = "redis", SessionOwner = owner }]);
        }
        fixture.Start();
        Assert.True(fixture.Removal.TryBegin("work"));
        fixture.RemovalGuard.ResumeRemoval(removing, null);
        await fixture.Autostart.RestoreThenAttachAsync();
        fixture.Coordinator.RaiseChanged();

        Assert.DoesNotContain(fixture.Review.Issues, issue =>
            issue.Contains("Automatic start for work", StringComparison.Ordinal));
        Assert.Contains(fixture.Review.Issues, issue =>
            issue.Contains("Automatic start for other", StringComparison.Ordinal));

        fixture.RemovalGuard.EndRemovalAttempt(removing);
        fixture.Removal.Complete("disk error");
        Assert.Contains(fixture.Review.Issues, issue =>
            issue.Contains("Automatic start for work", StringComparison.Ordinal));
    }

    [Fact]
    public void WarningThatPredatesRemovalRemainsVisible()
    {
        using var fixture = new Fixture();
        var owner = AutomationSessionOwner.Capture("engine", new("work"));
        fixture.AutostartStore.EnableTracking(owner);
        fixture.Start();
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("work (engine)", StringComparison.Ordinal));
        Assert.True(fixture.Removal.TryBegin("work"));
        fixture.RemovalGuard.ResumeRemoval(owner, null);
        fixture.Coordinator.RaiseChanged();
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("work (engine)", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpectedDefaultStopDoesNotHideAnUnrelatedSessionWarning()
    {
        using var fixture = new Fixture("[]");
        var owner = AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default);
        var other = AutomationSessionOwner.Capture("engine", new("other"));
        fixture.AutostartStore.EnableTracking(owner);
        fixture.Start();
        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Healthy });
        Assert.True(fixture.Stop.TryBegin("Default CLI session", defaultSession: true));
        fixture.Context.PauseDefaultSession("engine");
        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Down });
        fixture.AutostartStore.EnableTracking(other);

        Assert.DoesNotContain(fixture.Review.Issues, issue =>
            issue.Contains("selected session", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
        Assert.False(fixture.Review.CanEdit);
        fixture.Stop.Complete();
        Assert.True(fixture.Review.CanEdit);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("other (engine)", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultWarningThatPredatesStopRemainsVisible()
    {
        using var fixture = new Fixture("[]");
        fixture.AutostartStore.EnableTracking(AutomationSessionOwner.Capture("engine", WslcSessionTarget.Default));
        fixture.Start();
        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Down });
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("selected session", StringComparison.OrdinalIgnoreCase));
        Assert.True(fixture.Stop.TryBegin("Default CLI session", defaultSession: true));
        fixture.Context.PauseDefaultSession("engine");

        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("selected session", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ForgettingPreviouslyRemovedSessionClearsItsTrackingAttention()
    {
        using var fixture = new Fixture("""[{"TracksSession":true,"SessionOwner":{"ExecutablePath":"engine","SessionName":"test-session"}}]""");
        fixture.Start();
        Assert.True(fixture.Review.NeedsAttention);
        var item = Assert.Single(fixture.Review.UnavailableTracking);
        Assert.Equal("test-session", item.Owner.SessionName);

        await fixture.Review.ForgetTrackingAsync(item);

        Assert.False(fixture.Review.NeedsAttention);
        Assert.Empty(fixture.Review.Issues);
        Assert.Empty(fixture.ReloadAutostart().Snapshot());
    }

    [Fact]
    public async Task AssignedHistoryWithoutTrackingMarkerCanBeForgottenAfterAnUnavailableWarning()
    {
        using var fixture = new Fixture("""[{"Id":"old","Name":"web","SessionOwner":{"ExecutablePath":"engine","SessionName":"gone"}}]""")
        {
            SessionAvailable = false,
        };
        fixture.Start();
        await fixture.Autostart.RestoreThenAttachAsync();
        var item = Assert.Single(fixture.Review.UnavailableTracking);
        Assert.Equal("gone", item.Owner.SessionName);

        await fixture.Review.ForgetTrackingAsync(item);

        Assert.Empty(fixture.ReloadAutostart().Snapshot());
        Assert.Empty(fixture.Autostart.Problems);
    }

    [Fact]
    public void TrackingActionsAreDisabledWhileSessionRemovalIsInProgress()
    {
        using var fixture = new Fixture();
        fixture.Start();
        Assert.True(fixture.Review.CanEdit);
        Assert.True(fixture.Removal.TryBegin("work"));
        Assert.False(fixture.Review.CanEdit);
        fixture.Removal.Complete();
        Assert.True(fixture.Review.CanEdit);
    }

    [Fact]
    public async Task ForgetTrackingRequiresConfirmationAndPreservesOtherWarningsAndConfiguration()
    {
        using var fixture = new Fixture();
        var removed = AutomationSessionOwner.Capture("engine", new("deleted"));
        var waiting = AutomationSessionOwner.Capture("engine", new("waiting"));
        fixture.AutostartStore.EnableTracking(removed);
        fixture.AutostartStore.ReplaceOwned(removed, [new AutostartEntry { Id = "gone", Name = "web", SessionOwner = removed }]);
        fixture.AutostartStore.EnableTracking(waiting);
        fixture.Restart.Add(new RestartPolicyConfig { ContainerName = "web", Policy = RestartPolicyKind.Always, SessionOwner = removed });
        fixture.Start();
        var item = fixture.Review.UnavailableTracking.Single(row => row.Owner == removed);
        fixture.Dialogs.ConfirmResult = false;

        await fixture.Review.ForgetTrackingAsync(item);
        Assert.True(fixture.AutostartStore.Tracks(removed));
        fixture.Dialogs.ConfirmResult = true;
        await fixture.Review.ForgetTrackingAsync(item);

        Assert.False(fixture.ReloadAutostart().Tracks(removed));
        Assert.DoesNotContain(fixture.ReloadAutostart().Snapshot(), entry => entry.Id == "gone");
        Assert.True(fixture.ReloadAutostart().Tracks(waiting));
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("waiting", StringComparison.Ordinal));
        Assert.Single(fixture.Restart);
        Assert.True(fixture.Review.NeedsAttention);
    }

    [Fact]
    public async Task LegacyActionsRemainPausedAndTriggerOneNotification()
    {
        using var fixture = new Fixture();
        fixture.Health.Add(new() { ContainerName = "web", Command = "probe" });
        fixture.Restart.Add(new() { ContainerName = "db", Policy = RestartPolicyKind.Always });
        fixture.Start();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.Equal(3, fixture.Review.PendingAssignments.Count);
        Assert.Equal(1, fixture.NotificationCount);
        Assert.Empty(fixture.Starts);

        await fixture.Review.AssignAsync(fixture.Review.PendingAssignments[0], null);

        Assert.Equal(3, fixture.Review.PendingAssignments.Count);
        Assert.Empty(fixture.Starts);
        Assert.Contains("Choose a session", fixture.Dialogs.LastMessage);
    }

    [Fact]
    public async Task HealthAssignmentPersistsAndShowsWhenItsSessionIsUnavailable()
    {
        using var fixture = new Fixture();
        var health = new HealthCheckConfig { ContainerName = "web", Command = "probe" };
        fixture.Health.Add(health);
        fixture.Start();
        var item = fixture.Review.PendingAssignments.Single(entry => entry.Name == "web");

        await fixture.Review.AssignAsync(item, new WslcSessionChoice("SDK", "SDK"));

        Assert.Equal("SDK", fixture.Owners.OwnerFor(health)!.SessionName);
        Assert.Equal("SDK", fixture.ReloadOwners().OwnerFor(health)!.SessionName);
        Assert.DoesNotContain(fixture.Review.PendingAssignments, entry => entry.Name == "web");
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        fixture.Coordinator.ObservedSession = "SDK";
        fixture.Coordinator.RaiseChanged();
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("Health and restart actions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AssigningAutomaticStartPersistsAndUsesOnlyTheChosenSession()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var item = Assert.Single(fixture.Review.PendingAssignments);

        await fixture.Review.AssignAsync(item, new WslcSessionChoice("SDK", "SDK"));

        Assert.Equal(["SDK:container-id"], fixture.Starts);
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        Assert.True(fixture.ReloadAutostart().Tracks(owner));
        Assert.Equal(owner, fixture.ReloadAutostart().Snapshot().Single(entry => !entry.TracksSession).SessionOwner);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("tracking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LegacyComposeProjectRemainsPausedUntilItsOwnerIsSaved()
    {
        using var fixture = new Fixture();
        fixture.ComposeStore.Save(new ComposeProject { Name = "legacy-project" });
        fixture.Start();
        var item = fixture.Review.PendingAssignments.Single(entry => entry.Name == "legacy-project");

        await fixture.Review.AssignAsync(item, new WslcSessionChoice("SDK", "SDK"));

        var restored = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, fixture.Root);
        Assert.Equal("SDK", restored.Get("legacy-project")!.SessionOwner!.SessionName);
        Assert.DoesNotContain(fixture.Review.PendingAssignments, entry => entry.Name == "legacy-project");
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("Compose recovery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingNamedSessionDoesNotSaveOrStartAnything()
    {
        using var fixture = new Fixture { SessionAvailable = false };
        fixture.Start();
        var item = Assert.Single(fixture.Review.PendingAssignments);

        await fixture.Review.AssignAsync(item, new WslcSessionChoice("SDK", "SDK"));

        Assert.Empty(fixture.Starts);
        Assert.Null(Assert.Single(fixture.AutostartStore.Snapshot()).SessionOwner);
        Assert.Contains("still paused", fixture.Dialogs.LastMessage);
        Assert.True(fixture.Review.NeedsAttention);
    }

    [Fact]
    public void DisabledNotificationsStillLeavePersistentAttention()
    {
        using var fixture = new Fixture { NotificationsEnabled = false };
        fixture.Start();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.True(fixture.Review.NotificationsDisabled);
        Assert.Single(fixture.Review.PendingAssignments);
    }

    [Fact]
    public void WindowsNotificationFailureStillLeavesPersistentAttention()
    {
        using var fixture = new Fixture { WindowsNotificationsAvailable = false };
        fixture.Start();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.True(fixture.Review.NotificationsDisabled);
        Assert.Single(fixture.Review.PendingAssignments);
    }

    [Fact]
    public void EnablingNotificationsDeliversPreviouslyUnsentAttentionOnce()
    {
        using var fixture = new Fixture { NotificationsEnabled = false };
        fixture.Start();
        Assert.Equal(0, fixture.NotificationCount);

        fixture.NotificationsEnabled = true;
        fixture.PublishSettingsChanged();
        fixture.PublishSettingsChanged();

        Assert.Equal(1, fixture.NotificationCount);
        Assert.False(fixture.Review.NotificationsDisabled);
        Assert.True(fixture.Review.NeedsAttention);
    }

    [Fact]
    public void SettingsSaveFailureShowsPersistentWarningAndNotifies()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var originalNotifications = fixture.NotificationCount;

        fixture.SettingsSaveError = true;
        fixture.PublishSettingsChanged();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("could not be saved", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(originalNotifications + 1, fixture.NotificationCount);
        fixture.SettingsSaveError = false;
        fixture.PublishSettingsChanged();
        Assert.DoesNotContain(fixture.Review.Issues, issue => issue.Contains("could not be saved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnreadableSettingsShowAnActionablePersistentWarning()
    {
        using var fixture = new Fixture { SettingsLoadError = true };
        fixture.Start();

        Assert.True(fixture.Review.NeedsAttention);
        Assert.Contains(fixture.Review.Issues, issue => issue.Contains("settings are unreadable or incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public void RoutineStatusUpdatesDoNotReplacePendingRowsOrRepeatNotifications()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var item = Assert.Single(fixture.Review.PendingAssignments);

        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Healthy });
        fixture.Monitor.Publish(new EngineStatusSnapshot { Health = EngineHealth.Healthy });

        Assert.Same(item, Assert.Single(fixture.Review.PendingAssignments));
        Assert.Equal(1, fixture.NotificationCount);
    }

    [Fact]
    public void LanguageSwitchRefreshesLabelsWithoutReplacingPendingRows()
    {
        UiText.Configure((key, _) => key == "Automation_KindAutostart"
            ? UiText.LanguageTag == "zh-Hans" ? "自动启动" : "Automatic start"
            : key);
        try
        {
            UiText.SetLanguage("en-US");
            using var fixture = new Fixture();
            fixture.Start();
            var item = Assert.Single(fixture.Review.PendingAssignments);
            Assert.Equal("Automatic start", item.KindText);

            UiText.SetLanguage("zh-Hans");

            Assert.Same(item, Assert.Single(fixture.Review.PendingAssignments));
            Assert.Equal("自动启动", item.KindText);
        }
        finally
        {
            UiText.Configure(null);
            UiText.SetLanguage("en-US");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wcd-review-tests-" + Guid.NewGuid().ToString("N"));
        public string Root => _root;
        private EventHandler? _settingsChanged;
        public List<HealthCheckConfig> Health { get; } = [];
        public List<RestartPolicyConfig> Restart { get; } = [];
        public WslcSessionContext Context { get; } = new();
        public DialogService Dialogs { get; } = new();
        public LegacyPolicyOwnershipStore Owners { get; }
        public AutostartStateStore AutostartStore { get; }
        public ContainerAutostartService Autostart { get; }
        public AutomationReviewViewModel Review { get; }
        public ComposeProjectStore ComposeStore { get; }
        public List<string> Starts { get; } = [];
        public int NotificationCount { get; private set; }
        public bool NotificationsEnabled { get; set; } = true;
        public bool WindowsNotificationsAvailable { get; set; } = true;
        public bool SessionAvailable { get; set; } = true;
        public bool SettingsSaveError { get; set; }
        public bool SettingsLoadError { get; set; }
        public StatusMonitor Monitor { get; } = new();
        public SessionAutomationCoordinator Coordinator { get; } = new();
        public SessionRemovalProgress Removal { get; } = new();
        public SessionStopProgress Stop { get; } = new();
        public SessionRemovalGuard RemovalGuard { get; }
        public ISettingsService Settings { get; }
        public IWslcService Discovery { get; }
        public ComposeNetworkSupervisorTests.Fixture ComposeFixture { get; }

        public Fixture(string? initialAutostart = null)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "autostart.json"), initialAutostart ?? """[{"Id":"container-id","Name":"saved"}]""");
            Dialogs.DismissMessage.SetResult();
            Owners = ReloadOwners();
            AutostartStore = ReloadAutostart();
            RemovalGuard = new SessionRemovalGuard(new ManagedSessionStore(NullLogger.Instance, Path.Combine(_root, "managed")));
            ComposeStore = new ComposeProjectStore(NullLogger<ComposeProjectStore>.Instance, _root);
            var settings = NetworkTestProxy.Create<ISettingsService>((method, args) => method.Name switch
            {
                "get_HealthChecks" => Health,
                "get_RestartPolicies" => Restart,
                "get_WslcPath" => "engine",
                "get_RestartRunningContainersOnLaunch" => true,
                "get_NotificationsEnabled" => NotificationsEnabled,
                "get_HasSaveError" => SettingsSaveError,
                "get_HasLoadError" => SettingsLoadError,
                "add_Changed" => AddChanged((EventHandler)args[0]!),
                "remove_Changed" => RemoveChanged((EventHandler)args[0]!),
                _ => throw new InvalidOperationException(method.Name),
            });
            Settings = settings;
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) => method.Name switch
            {
                nameof(IWslcService.GetSystemInfoAsync) => Task.FromResult(new WslcSystemInfo
                {
                    Server = new WslcServerInfo { Sessions = Sessions() },
                }),
                _ => throw new InvalidOperationException(method.Name),
            });
            Discovery = discovery;
            var reader = new WslcSessionInventoryReader(discovery, (_, _) => discovery, () => "engine",
                NullLogger<WslcSessionInventoryReader>.Instance);
            Autostart = new ContainerAutostartService(discovery, Monitor, settings,
                NullLogger<ContainerAutostartService>.Instance, Context, AutostartStore, CreateClient,
                _ => Task.FromResult(Sessions()), (_, _, _) => Task.CompletedTask);
            var notifications = NetworkTestProxy.Create<INotificationService>((method, _) => method.Name switch
            {
                nameof(INotificationService.NotifyAutomationActionRequired) => RecordNotification(),
                "get_CanShowNotifications" => NotificationsEnabled && WindowsNotificationsAvailable,
                _ => throw new InvalidOperationException(method.Name),
            });
            ComposeFixture = new ComposeNetworkSupervisorTests.Fixture(storeOverride: ComposeStore, sessionContext: Context);
            var compose = ComposeFixture.Supervisor;
            Review = new AutomationReviewViewModel(settings, Owners, AutostartStore, Autostart,
                reader, Context, notifications, Dialogs, Monitor, NullLogger<AutomationReviewViewModel>.Instance,
                ComposeStore, compose, Coordinator, Removal, RemovalGuard, Stop);
        }

        public void Start() => Review.Start();
        public void PublishSettingsChanged() => _settingsChanged?.Invoke(this, EventArgs.Empty);
        public LegacyPolicyOwnershipStore ReloadOwners() =>
            new(NullLogger<LegacyPolicyOwnershipStore>.Instance, Path.Combine(_root, "owners.json"));
        public AutostartStateStore ReloadAutostart() =>
            new(NullLogger<AutostartStateStore>.Instance, Path.Combine(_root, "autostart.json"));
        private IReadOnlyList<WslcSessionInfo> Sessions() => SessionAvailable
            ? [new WslcSessionInfo { ID = 7, CreatorPid = 100, Name = "SDK" }] : [];
        private object? AddChanged(EventHandler handler) { _settingsChanged += handler; return null; }
        private object? RemoveChanged(EventHandler handler) { _settingsChanged -= handler; return null; }
        private object? RecordNotification() { NotificationCount++; return null; }

        private IWslcService CreateClient(AutomationSessionOwner owner) =>
            NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.IsEngineAvailableAsync) => Task.FromResult(true),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    [new ContainerInfo { Id = "container-id", Name = "saved", StateValue = (int)ContainerState.Stopped }]),
                nameof(IWslcService.StartContainerAsync) => StartAsync(owner, (string)args[0]!),
                _ => throw new InvalidOperationException(method.Name),
            });

        private Task<CommandResult> StartAsync(AutomationSessionOwner owner, string id)
        {
            Starts.Add($"{owner.SessionName ?? "default"}:{id}");
            return Task.FromResult(new CommandResult());
        }

        public void Dispose()
        {
            Review.Dispose();
            Autostart.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
