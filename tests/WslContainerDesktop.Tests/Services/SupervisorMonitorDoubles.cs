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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

// Supervisor tests exercise the real orchestration with controlled snapshots, not WinUI pollers.
/// <summary>Test double for the compose health watchdog that exposes a controllable latest health snapshot.</summary>
public sealed class HealthWatchdog : IAppHealthObservationSource
{
    public HealthSnapshot Latest { get; set; } = new([]);
    public sealed record HealthSnapshot(IReadOnlyList<ContainerHealthSnapshot> Containers);
    public IReadOnlyList<ContainerHealthSnapshot> GetObservations() => Latest.Containers;
    public void ApplyMirroredSnapshot(WslContainerDesktop.Services.HealthSnapshot snapshot) =>
        Latest = new(snapshot.Containers);
}

/// <summary>Test double for the app status monitor that lets supervisor tests provide inventory and observe refresh requests.</summary>
public sealed class StatusMonitor
{
    public StatusSnapshot? Latest { get; set; }
    public Action? RefreshRequested { get; set; }
    public Microsoft.UI.Dispatching.DispatcherQueue Dispatcher { get; } = new();
    public event EventHandler<EngineStatusSnapshot>? StatusChanged;
    public event EventHandler<WslcAutomationInventory>? AutomationSessionsChanged;
    public void Publish(EngineStatusSnapshot snapshot)
    {
        Latest = new(snapshot.Containers, snapshot.Health);
        StatusChanged?.Invoke(this, snapshot);
    }
    public void RequestRefresh() => RefreshRequested?.Invoke();
    public void RequestAutomationRefresh() { }
    public IDisposable ObserveSessionForOperation(WslcSessionInfo session, string executable) => new EmptyLease();
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    public void PublishAutomation(WslcAutomationInventory snapshot) =>
        AutomationSessionsChanged?.Invoke(this, snapshot);
    public void SuppressExitNotification(string id) { }
    /// <summary>Represents the container inventory snapshot that supervisor tests want the monitor to publish.</summary>
    public sealed record StatusSnapshot(IReadOnlyList<ContainerInfo> Containers,
        WslContainerDesktop.Tray.EngineHealth Health = WslContainerDesktop.Tray.EngineHealth.Healthy);
}

public sealed class EngineStatusSnapshot
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ContainerInfo> Containers { get; init; } = [];
    public WslContainerDesktop.Tray.EngineHealth Health { get; init; }
    public WslcSessionInfo? Session { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
}

public sealed class WslcSessionClientFactory(
    Func<WslcSessionInfo, string, bool, IWslcService> create)
{
    public IWslcService CreateGuarded(WslcSessionInfo session, string executable, bool managed) =>
        create(session, executable, managed);
}

public sealed class SessionAutomationCoordinator
{
    public event EventHandler? Changed;
    private string? _error;
    public Func<string?>? ErrorProvider { get; set; }
    public Func<AutomationSessionOwner, bool>? ObservedProvider { get; set; }
    public Func<AutomationSessionOwner, bool>? InitializingProvider { get; set; }
    public string? Error { get => ErrorProvider is null ? _error : ErrorProvider(); set => _error = value; }
    public string? ObservedSession { get; set; }
    public string? InitializingSession { get; set; }
    public bool IsObserved(AutomationSessionOwner owner) => ObservedProvider?.Invoke(owner) ??
        (owner.SessionName == ObservedSession && ObservedSession is not null);
    public bool HasFreshObservationInProgress(AutomationSessionOwner owner) =>
        InitializingProvider?.Invoke(owner) ??
        (owner.SessionName == InitializingSession && InitializingSession is not null);
    public IReadOnlyList<ContainerInfo>? GetContainers(AutomationSessionOwner owner) => null;
    public Task<ComposeSessionScope> GetComposeScopeAsync(WslcSessionSelection selection, CancellationToken ct = default) =>
        throw new InvalidOperationException("No scope configured.");
    public void RequestReconciliation(AutomationSessionOwner owner) { }
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
