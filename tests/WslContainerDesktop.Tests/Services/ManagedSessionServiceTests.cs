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
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ManagedSessionServiceTests
{
    [Fact]
    public async Task StoppingSavedSessionFencesOldObservationsUntilItStartsAgain()
    {
        using var fixture = new Fixture();
        var definition = await fixture.Service.CreateAsync("work");
        var generation = Assert.Single(fixture.Snapshot());
        var owner = AutomationSessionOwner.Capture(@"C:\wslc.exe", new("work"));
        await fixture.Service.StopAsync(definition);
        Assert.True(fixture.RemovalGuard.IsIntentionallyStopped(owner));
        Assert.True(fixture.Service.IsIntentionallyStopped("work"));
        Assert.True(fixture.RemovalGuard.IsBlocked(owner, generation, DateTimeOffset.UtcNow));
        await fixture.Service.StartSavedAsync(definition);
        Assert.False(fixture.RemovalGuard.IsIntentionallyStopped(owner));
        Assert.False(fixture.RemovalGuard.IsBlocked(owner, Assert.Single(fixture.Snapshot()), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SlowStorageDeletionYieldsAndDuplicateRemovalCannotRunTwice()
    {
        using var fixture = new Fixture { BlockDeletion = true };
        var definition = await fixture.Service.CreateAsync("work");
        var removed = new List<string>();
        fixture.Service.SessionRemoved += (name, _, _) => removed.Add(name);
        var first = fixture.Service.RemoveAsync(definition, deleteData: true);
        await fixture.DeletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(first.IsCompleted);
            Assert.True(fixture.Service.IsRemoving(definition));
            Assert.False(fixture.Service.HasProblems);
            Assert.Empty(removed);
            var duplicate = fixture.Service.RemoveAsync(definition, deleteData: true);
            Assert.False(duplicate.IsCompleted);
            fixture.ReleaseDeletion.Set();
            await first;
            await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate);
            Assert.Equal(1, fixture.DeletionCount);
            Assert.Equal(["work"], removed);
        }
        finally { fixture.ReleaseDeletion.Set(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovalForgetsOnlyOwnedAutomaticStartHistoryAndRejectsLateObservations(bool deleteData)
    {
        using var fixture = new Fixture();
        var definition = await fixture.Service.CreateAsync("work");
        var owner = AutomationSessionOwner.Capture(@"C:\wslc.exe", new("work"));
        var other = AutomationSessionOwner.Capture(@"C:\other\wslc.exe", new("work"));
        fixture.AutostartStore.EnableTracking(owner);
        fixture.AutostartStore.ReplaceOwned(owner, [new AutostartEntry { Id = "one", Name = "web", SessionOwner = owner }]);
        fixture.AutostartStore.EnableTracking(other);
        fixture.AutostartStore.ReplaceOwned(other, [new AutostartEntry { Id = "two", Name = "web", SessionOwner = other }]);
        var generation = Assert.Single(fixture.Snapshot());
        var captured = DateTimeOffset.UtcNow;
        var path = fixture.Store.StoragePath(definition);
        File.WriteAllText(Path.Combine(path, "data"), "kept");

        await fixture.Service.RemoveAsync(definition, deleteData);
        fixture.Autostart.OnAutomationSessionsChanged(null, new WslcAutomationInventory(@"C:\wslc.exe",
            [new(generation, new([new ContainerInfo { Id = "one", Name = "web", StateValue = 2 }]), ObservedAt: captured)]));

        Assert.False(fixture.AutostartStore.Tracks(owner));
        Assert.DoesNotContain(fixture.AutostartStore.Snapshot(), entry => AutostartStateStore.SameOwner(entry.SessionOwner, owner));
        Assert.True(fixture.AutostartStore.Tracks(other));
        Assert.Contains(fixture.AutostartStore.Snapshot(), entry => entry.Id == "two");
        Assert.Equal(!deleteData, File.Exists(Path.Combine(path, "data")));
    }

    [Fact]
    public async Task CleanupFailureLeavesRemovalPendingAndCanBeRetried()
    {
        using var fixture = new Fixture { FailCleanup = true };
        var definition = await fixture.Service.CreateAsync("work");
        var owner = AutomationSessionOwner.Capture(@"C:\wslc.exe", new("work"));
        fixture.AutostartStore.EnableTracking(owner);

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RemoveAsync(definition, deleteData: true));

        var pending = Assert.Single(fixture.Store.Snapshot());
        Assert.True(pending.PendingDeletion);
        Assert.Equal(owner, pending.PendingRemovalOwner);
        Assert.True(fixture.Service.HasProblems);
        Assert.False(fixture.RemovalGuard.IsRemovalInProgress(owner));
        Assert.True(fixture.RemovalGuard.IsBlocked(owner, null, DateTimeOffset.UtcNow));
        Assert.True(fixture.AutostartStore.Tracks(owner));
        Assert.True(Directory.Exists(fixture.Store.StoragePath(pending)));
        fixture.FailCleanup = false;
        await fixture.Service.RemoveAsync(pending, deleteData: true);
        Assert.Empty(fixture.Store.Snapshot());
        Assert.False(fixture.AutostartStore.Tracks(owner));
    }

    [Fact]
    public async Task KeepingSavedDataDoesNotForgetTrackingForExternalReplacement()
    {
        using var fixture = new Fixture();
        var definition = await fixture.Service.CreateAsync("work");
        await fixture.Service.StopAsync(definition);
        var owner = AutomationSessionOwner.Capture(@"C:\wslc.exe", new("work"));
        fixture.AutostartStore.EnableTracking(owner);
        fixture.SetSessions(new WslcSessionInfo { ID = 500, CreatorPid = 600, Name = "work" });
        var removed = false;
        fixture.Service.SessionRemoved += (_, _, _) => removed = true;

        await fixture.Service.RemoveAsync(definition, deleteData: false);

        Assert.True(fixture.AutostartStore.Tracks(owner));
        Assert.True(Directory.Exists(fixture.Store.StoragePath(definition)));
        Assert.False(removed);
    }

    [Fact]
    public async Task UnrelatedPendingFailureRemainsVisibleWhileAnotherSessionIsRemoved()
    {
        using var fixture = new Fixture { BlockDeletion = true };
        var current = await fixture.Service.CreateAsync("current");
        var unrelated = new ManagedSessionDefinition(Guid.NewGuid(), "unrelated");
        fixture.Store.Add(unrelated);
        fixture.Store.MarkForDeletion(unrelated);
        var operation = fixture.Service.RemoveAsync(current, deleteData: true);
        try
        {
            await fixture.DeletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Service.IsRemoving(current));
            Assert.True(fixture.Service.HasProblems);
        }
        finally { fixture.ReleaseDeletion.Set(); }
        await operation;
        Assert.True(fixture.Service.HasProblems);
    }

    [Fact]
    public async Task RemovingAndKeepingDataIgnoresDelayedTermination()
    {
        using var fixture = new Fixture();
        var definition = await fixture.Service.CreateAsync("work");
        var path = fixture.Store.StoragePath(definition);
        File.WriteAllText(Path.Combine(path, "data"), "kept");
        var handle = Assert.Single(fixture.Handles);
        handle.DelayNotification = true;

        Assert.Equal(path, await fixture.Service.RemoveAsync(definition, deleteData: false));
        handle.RaiseTerminated();

        Assert.Empty(fixture.Store.Snapshot());
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "data")));
        Assert.False(fixture.Service.HasProblems);
        Assert.True(handle.Disposed);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheSavedSessionsStorage()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.CreateAsync("first");
        var second = await fixture.Service.CreateAsync("second");
        File.WriteAllText(Path.Combine(fixture.Store.StoragePath(second), "data"), "keep");

        await fixture.Service.RemoveAsync(first, deleteData: true);

        Assert.False(Directory.Exists(fixture.Store.StoragePath(first)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Store.StoragePath(second), "data")));
        Assert.Equal(second, Assert.Single(fixture.Store.Snapshot()));
    }

    [Fact]
    public async Task StoppingKeepsTheDefinitionAndDataAndAllowsRestart()
    {
        using var fixture = new Fixture();
        var definition = await fixture.Service.CreateAsync("work");
        var path = fixture.Store.StoragePath(definition);
        File.WriteAllText(Path.Combine(path, "data"), "kept");
        var firstHandle = Assert.Single(fixture.Handles);
        firstHandle.DelayNotification = true;

        await fixture.Service.StopAsync(definition);
        firstHandle.RaiseTerminated();
        Assert.False(fixture.Service.IsRunning(definition));
        Assert.Equal(definition, Assert.Single(fixture.Store.Snapshot()));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "data")));
        Assert.False(fixture.Service.HasProblems);

        await fixture.Service.StartSavedAsync(definition);
        Assert.True(fixture.Service.IsRunning(definition));
        Assert.Equal(2, fixture.Handles.Count);
    }

    [Fact]
    public async Task StoppingDefaultPausesOnlyItsAutomationAndExplicitResumeRestartsIt()
    {
        using var fixture = new Fixture { BlockExternalStop = true };
        var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
        var initial = new WslcSessionInfo { ID = 50, CreatorPid = 60, Name = defaultName };
        var other = new WslcSessionInfo { ID = 70, CreatorPid = 80, Name = "other" };
        fixture.SetSessions(initial, other);
        var owner = AutomationSessionOwner.Capture(@"C:\wslc.exe", WslcSessionTarget.Default);
        var namedOwner = AutomationSessionOwner.Capture(@"C:\wslc.exe", new WslcSessionTarget(defaultName));
        fixture.AutostartStore.EnableTracking(owner);
        fixture.AutostartStore.EnableTracking(namedOwner);
        var stop = fixture.Service.StopExternalAsync(initial);
        try
        {
            await fixture.ExternalStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
            Assert.True(fixture.RemovalGuard.IsBlocked(owner, initial, DateTimeOffset.UtcNow));
            Assert.True(fixture.RemovalGuard.IsBlocked(namedOwner, initial, DateTimeOffset.UtcNow));
            Assert.False(fixture.RemovalGuard.IsBlocked(AutomationSessionOwner.Capture(@"C:\wslc.exe", new("other")),
                other, DateTimeOffset.UtcNow));
        }
        finally { fixture.ReleaseExternalStop.TrySetResult(); }
        await stop;

        Assert.DoesNotContain(fixture.Snapshot(), session => session.Name == defaultName);
        Assert.Contains(other, fixture.Snapshot());
        Assert.True(fixture.AutostartStore.Tracks(owner));
        Assert.True(fixture.AutostartStore.Tracks(namedOwner));
        Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));

        await fixture.Service.ResumeDefaultAsync();

        Assert.False(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        Assert.Contains(fixture.Snapshot(), session => session.Name == defaultName && session.ID != initial.ID);
        Assert.Contains(other, fixture.Snapshot());
        Assert.True(fixture.AutostartStore.Tracks(owner));
        Assert.True(fixture.AutostartStore.Tracks(namedOwner));
        Assert.Equal(1, fixture.ExternalStarts);
    }

    [Fact]
    public async Task AmbiguousStopFailureStaysPausedAndCanResumeExistingSession()
    {
        using var fixture = new Fixture { FailExternalStop = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StopExternalAsync(initial));

        Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        await fixture.Service.ResumeDefaultAsync();
        Assert.False(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        Assert.Contains(initial, fixture.Snapshot());
        Assert.Equal(0, fixture.ExternalStarts);
    }

    [Fact]
    public async Task AmbiguousStopFailureCanBeRetriedWithoutResumingAutomation()
    {
        using var fixture = new Fixture { FailExternalStop = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StopExternalAsync(initial));

        fixture.FailExternalStop = false;
        await fixture.Service.StopExternalAsync(initial);

        Assert.Empty(fixture.Snapshot());
        Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        Assert.Equal(2, fixture.ExternalStops);
    }

    [Fact]
    public async Task StopRevalidatesGenerationImmediatelyBeforeCommand()
    {
        using var fixture = new Fixture { BlockAutostartPause = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        var stop = fixture.Service.StopExternalAsync(initial);
        try
        {
            await fixture.ExternalPauseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.SetSessions(initial with { ID = 51, CreatorPid = 61 });
        }
        finally { fixture.ReleaseExternalPause.TrySetResult(); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => stop);
        Assert.Equal(0, fixture.ExternalStops);
        Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
    }

    [Fact]
    public async Task FailedQuiescenceCancelsPauseBeforeAnyStopCommand()
    {
        using var fixture = new Fixture { FailPollQuiescence = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.StopExternalAsync(initial));

        Assert.False(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        Assert.Equal(0, fixture.ExternalStops);
        Assert.Contains(initial, fixture.Snapshot());
    }

    [Fact]
    public async Task StoppingDefaultDoesNotInterruptAnotherSelectedSession()
    {
        using var fixture = new Fixture { FailPollQuiescence = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        var other = new WslcSessionInfo { ID = 70, CreatorPid = 80, Name = "other" };
        fixture.SetSessions(initial, other);
        fixture.Context.Select("other");
        var observation = fixture.Context.CaptureSnapshot();

        await fixture.Service.StopExternalAsync(initial);

        Assert.True(fixture.Context.TryApply(observation, () => { }));
        Assert.Contains(other, fixture.Snapshot());
        Assert.Equal(1, fixture.ExternalStops);
    }

    [Fact]
    public async Task StoppingDefaultWaitsForAutomaticStartWorkToFinish()
    {
        using var fixture = new Fixture { BlockAutostartWait = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        var stop = fixture.Service.StopExternalAsync(initial);
        try
        {
            await fixture.AutostartWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, fixture.ExternalStops);
            Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        }
        finally { fixture.ReleaseAutostartWait.TrySetResult(); }
        await stop;
        Assert.Equal(1, fixture.ExternalStops);
    }

    [Fact]
    public async Task StuckAutomaticStartWorkFailsStopWithoutTerminatingTheSession()
    {
        using var fixture = new Fixture(waitTimeout: TimeSpan.FromMilliseconds(50)) { BlockAutostartWait = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Service.StopExternalAsync(initial));

        Assert.False(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        Assert.Equal(0, fixture.ExternalStops);
        Assert.Contains(initial, fixture.Snapshot());
    }

    [Fact]
    public async Task TimedOutStopKeepsDefaultPausedUntilTheUserResumes()
    {
        using var fixture = new Fixture(commandTimeout: TimeSpan.FromMilliseconds(50)) { BlockExternalStop = true };
        var initial = new WslcSessionInfo
            { ID = 50, CreatorPid = 60, Name = WslcSessionActionGuard.ResolveDefaultSessionName() };
        fixture.SetSessions(initial);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Service.StopExternalAsync(initial));

        Assert.True(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
        Assert.Contains(initial, fixture.Snapshot());
        await fixture.Service.ResumeDefaultAsync();
        Assert.False(fixture.Context.IsDefaultSessionStopped(@"C:\wslc.exe", WslcSessionTarget.Default));
    }

    [Fact]
    public async Task ReplacementGenerationIsNotTreatedAsOwnedOrTerminated()
    {
        using var fixture = new Fixture();
        var definition = await fixture.Service.CreateAsync("work");
        var handle = Assert.Single(fixture.Handles);
        var replacement = new WslcSessionInfo { ID = 500, CreatorPid = 600, Name = "work" };
        fixture.SetSessions(replacement);

        Assert.False(fixture.Service.Owns(definition, replacement));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RemoveAsync(definition, true));

        Assert.Equal(0, handle.TerminateCount);
        Assert.Equal(definition, Assert.Single(fixture.Store.Snapshot()));
        Assert.True(Directory.Exists(fixture.Store.StoragePath(definition)));
    }

    [Fact]
    public async Task PendingDeletionIsNotRestartedAndCanBeKept()
    {
        using var fixture = new Fixture();
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "pending");
        fixture.Store.Add(definition);
        var pending = fixture.Store.MarkForDeletion(definition);
        var path = fixture.Store.StoragePath(pending);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data"), "kept");

        await fixture.Service.RestoreAsync();
        Assert.Empty(fixture.Handles);
        Assert.True(fixture.Service.HasProblems);

        await fixture.Service.RemoveAsync(pending, deleteData: false);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "data")));
        Assert.False(fixture.Service.HasProblems);
    }

    [Fact]
    public async Task FailedStartIsSavedAndRetryClearsTheFailure()
    {
        using var fixture = new Fixture { FailNextStart = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateAsync("work"));
        var definition = Assert.Single(fixture.Store.Snapshot());
        Assert.True(fixture.Service.HasProblems);
        Assert.NotNull(fixture.Service.FailureFor(definition.Id));
        Assert.True(fixture.Handles[0].Disposed);

        await fixture.Service.StartSavedAsync(definition);

        Assert.True(fixture.Service.IsRunning(definition));
        Assert.Null(fixture.Service.FailureFor(definition.Id));
        Assert.False(fixture.Service.HasProblems);
    }

    [Fact]
    public async Task MissingPreviouslyCreatedStorageIsNotReplacedByAnEmptySession()
    {
        using var fixture = new Fixture();
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "missing", StorageCreated: true);
        fixture.Store.Add(definition);

        await fixture.Service.RestoreAsync();

        Assert.False(Directory.Exists(fixture.Store.StoragePath(definition)));
        Assert.Empty(fixture.Handles);
        Assert.True(fixture.Service.HasProblems);
        Assert.Contains("missing", fixture.Service.FailureFor(definition.Id), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnexpectedRestorationFailureRemainsVisible()
    {
        using var fixture = new Fixture();

        fixture.Service.ReportRestoreFailure(new InvalidOperationException("discovery failed"));

        Assert.True(fixture.Service.HasProblems);
        Assert.Equal("discovery failed", fixture.Service.RestoreFailure);
    }

    [Fact]
    public async Task UnreadableSettingsBlockSessionChangesWithoutDeletingData()
    {
        using var fixture = new Fixture(corruptSettings: true);
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "work", StorageCreated: true);
        fixture.Store.Add(definition);
        var path = fixture.Store.StoragePath(definition);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data"), "kept");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CreateAsync("new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RemoveAsync(definition, true));

        Assert.Equal(definition, Assert.Single(fixture.Store.Snapshot()));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "data")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wcd-managed-session-tests-" + Guid.NewGuid().ToString("N"));
        private WslcSessionInfo[] _sessions = [];
        private int _nextId;
        public ManagedSessionStore Store { get; }
        public ManagedSessionService Service { get; }
        public AutostartStateStore AutostartStore { get; }
        public ContainerAutostartService Autostart { get; }
        public SessionRemovalGuard RemovalGuard { get; }
        public WslcSessionContext Context { get; } = new();
        public bool BlockExternalStop { get; set; }
        public bool FailExternalStop { get; set; }
        public bool FailPollQuiescence { get; set; }
        public bool BlockAutostartWait { get; set; }
        public int ExternalStops { get; private set; }
        public int ExternalStarts { get; private set; }
        public TaskCompletionSource ExternalStopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExternalStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AutostartWaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAutostartWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockAutostartPause { get; set; }
        public TaskCompletionSource ExternalPauseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExternalPause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailCleanup { get; set; }
        public bool BlockDeletion { get; set; }
        public int DeletionCount => Volatile.Read(ref _deletionCount);
        public TaskCompletionSource DeletionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseDeletion { get; } = new(false);
        public List<FakeHandle> Handles { get; } = [];
        public bool FailNextStart { get; set; }
        private IWslcService? _discovery;
        private ISettingsService? _settings;

        public Fixture(bool corruptSettings = false, TimeSpan? waitTimeout = null, TimeSpan? commandTimeout = null)
        {
            Directory.CreateDirectory(_root);
            Store = new ManagedSessionStore(NullLogger.Instance, Path.Combine(_root, "store"));
            var settingsPath = Path.Combine(_root, "settings");
            if (corruptSettings)
            {
                Directory.CreateDirectory(settingsPath);
                File.WriteAllText(Path.Combine(settingsPath, "settings.json"), "{bad");
            }
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, settingsPath)
            {
                WslcPath = @"C:\wslc.exe",
            };
            _settings = settings;
            if (corruptSettings)
                settings.Load();
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
                method.Name == nameof(IWslcService.GetSystemInfoAsync)
                    ? Task.FromResult(new WslcSystemInfo { Server = new WslcServerInfo { Sessions = Snapshot() } })
                    : throw new InvalidOperationException(method.Name));
            _discovery = discovery;
            var reader = new WslcSessionInventoryReader(discovery,
                (_, _) => throw new InvalidOperationException("Unexpected inventory read"),
                () => settings.WslcPath, NullLogger<WslcSessionInventoryReader>.Instance);
            AutostartStore = new AutostartStateStore(NullLogger<AutostartStateStore>.Instance, Path.Combine(_root, "autostart.json"));
            RemovalGuard = new SessionRemovalGuard(Store);
            Autostart = new ContainerAutostartService(discovery, new StatusMonitor(), settings,
                NullLogger<ContainerAutostartService>.Instance, Context, AutostartStore,
                _ => throw new InvalidOperationException("Unexpected automatic start"),
                reader.DiscoverAsync, (_, _, _) => Task.CompletedTask, removalGuard: RemovalGuard);
            Service = new ManagedSessionService(Store, reader, discovery,
                (target, _) => NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
                {
                    nameof(IWslcService.RestartSessionAsync) => StopExternalAsync(target, (CancellationToken)args[0]!),
                    nameof(IWslcService.ListImagesAsync) => StartExternalAsync(target),
                    _ => throw new InvalidOperationException(method.Name),
                }),
                settings, Context, new DispatcherQueue(),
                NullLogger<ManagedSessionService>.Instance, (name, _) =>
                {
                    var handle = new FakeHandle(
                        () =>
                        {
                            if (FailNextStart)
                            {
                                FailNextStart = false;
                                throw new InvalidOperationException("start failed");
                            }
                            lock (_gate)
                                _sessions = [.. _sessions, new WslcSessionInfo
                                    { ID = ++_nextId, CreatorPid = Environment.ProcessId, Name = name }];
                        },
                        () =>
                        {
                            lock (_gate)
                                _sessions = _sessions.Where(session => session.Name != name).ToArray();
                        });
                    Handles.Add(handle);
                    return handle;
                }, RemovalGuard, (owner, ct) => FailCleanup
                    ? Task.FromException(new IOException("controlled cleanup failure"))
                    : Autostart.QuiesceAndForgetOwnerAsync(owner, ct),
                deleteStorage: path =>
                {
                    Interlocked.Increment(ref _deletionCount);
                    DeletionStarted.TrySetResult();
                    if (BlockDeletion) ReleaseDeletion.Wait(TimeSpan.FromSeconds(10));
                    Directory.Delete(path, recursive: true);
                }, waitForSelectedPoll: _ => FailPollQuiescence
                    ? Task.FromException(new IOException("poll did not stop"))
                    : Task.CompletedTask, waitForAutostart: WaitForAutostartAsync,
                stopQuiescenceTimeout: waitTimeout, stopCommandTimeout: commandTimeout,
                pauseAutostart: PauseAutostartAsync, resumeAutostart: Autostart.ResumeOwnerAsync,
                createGenerationClient: (target, executable, generation) =>
                    NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
                    {
                        nameof(IWslcService.RestartSessionAsync) => StopExternalWithGenerationCheck(
                            target, executable, generation, (CancellationToken)args[0]!),
                        nameof(IWslcService.ListImagesAsync) => StartExternalAsync(target),
                        _ => throw new InvalidOperationException(method.Name),
                    }));
        }

        private async Task PauseAutostartAsync(AutomationSessionOwner owner, CancellationToken ct)
        {
            ExternalPauseEntered.TrySetResult();
            if (BlockAutostartPause) await ReleaseExternalPause.Task.WaitAsync(ct);
            await Autostart.PauseOwnerAsync(owner, ct);
        }

        private async Task<CommandResult> StopExternalWithGenerationCheck(WslcSessionTarget target,
            string executable, WslcSessionInfo? generation, CancellationToken ct)
        {
            if (generation is not null)
                await WslcSessionActionGuard.VerifyAsync(_discovery!, () => _settings!.WslcPath,
                    generation, executable, ct);
            return await StopExternalAsync(target, ct);
        }

        private async Task WaitForAutostartAsync(CancellationToken ct)
        {
            AutostartWaitEntered.TrySetResult();
            if (BlockAutostartWait) await ReleaseAutostartWait.Task.WaitAsync(ct);
            await Autostart.WaitForCurrentRestoreAsync(ct);
        }

        private async Task<CommandResult> StopExternalAsync(WslcSessionTarget target, CancellationToken ct)
        {
            ExternalStops++;
            ExternalStopEntered.TrySetResult();
            if (BlockExternalStop) await ReleaseExternalStop.Task.WaitAsync(ct);
            if (FailExternalStop) return new CommandResult { ExitCode = 1, StandardError = "stop failed" };
            lock (_gate)
                _sessions = _sessions.Where(session => session.Name != target.Name).ToArray();
            return new CommandResult();
        }

        private Task<IReadOnlyList<ImageInfo>> StartExternalAsync(WslcSessionTarget target)
        {
            ExternalStarts++;
            var name = target.Name ?? WslcSessionActionGuard.ResolveDefaultSessionName();
            lock (_gate)
                _sessions = [.. _sessions, new WslcSessionInfo { ID = ++_nextId, CreatorPid = 61, Name = name }];
            return Task.FromResult<IReadOnlyList<ImageInfo>>([]);
        }

        public void SetSessions(params WslcSessionInfo[] sessions)
        {
            lock (_gate)
                _sessions = sessions;
        }

        private int _deletionCount;

        public WslcSessionInfo[] Snapshot()
        {
            lock (_gate)
                return _sessions.ToArray();
        }

        public void Dispose()
        {
            Service.Dispose();
            Autostart.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeHandle(Action start, Action terminate) : IManagedSessionHandle
    {
        public event Action? Terminated;
        public bool DelayNotification { get; set; }
        public bool Disposed { get; private set; }
        public int TerminateCount { get; private set; }
        public void Start() => start();
        public void Terminate()
        {
            TerminateCount++;
            terminate();
            if (!DelayNotification)
                RaiseTerminated();
        }
        public void RaiseTerminated() => Terminated?.Invoke();
        public void Dispose() => Disposed = true;
    }
}
#endif
