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
using WslContainerDesktop.Tray;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class ContainerAutostartSessionTests
{
    [Fact]
    public async Task IntentionalSessionStopPreservesHistoryAndResumesWithoutManualRetry()
    {
        using var fixture = new Fixture(legacy: false);
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        var generation = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "SDK" };
        fixture.Store.EnableTracking(owner);
        fixture.Service.OnAutomationSessionsChanged(null, new("engine", [
            new(generation, new([new ContainerInfo
                { Id = "container-id", Name = "web", StateValue = (int)ContainerState.Running }]),
                ObservedAt: DateTimeOffset.UtcNow)]));
        fixture.RemovalGuard.BeginStop(owner, generation);
        await fixture.Service.PauseOwnerAsync(owner, CancellationToken.None);
        fixture.RemovalGuard.CompleteStop(owner);
        fixture.SessionAvailable = false;
        await fixture.Service.RetryPendingAsync();
        Assert.Empty(fixture.Starts);
        Assert.Empty(fixture.Clients);
        Assert.Empty(fixture.Service.Problems);
        Assert.Contains(fixture.Store.Snapshot(), entry => entry.Id == "container-id");
        fixture.SessionAvailable = true;
        fixture.RemovalGuard.ReleaseStoppedSession(owner);
        await fixture.Service.ResumeOwnerAsync(owner, CancellationToken.None);
        Assert.Equal(["SDK:container-id"], fixture.Starts);
    }

    [Fact]
    public async Task RecreatedExternalSessionResumesOnFreshObservationWithoutRetryButton()
    {
        using var fixture = new Fixture(legacy: false);
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        var old = new WslcSessionInfo { ID = 7, CreatorPid = 100, Name = "SDK" };
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = "container-id", Name = "web", SessionOwner = owner }]);
        fixture.RemovalGuard.BeginStop(owner, old);
        await fixture.Service.PauseOwnerAsync(owner, CancellationToken.None);
        fixture.RemovalGuard.CompleteStop(owner);
        fixture.Service.OnAutomationSessionsChanged(null, new("engine", [
            new(new() { ID = 1, CreatorPid = 100, Name = "SDK" }, new([new ContainerInfo
                { Id = "container-id", Name = "web", StateValue = (int)ContainerState.Stopped }]),
                ObservedAt: DateTimeOffset.UtcNow)]));
        Assert.False(fixture.RemovalGuard.IsIntentionallyStopped(owner));
        await fixture.Service.RetryPendingAsync();
        Assert.Equal(["SDK:container-id"], fixture.Starts);
    }

    [Fact]
    public async Task PersistedPendingRemovalBlocksAutomaticStartAfterRestart()
    {
        using var fixture = new Fixture(legacy: false);
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = "container-id", Name = "web", SessionOwner = owner }]);
        var definition = new ManagedSessionDefinition(Guid.NewGuid(), "SDK");
        fixture.ManagedSessions.Add(definition);
        fixture.ManagedSessions.MarkForDeletion(definition, owner);
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();

        Assert.Empty(fixture.Starts);
        Assert.True(fixture.Store.Tracks(owner));
    }

    [Fact]
    public async Task ForgetWaitsForInFlightStartAndRejectsLateObservation()
    {
        using var fixture = new Fixture(legacy: false) { BlockStart = true };
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = "container-id", Name = "web", SessionOwner = owner }]);
        fixture.RecreateService();
        var restore = fixture.Service.RestoreThenAttachAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observedAt = DateTimeOffset.UtcNow;
        var forget = fixture.Service.ForgetOwnerAsync(owner);
        Assert.False(forget.IsCompleted);
        fixture.ReleaseStart.SetResult(new CommandResult());
        await restore;
        await forget;
        var session = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "SDK" };
        fixture.Service.OnAutomationSessionsChanged(null, new WslcAutomationInventory("engine",
            [new(session, new([new ContainerInfo { Id = "container-id", Name = "web", StateValue = (int)ContainerState.Running }]),
                ObservedAt: observedAt)]));

        Assert.False(fixture.Store.Tracks(owner));
        Assert.DoesNotContain(fixture.Store.Snapshot(), entry => entry.Id == "container-id");
        Assert.DoesNotContain(fixture.Service.Problems, problem => AutostartStateStore.SameOwner(problem.Owner, owner));
    }

    [Fact]
    public async Task LegacyHistoryStaysPausedAndUnchangedUntilAssigned()
    {
        using var fixture = new Fixture(legacy: true);

        await fixture.Service.RestoreThenAttachAsync();

        Assert.Empty(fixture.Starts);
        Assert.Null(Assert.Single(fixture.Store.Snapshot()).SessionOwner);
        Assert.Contains("container-id", File.ReadAllText(fixture.StateFile));
    }

    [Fact]
    public async Task UnreadableSettingsPauseRestorationAndTrackingWithoutChangingHistory()
    {
        using var fixture = new Fixture(legacy: true) { SettingsLoadError = true };
        var entry = Assert.Single(fixture.Store.Snapshot());
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.AssignLegacy(entry, owner);
        var saved = File.ReadAllText(fixture.StateFile);
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();
        fixture.Context.Select("SDK");
        fixture.Service.OnStatusChanged(null, new EngineStatusSnapshot
        {
            Health = EngineHealth.Healthy,
            Containers = [new ContainerInfo { Id = "other", Name = "web", StateValue = (int)ContainerState.Running }],
        });
        await fixture.Service.RetryPendingAsync();

        Assert.Empty(fixture.Clients);
        Assert.Empty(fixture.Starts);
        Assert.Equal(saved, File.ReadAllText(fixture.StateFile));
    }

    [Fact]
    public async Task AssignedNamedSessionRestoresAfterRestartWithoutChangingManagementSelection()
    {
        using var fixture = new Fixture(legacy: true);
        var entry = Assert.Single(fixture.Store.Snapshot());
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.AssignLegacy(entry, owner);
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();

        Assert.Equal(["SDK:container-id"], fixture.Starts);
        Assert.Null(fixture.Context.SelectedSessionName);
        Assert.True(fixture.Store.Tracks(owner));
    }

    [Theory]
    [InlineData("original-id")]
    [InlineData("")]
    public async Task SameNamedReplacementIsNeverAutomaticallyStarted(string savedId)
    {
        using var fixture = new Fixture(legacy: false) { ContainerId = "replacement-id" };
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = savedId, Name = "web", SessionOwner = owner }]);
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();
        await fixture.Service.RetryPendingAsync();

        Assert.Empty(fixture.Starts);
        Assert.Equal(ContainerAutostartService.ProblemKind.ContainerMissing, Assert.Single(fixture.Service.Problems).Kind);
        Assert.Contains(fixture.Store.Snapshot(), entry => entry.Id == savedId && entry.Name == "web");
    }

    [Fact]
    public async Task ShortSavedIdStillMatchesTheSameFullContainerId()
    {
        using var fixture = new Fixture(legacy: false) { ContainerId = "abcdef0123456789abcdef0123456789" };
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = "abcdef012345", Name = "web", SessionOwner = owner }]);
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();

        Assert.Equal(["SDK:abcdef0123456789abcdef0123456789"], fixture.Starts);
    }

    [Fact]
    public async Task MissingNamedSessionWaitsWithoutCreatingItThenResumesWhenAvailable()
    {
        using var fixture = new Fixture(legacy: true) { SessionAvailable = false };
        var entry = Assert.Single(fixture.Store.Snapshot());
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.AssignLegacy(entry, owner);
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();
        Assert.Empty(fixture.Clients);
        Assert.Equal(ContainerAutostartService.ProblemKind.SessionUnavailable,
            Assert.Single(fixture.Service.Problems).Kind);

        fixture.SessionAvailable = true;
        await fixture.Service.RetryPendingAsync();
        Assert.Equal(["SDK:container-id"], fixture.Starts);
        Assert.Empty(fixture.Service.Problems);
    }

    [Fact]
    public async Task UnhealthySelectedSessionDoesNotBlockRetryForAnAssignedSession()
    {
        using var fixture = new Fixture(legacy: true) { SessionAvailable = false, ImmediateRetry = true };
        var entry = Assert.Single(fixture.Store.Snapshot());
        fixture.Store.AssignLegacy(entry, AutomationSessionOwner.Capture("engine", new("SDK")));
        fixture.RecreateService();
        await fixture.Service.RestoreThenAttachAsync();
        Assert.Empty(fixture.Clients);

        var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ProblemsChanged += (_, _) =>
        {
            if (fixture.Service.Problems.Count == 0)
                resolved.TrySetResult();
        };
        fixture.SessionAvailable = true;
        fixture.Service.OnStatusChanged(null, new EngineStatusSnapshot { Health = EngineHealth.Down });

        await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["SDK:container-id"], fixture.Starts);
        Assert.Null(fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task UnstartableContainerRemainsPendingAndReportsWhy()
    {
        using var fixture = new Fixture(legacy: true) { ContainerState = ContainerState.Paused };
        var entry = Assert.Single(fixture.Store.Snapshot());
        fixture.Store.AssignLegacy(entry, AutomationSessionOwner.Capture("engine", new("SDK")));
        fixture.RecreateService();

        await fixture.Service.RestoreThenAttachAsync();

        Assert.Empty(fixture.Starts);
        var problem = Assert.Single(fixture.Service.Problems);
        Assert.Equal(ContainerAutostartService.ProblemKind.NotStartable, problem.Kind);
        Assert.Contains("web", problem.Detail);
    }

    [Fact]
    public async Task SuccessfulStartRetryDoesNotHideAnUnresolvedHistorySaveFailure()
    {
        using var fixture = new Fixture(legacy: true);
        var entry = Assert.Single(fixture.Store.Snapshot());
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.AssignLegacy(entry, owner);
        fixture.Context.Select("SDK");
        fixture.RecreateService();
        await fixture.Service.RestoreThenAttachAsync();
        using (new FileStream(fixture.StateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fixture.Service.OnStatusChanged(null, new EngineStatusSnapshot
            {
                Health = EngineHealth.Healthy,
                Containers = [new ContainerInfo { Id = "replacement-id", Name = "web", StateValue = (int)ContainerState.Running }],
            });
            Assert.Contains(fixture.Service.Problems, problem => problem.Kind == ContainerAutostartService.ProblemKind.StorageFailed);
            await fixture.Service.RetryPendingAsync();
            Assert.Contains(fixture.Service.Problems, problem => problem.Kind == ContainerAutostartService.ProblemKind.StorageFailed);
        }

        fixture.Service.OnStatusChanged(null, new EngineStatusSnapshot
        {
            Health = EngineHealth.Healthy,
            Containers = [new ContainerInfo { Id = "replacement-id", Name = "web", StateValue = (int)ContainerState.Running }],
        });
        Assert.DoesNotContain(fixture.Service.Problems, problem => problem.Kind == ContainerAutostartService.ProblemKind.StorageFailed);
    }

    [Fact]
    public async Task InFlightRestorePinsEngineAndSelectionUntilStartCompletes()
    {
        using var fixture = new Fixture(legacy: true) { BlockStart = true };
        var entry = Assert.Single(fixture.Store.Snapshot());
        fixture.Store.AssignLegacy(entry, AutomationSessionOwner.Capture("engine", new("SDK")));
        fixture.RecreateService();
        var restore = fixture.Service.RestoreThenAttachAsync();
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Context.TrySelect("another"));
        Assert.False(fixture.Context.TryApplyIfUnlocked(() => { }));
        fixture.ReleaseStart.SetResult(new CommandResult());
        await restore;

        Assert.Equal(["SDK:container-id"], fixture.Starts);
        Assert.True(fixture.Context.TrySelect("another"));
    }

    [Fact]
    public async Task StatusFromUnenrolledSessionDoesNotOverwriteLegacyOrOtherSessionHistory()
    {
        using var fixture = new Fixture(legacy: true);
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = "sdk-id", Name = "web", SessionOwner = owner }]);
        fixture.RecreateService();
        await fixture.Service.RestoreThenAttachAsync();
        fixture.Context.Select("unrelated");

        fixture.Service.OnStatusChanged(null, new EngineStatusSnapshot
        {
            Health = EngineHealth.Healthy,
            Containers = [new ContainerInfo { Id = "other", Name = "web", StateValue = (int)ContainerState.Running }],
        });

        var state = fixture.Store.Snapshot();
        Assert.Contains(state, entry => entry.Id == "container-id" && entry.SessionOwner is null);
        Assert.Contains(state, entry => entry.Id == "sdk-id" && entry.SessionOwner == owner);
        Assert.DoesNotContain(state, entry => entry.Id == "other");
    }

    [Fact]
    public async Task OtherSessionsTrackRunningContainersWithoutChangingSelection()
    {
        using var fixture = new Fixture(legacy: false);
        await fixture.Service.RestoreThenAttachAsync();
        var first = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "SDK" };
        var second = new WslcSessionInfo { ID = 2, CreatorPid = 200, Name = "other" };
        var running = new[] { new ContainerInfo { Id = "same", Name = "web", StateValue = (int)ContainerState.Running } };

        fixture.Service.OnAutomationSessionsChanged(null, new WslcAutomationInventory("engine",
            [new(first, new(running)), new(second, new(running))]));

        var state = fixture.Store.Snapshot().Where(entry => entry.Id == "same").ToArray();
        Assert.Equal(2, state.Length);
        Assert.Contains(state, entry => entry.SessionOwner?.SessionName == "SDK");
        Assert.Contains(state, entry => entry.SessionOwner?.SessionName == "other");
        Assert.Null(fixture.Context.SelectedSessionName);
    }

    [Fact]
    public async Task FailedOtherSessionObservationDoesNotReplaceSavedHistory()
    {
        using var fixture = new Fixture(legacy: false);
        var owner = AutomationSessionOwner.Capture("engine", new("SDK"));
        fixture.Store.EnableTracking(owner);
        fixture.Store.ReplaceOwned(owner, [new AutostartEntry { Id = "saved", Name = "web", SessionOwner = owner }]);
        fixture.RecreateService();
        await fixture.Service.RestoreThenAttachAsync();
        var session = new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "SDK" };

        fixture.Service.OnAutomationSessionsChanged(null, new WslcAutomationInventory("engine",
            [new(session, new(null, Error: "unavailable"))]));

        Assert.Contains(fixture.Store.Snapshot(), entry => entry.Id == "saved" && entry.SessionOwner == owner);
    }

    [Fact]
    public async Task ManualStopForOneSessionCancelsItsPendingAutostartAndPreservesOtherOwners()
    {
        using var fixture = new Fixture(legacy: false) { SessionAvailable = false };
        var first = AutomationSessionOwner.Capture("engine", new("SDK"));
        var second = AutomationSessionOwner.Capture("engine", new("other"));
        foreach (var owner in new[] { first, second })
        {
            fixture.Store.EnableTracking(owner);
            fixture.Store.ReplaceOwned(owner, [new AutostartEntry
            {
                Id = "container-id", Name = owner == first ? "" : "web", SessionOwner = owner,
            }]);
        }
        fixture.RecreateService();
        await fixture.Service.RestoreThenAttachAsync();

        fixture.Suppression.Suppress("web", first, "container-id");
        fixture.SessionAvailable = true;
        await fixture.Service.RetryPendingAsync();
        fixture.Service.OnAutomationSessionsChanged(null, new WslcAutomationInventory("engine",
            [new(new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "SDK" },
                new([new ContainerInfo { Id = "container-id", Name = "web", StateValue = (int)ContainerState.Running }]))]));

        Assert.Empty(fixture.Starts);
        Assert.DoesNotContain(fixture.Store.Snapshot(), entry => entry.Id == "container-id" && entry.SessionOwner == first);
        Assert.Contains(fixture.Store.Snapshot(), entry => entry.Id == "container-id" && entry.SessionOwner == second);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "wcd-autostart-tests-" + Guid.NewGuid().ToString("N"));
        public string StateFile => Path.Combine(_root, "state.json");
        public WslcSessionContext Context { get; } = new();
        public RestartSuppressionState Suppression { get; } = new();
        public SessionRemovalGuard RemovalGuard { get; }
        public ManagedSessionStore ManagedSessions { get; }
        public AutostartStateStore Store { get; private set; }
        public ContainerAutostartService Service { get; private set; }
        public List<string> Clients { get; } = [];
        public List<string> Starts { get; } = [];
        public bool SessionAvailable { get; set; } = true;
        public bool BlockStart { get; set; }
        public bool ImmediateRetry { get; set; }
        public bool SettingsLoadError { get; set; }
        public string ContainerId { get; set; } = "container-id";
        public ContainerState ContainerState { get; set; } = ContainerState.Stopped;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CommandResult> ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture(bool legacy)
        {
            Directory.CreateDirectory(_root);
            if (legacy)
                File.WriteAllText(StateFile, """[{"Id":"container-id","Name":"web"}]""");
            Store = CreateStore();
            ManagedSessions = new ManagedSessionStore(NullLogger.Instance, Path.Combine(_root, "managed"));
            RemovalGuard = new SessionRemovalGuard(ManagedSessions);
            Service = CreateService();
        }

        public void RecreateService()
        {
            Service.Dispose();
            Store = CreateStore();
            Service = CreateService();
        }

        private AutostartStateStore CreateStore() =>
            new(NullLogger<AutostartStateStore>.Instance, StateFile);

        private ContainerAutostartService CreateService()
        {
            var discovery = NetworkTestProxy.Create<IWslcService>((method, _) =>
                throw new InvalidOperationException(method.Name));
            var settings = NetworkTestProxy.Create<ISettingsService>((method, _) => method.Name switch
            {
                "get_RestartRunningContainersOnLaunch" => true,
                "get_WslcPath" => "engine",
                "get_HasLoadError" => SettingsLoadError,
                _ => throw new InvalidOperationException(method.Name),
            });
            return new ContainerAutostartService(discovery, new StatusMonitor(), settings,
                NullLogger<ContainerAutostartService>.Instance, Context, Store, CreateClient,
                _ => Task.FromResult<IReadOnlyList<WslcSessionInfo>>(SessionAvailable
                    ? [new WslcSessionInfo { ID = 1, CreatorPid = 100, Name = "SDK" }] : []),
                (_, _, _) => Task.CompletedTask, retryInterval: ImmediateRetry ? TimeSpan.Zero : null,
                suppression: Suppression, removalGuard: RemovalGuard);
        }

        private IWslcService CreateClient(AutomationSessionOwner owner)
        {
            Clients.Add(owner.SessionName ?? "default");
            return NetworkTestProxy.Create<IWslcService>((method, args) => method.Name switch
            {
                nameof(IWslcService.IsEngineAvailableAsync) => Task.FromResult(true),
                nameof(IWslcService.ListContainersAsync) => Task.FromResult<IReadOnlyList<ContainerInfo>>(
                    [new ContainerInfo { Id = ContainerId, Name = "web", StateValue = (int)ContainerState }]),
                nameof(IWslcService.StartContainerAsync) => StartAsync(owner, (string)args[0]!),
                _ => throw new InvalidOperationException(method.Name),
            });
        }

        private Task<CommandResult> StartAsync(AutomationSessionOwner owner, string id)
        {
            Starts.Add($"{owner.SessionName ?? "default"}:{id}");
            Started.TrySetResult();
            return BlockStart ? ReleaseStart.Task : Task.FromResult(new CommandResult());
        }

        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
