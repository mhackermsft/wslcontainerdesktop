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

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.ViewModels;

/// <summary>Backs the shell header/status strip: live engine health indicator.</summary>
public partial class ShellViewModel : ObservableObject
{
    private readonly StatusMonitor _monitor;
    private readonly ManagedSessionService _managedSessions;
    private EngineStatusSnapshot? _displayedEngine;
    private readonly CombinedSessionViewModel _combined;
    private readonly ISettingsService _settings;
    private readonly WslcSessionContext _sessionContext;
    private readonly SessionStatusTracker _sessionStatuses = new();
    private string? _statusExecutable;

    /// <summary>Cached, localized details used by the hover and keyboard-accessible flyout.</summary>
    [ObservableProperty]
    private string _sessionStatusDetails = string.Empty;

    private bool IsCurrentExecutable(string executable) =>
        string.Equals(executable, _settings.WslcPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an app-created session needs attention.</summary>
    public bool HasSessionProblems => _managedSessions.HasProblems;
    /// <summary>Persistent explanation of unavailable or incomplete saved sessions.</summary>
    public string SessionProblemText => UiText.Get("ManagedSession_Attention",
        "Some saved sessions could not start or need cleanup. Review them on the WSL engine page.");

    /// <summary>Bindable state for engine status text used by the view.</summary>
    [ObservableProperty]
    private string _engineStatusText = UiText.Get("Shell_DefaultSessionStatus", "Default CLI session · {0}",
        UiText.Get("Common_Text0247", "Connecting…"));

    /// <summary>Bindable state for engine status brush used by the view.</summary>
    [ObservableProperty]
    private Brush _engineStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    /// <summary>Bindable state for kubernetes status text used by the view.</summary>
    [ObservableProperty]
    private string _kubernetesStatusText = UiText.Get("Common_Text0248", "Kubernetes: …");

    /// <summary>Bindable state for kubernetes status brush used by the view.</summary>
    [ObservableProperty]
    private Brush _kubernetesStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    /// <summary>The footer Kubernetes indicator is shown only when the cluster is installed.</summary>
    [ObservableProperty]
    private Visibility _kubernetesVisibility = Visibility.Collapsed;

    /// <summary>Creates the Shell view model and stores its injected services.</summary>
    public ShellViewModel(StatusMonitor monitor, ManagedSessionService managedSessions,
        CombinedSessionViewModel combined, ISettingsService settings, WslcSessionContext sessionContext)
    {
        _monitor = monitor;
        _managedSessions = managedSessions;
        _combined = combined;
        _settings = settings;
        _sessionContext = sessionContext;
        UiText.LanguageChanged += (_, _) =>
        {
            RefreshSessionStatus();
            KubernetesStatusText = UiText.Translate(KubernetesStatusText);
            OnPropertyChanged(nameof(SessionProblemText));
        };
        _managedSessions.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSessionProblems));
            RefreshSessionStatus();
        };
        _managedSessions.SessionRemoved += (name, executable, at) =>
        {
            if (IsCurrentExecutable(executable))
                _sessionStatuses.Remove(name, at);
        };
        _sessionContext.EngineSettingChanged += (_, _) =>
        {
            _sessionStatuses.Clear();
            _displayedEngine = null;
            RefreshSessionStatus();
        };
        _monitor.AutomationSessionsChanged += (_, batch) =>
        {
            if (!IsCurrentExecutable(batch.ExecutablePath)) return;
            if (batch.DiscoveredSessions is { } discovered && batch.DiscoveredAt is { } at)
                _sessionStatuses.Discover(discovered, at);
            foreach (var observation in batch.Sessions)
                _sessionStatuses.ObserveContainers(observation);
            RefreshSessionStatus();
        };
        _combined.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(CombinedSessionViewModel.IsBusy) || _combined.IsBusy) return;
            if (_combined.HasSuccessfulDiscovery && _combined.LastDiscoveryAt is { } at)
            {
                _sessionStatuses.Discover(_combined.Sessions.Where(choice => choice.IsAvailable)
                    .Select(choice => choice.Session).ToArray(), at);
                foreach (var observation in _combined.SessionObservations)
                    _sessionStatuses.ObserveContainers(new(observation.Session, observation.Containers,
                        observation.SessionChanged, observation.ObservedAt));
            }
            RefreshSessionStatus();
        };
        _monitor.StatusChanged += OnStatusChanged;
        _monitor.K8sStatusChanged += OnK8sStatusChanged;

        if (_monitor.Latest is not null)
        {
            Apply(_monitor.Latest);
        }

        if (_monitor.LatestK8s is not null)
        {
            ApplyK8s(_monitor.LatestK8s);
        }
        RefreshSessionStatus();
    }

    /// <summary>Handles status changed changes and updates related view-model state.</summary>
    private void OnStatusChanged(object? sender, EngineStatusSnapshot e) => Apply(e);

    /// <summary>Handles k8s status changed changes and updates related view-model state.</summary>
    private void OnK8sStatusChanged(object? sender, K8sStatusSnapshot e) => ApplyK8s(e);

    /// <summary>Applies apply k8s state to bindable properties.</summary>
    private void ApplyK8s(K8sStatusSnapshot snapshot)
    {
        KubernetesVisibility = snapshot.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        KubernetesStatusText = UiText.Translate(snapshot.Summary);

        var color = snapshot.State switch
        {
            Models.ClusterState.Running => Color.FromArgb(255, 45, 200, 95),
            Models.ClusterState.Stopped or Models.ClusterState.Starting => Color.FromArgb(255, 240, 180, 40),
            _ => Color.FromArgb(255, 150, 150, 150),
        };

        KubernetesStatusBrush = new SolidColorBrush(color);
    }

    /// <summary>Applies apply state to bindable properties.</summary>
    private void Apply(EngineStatusSnapshot snapshot)
    {
        if (!IsCurrentExecutable(snapshot.ExecutablePath)) return;
        _displayedEngine = snapshot;
        var session = snapshot.Session;
        var name = session?.Name ?? _sessionContext.Capture().Name;
        if (name is null)
        {
            try { name = WslcSessionActionGuard.ResolveDefaultSessionName(); }
            catch (InvalidOperationException)
            {
                // The unknown overall state remains visible; do not invent a default-session identity.
            }
        }
        if (name is not null)
        {
            _sessionStatuses.Observe(new(name, session,
                snapshot.DefaultSessionStoppedByUser ? SessionIndicatorState.Paused : snapshot.Health switch
                {
                    EngineHealth.Healthy => SessionIndicatorState.Active,
                    EngineHealth.Degraded => SessionIndicatorState.Attention,
                    EngineHealth.Down => SessionIndicatorState.Failed,
                    _ => SessionIndicatorState.Unknown,
                }, snapshot.ObservedAt,
                snapshot.Health is EngineHealth.Healthy or EngineHealth.Degraded ? snapshot.RunningCount : null,
                snapshot.Health is EngineHealth.Healthy or EngineHealth.Degraded ? snapshot.TotalCount : null));
        }
        RefreshSessionStatus();
    }

    /// <summary>Reformats existing observations only; opening details never starts an engine read.</summary>
    public void RefreshSessionStatus()
    {
        if (_statusExecutable is not null && !IsCurrentExecutable(_statusExecutable))
            _sessionStatuses.Clear();
        _statusExecutable = _settings.WslcPath;
        var now = DateTimeOffset.UtcNow;
        var sessions = _sessionStatuses.Snapshot(now).ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var name in sessions.Keys.ToArray())
            if (_managedSessions.IsIntentionallyStopped(name))
                sessions[name] = sessions[name] with { State = SessionIndicatorState.Paused, Running = null, Total = null };
        foreach (var saved in _managedSessions.Sessions)
        {
            var failure = _managedSessions.FailureFor(saved.Id);
            var removing = _managedSessions.IsRemoving(saved);
            if (failure is not null || saved.PendingDeletion && !removing)
                sessions[saved.Name] = new(saved.Name, null, SessionIndicatorState.Attention, null);
            else if (removing)
                sessions[saved.Name] = new(saved.Name, null, SessionIndicatorState.Removing, null);
            else if (!_managedSessions.IsRunning(saved) && !sessions.ContainsKey(saved.Name))
                sessions[saved.Name] = new(saved.Name, null, SessionIndicatorState.Paused, null);
        }
        var values = sessions.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var active = values.Count(item => item.State == SessionIndicatorState.Active);
        var hasProblems = _managedSessions.HasProblems ||
            _displayedEngine is { DefaultSessionStoppedByUser: false, Health: EngineHealth.Down or EngineHealth.Degraded } failed &&
            now - failed.ObservedAt <= SessionStatusTracker.MaximumAge;
        var overall = SessionStatusTracker.Overall(values, hasProblems);
        EngineStatusText = overall switch
        {
            SessionIndicatorState.Active => active == 1
                ? UiText.Get("Shell_OneSessionActive", "1 session active")
                : UiText.Get("Shell_SessionsActive", "{0} sessions active", active),
            SessionIndicatorState.Attention or SessionIndicatorState.Failed =>
                UiText.Get("Shell_SessionsAttention", "{0} sessions · needs attention", values.Length),
            SessionIndicatorState.Paused when values.Any(item => item.State == SessionIndicatorState.Removing) =>
                UiText.Get("Shell_SessionsRemoving", "{0} sessions · removing", values.Length),
            SessionIndicatorState.Paused when values.Length > 0 =>
                UiText.Get("Shell_SessionsPaused", "{0} sessions · paused", values.Length),
            _ => UiText.Get("Shell_SessionsUnconfirmed", "{0} sessions · status not confirmed", values.Length),
        };
        EngineStatusBrush = new SolidColorBrush(overall switch
        {
            SessionIndicatorState.Active => Color.FromArgb(255, 45, 200, 95),
            SessionIndicatorState.Attention => Color.FromArgb(255, 240, 180, 40),
            SessionIndicatorState.Failed => Color.FromArgb(255, 230, 70, 70),
            _ => Color.FromArgb(255, 150, 150, 150),
        });
        var lines = values.Select(item =>
        {
            var state = item.State switch
            {
                SessionIndicatorState.Active => UiText.Get("Shell_SessionActive", "Active"),
                SessionIndicatorState.Attention => UiText.Get("Shell_SessionAttention", "Needs attention"),
                SessionIndicatorState.Failed => UiText.Get("Shell_SessionFailed", "Failed"),
                SessionIndicatorState.Paused => UiText.Get("Shell_SessionPaused", "Paused"),
                SessionIndicatorState.Removing => UiText.Get("Shell_SessionRemoving", "Removing"),
                _ => UiText.Get("Shell_SessionUnknown", "Not recently checked"),
            };
            var counts = item.Running is { } running && item.Total is { } total
                ? UiText.Get("Shell_SessionCounts", "{0}/{1} containers running", running, total)
                : UiText.Get("Shell_SessionCountsUnknown", "Container count unavailable");
            var checkedAt = item.ObservedAt is { } at
                ? UiText.Get("Shell_SessionChecked", "Last checked: {0}", at.ToLocalTime().ToString("g"))
                : UiText.Get("Shell_SessionNeverChecked", "No recent observation");
            return UiText.Get("Shell_SessionDetail", "{0} · {1}\n{2} · {3}", item.Name, state, counts, checkedAt);
        }).ToList();
        if (lines.Count == 0) lines.Add(UiText.Get("Shell_SessionsEmpty", "No sessions have been observed yet."));
        lines.Add(UiText.Get("Shell_SessionsNote",
            "Active means the session responded, not that every container is healthy. Details use existing observations; opening this panel does not wake sessions."));
        SessionStatusDetails = string.Join("\n\n", lines);
    }
}
