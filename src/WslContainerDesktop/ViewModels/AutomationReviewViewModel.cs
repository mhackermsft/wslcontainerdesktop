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

using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
#if !WSLCD_TEST_NO_DISPATCHER
using Microsoft.UI.Dispatching;
#endif
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.ViewModels;

/// <summary>Surfaces paused automatic actions and lets a person assign their owning session.</summary>
public sealed partial class AutomationReviewViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly LegacyPolicyOwnershipStore _legacyOwners;
    private readonly AutostartStateStore _autostartStore;
    private readonly ContainerAutostartService _autostart;
    private readonly WslcSessionInventoryReader _reader;
    private readonly WslcSessionContext _sessionContext;
    private readonly INotificationService _notifications;
    private readonly DialogService _dialogs;
    private readonly StatusMonitor _monitor;
    private readonly ILogger<AutomationReviewViewModel> _logger;
    private readonly IComposeProjectStore _composeStore;
    private readonly ComposeProjectSupervisor _compose;
    private readonly SessionAutomationCoordinator? _sessionAutomation;
    private readonly SessionRemovalProgress? _removal;
    private readonly SessionRemovalGuard? _removalGuard;
    private readonly SessionStopProgress? _stopProgress;
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private HashSet<string>? _issuesBeforeRemoval;
    private HashSet<string>? _issuesBeforeStop;
    private readonly CancellationTokenSource _shutdown = new();
#if !WSLCD_TEST_NO_DISPATCHER
    private readonly DispatcherQueue _dispatcher;
#endif
    private bool _started;

    /// <summary>Creates the review model without starting discovery or automatic actions.</summary>
    public AutomationReviewViewModel(ISettingsService settings, LegacyPolicyOwnershipStore legacyOwners,
        AutostartStateStore autostartStore, ContainerAutostartService autostart,
        WslcSessionInventoryReader reader, WslcSessionContext sessionContext,
        INotificationService notifications, DialogService dialogs, StatusMonitor monitor,
        ILogger<AutomationReviewViewModel> logger, IComposeProjectStore composeStore, ComposeProjectSupervisor compose,
        SessionAutomationCoordinator? sessionAutomation = null, SessionRemovalProgress? removal = null,
        SessionRemovalGuard? removalGuard = null, SessionStopProgress? stopProgress = null)
    {
        _settings = settings;
        _legacyOwners = legacyOwners;
        _autostartStore = autostartStore;
        _autostart = autostart;
        _reader = reader;
        _sessionContext = sessionContext;
        _notifications = notifications;
        _dialogs = dialogs;
        _monitor = monitor;
        _logger = logger;
        _composeStore = composeStore;
        _compose = compose;
        _sessionAutomation = sessionAutomation;
        _removal = removal;
        _removalGuard = removalGuard;
        _stopProgress = stopProgress;
#if !WSLCD_TEST_NO_DISPATCHER
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("AutomationReviewViewModel must be created on the UI thread.");
#endif
    }

    /// <summary>Saved actions that have not yet been assigned to a session.</summary>
    public ObservableCollection<AutomationReviewItem> PendingAssignments { get; } = [];
    /// <summary>Whether legacy automatic actions still need a session choice.</summary>
    public bool HasPendingAssignments => PendingAssignments.Count > 0;
    /// <summary>Other reasons an expected automatic action cannot currently run.</summary>
    public ObservableCollection<string> Issues { get; } = [];
    /// <summary>Unavailable tracking owners that can be explicitly forgotten.</summary>
    public ObservableCollection<AutomationTrackingIssue> UnavailableTracking { get; } = [];
    public bool HasUnavailableTracking => UnavailableTracking.Count > 0;

    /// <summary>True when a session assignment or retry is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isBusy;

    /// <summary>Whether assignment controls are available.</summary>
    public bool CanEdit => !IsBusy && _removal?.IsBusy != true && _stopProgress?.IsBusy != true;

    /// <summary>Whether the shell must show a persistent attention banner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttentionMessage))]
    [NotifyPropertyChangedFor(nameof(NotificationsDisabled))]
    private bool _needsAttention;

    /// <summary>Whether notifications are disabled while attention is required.</summary>
    public bool NotificationsDisabled => NeedsAttention && !_notifications.CanShowNotifications;

    /// <summary>Localized summary used by the shell and review page.</summary>
    public string AttentionMessage => PendingAssignments.Count > 0
        ? UiText.Get("Automation_PendingSummary",
            "{0} saved automatic action(s) need a session before they can run.", PendingAssignments.Count)
        : UiText.Get("Automation_WaitingSummary", "Some automatic actions are waiting or need attention.");

    /// <summary>Begins monitoring saved settings and sends an actionable Windows notification.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _settings.Changed += OnChanged;
        _legacyOwners.Changed += OnChanged;
        _autostartStore.Changed += OnChanged;
        _autostart.ProblemsChanged += OnChanged;
        _monitor.StatusChanged += OnMonitorChanged;
        _composeStore.Changed += OnChanged;
        _compose.ReconciliationChanged += OnChanged;
        if (_sessionAutomation is not null)
            _sessionAutomation.Changed += OnChanged;
        if (_removal is not null)
            _removal.PropertyChanged += OnRemovalChanged;
        if (_stopProgress is not null)
            _stopProgress.PropertyChanged += OnStopChanged;
        _sessionContext.SelectionChanged += OnSelectionChanged;
        _sessionContext.EngineSettingChanged += OnChanged;
        _sessionContext.DefaultSessionStopChanged += OnDefaultSessionStopChanged;
        UiText.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    /// <summary>Assigns one saved action after confirming the destination and current session availability.</summary>
    public async Task AssignAsync(AutomationReviewItem item, WslcSessionChoice? choice)
    {
        if (!CanEdit)
            return;
        if (choice is null)
        {
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("Automation_ChooseSessionTitle", "Choose a session"),
                UiText.Get("Automation_ChooseSession", "Choose a session before assigning this action."));
            return;
        }
        using var selection = _sessionContext.LockSelection();
        IsBusy = true;
        try
        {
            if (!PendingAssignments.Contains(item))
                throw new InvalidOperationException(UiText.Get("Automation_EntryChanged",
                    "This setting changed. Refresh before assigning a session."));
            var owner = await CaptureAvailableOwnerAsync(choice);
            var confirmed = await _dialogs.ShowLocalizedConfirmAsync(
                UiText.Get("Automation_AssignTitle", "Assign an automatic action"),
                UiText.Get("Automation_AssignConfirmation",
                    "Assign {0} for \"{1}\" to \"{2}\"? This may start or restart containers in that session.",
                    item.KindText, item.Name, SessionLabel(owner)),
                UiText.Get("Automation_AssignButton", "Assign"),
                UiText.Get("Workload_Text_Cancel_19766e", "Cancel"));
            if (!confirmed)
                return;
            if (_removal?.IsBusy == true)
                throw new InvalidOperationException(UiText.Get("ManagedSession_OperationInProgress",
                    "A session action is already in progress."));
            switch (item.Kind)
            {
                case AutomationReviewItem.ItemKind.Health when item.Value is HealthCheckConfig health:
                    if (!_settings.HealthChecks.Contains(health) || _legacyOwners.OwnerFor(health) is not null)
                        throw ChangedEntry();
                    _legacyOwners.Assign(health, owner);
                    break;
                case AutomationReviewItem.ItemKind.Restart when item.Value is RestartPolicyConfig restart:
                    if (!_settings.RestartPolicies.Contains(restart) || _legacyOwners.OwnerFor(restart) is not null)
                        throw ChangedEntry();
                    _legacyOwners.Assign(restart, owner);
                    break;
                case AutomationReviewItem.ItemKind.Autostart when item.Value is AutostartEntry entry:
                    _autostartStore.AssignLegacy(entry, owner);
                    await _autostart.ResumeAssignedAsync(entry, owner);
                    break;
                case AutomationReviewItem.ItemKind.Compose when item.Value is ComposeProject project:
                    if (!ReferenceEquals(_composeStore.Get(project.Name), project) || project.SessionOwner?.IsValid == true)
                        throw ChangedEntry();
                    var assignedProject = JsonSerializer.Deserialize<ComposeProject>(JsonSerializer.Serialize(project))
                        ?? throw ChangedEntry();
                    assignedProject.SessionOwner = owner;
                    _composeStore.Save(assignedProject);
                    if (!_settings.HasLoadError && _sessionAutomation is not null)
                        _sessionAutomation.RequestReconciliation(owner);
                    else if (!_settings.HasLoadError && owner.Matches(_settings.WslcPath, _sessionContext.Capture()))
                        await _compose.ReconcileAsync();
                    Refresh();
                    break;
                default:
                    throw ChangedEntry();
            }
            _monitor.RequestRefresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not assign an automatic action to a session.");
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("Automation_AssignmentFailedTitle", "Could not save the session choice"),
                UiText.Get("Automation_AssignmentFailed", "The action is still paused. {0}", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Retries saved automatic-start work that is waiting for its session.</summary>
    public async Task RetryAsync()
    {
        if (!CanEdit)
            return;
        IsBusy = true;
        try
        {
            await _autostart.RetryPendingAsync();
            _monitor.RequestRefresh();
            _monitor.RequestAutomationRefresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not retry automatic actions.");
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("Automation_RetryFailedTitle", "Could not retry automatic actions"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Forgets only the selected owner's automatic-start history after confirmation.</summary>
    public async Task ForgetTrackingAsync(AutomationTrackingIssue item)
    {
        if (!CanEdit || !UnavailableTracking.Contains(item))
            return;
        IsBusy = true;
        try
        {
            var confirmed = await _dialogs.ShowLocalizedConfirmAsync(
                UiText.Get("Automation_ForgetTrackingTitle", "Forget automatic-start tracking"),
                UiText.Get("Automation_ForgetTrackingConfirmation",
                    "Forget automatic-start tracking for {0}? Its remembered restart history will be removed. Container data and other settings will be kept. If the session becomes active again, automatic-start tracking may resume.",
                    item.DisplayName),
                UiText.Get("Automation_ForgetTrackingButton", "Forget tracking"),
                UiText.Get("Workload_Text_Cancel_19766e", "Cancel"));
            if (!confirmed)
                return;
            if (!UnavailableTracking.Contains(item) || !_autostartStore.Snapshot()
                    .Any(entry => AutostartStateStore.SameOwner(entry.SessionOwner, item.Owner)))
                throw ChangedEntry();
            await _autostart.ForgetOwnerAsync(item.Owner);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not forget automatic-start tracking.");
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("Automation_ForgetTrackingFailed", "Could not forget automatic-start tracking"), ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task<AutomationSessionOwner> CaptureAvailableOwnerAsync(WslcSessionChoice choice)
    {
        if (string.IsNullOrWhiteSpace(_settings.WslcPath))
            throw new InvalidOperationException(UiText.Get("Automation_InvalidOwner",
                "Choose a valid engine and session before continuing."));
        if (choice.Name is not null)
        {
            var sessions = await _reader.DiscoverAsync();
            if (!sessions.Any(session => session.ID > 0 && session.CreatorPid > 0 &&
                string.Equals(session.Name, choice.Name, StringComparison.Ordinal)))
                throw new InvalidOperationException(UiText.Get("Automation_SessionUnavailable",
                    "That session is no longer available. Refresh the session list and try again."));
        }
        return AutomationSessionOwner.Capture(_settings.WslcPath, new WslcSessionTarget(choice.Name));
    }

    private void OnChanged(object? sender, EventArgs args) => Dispatch(Refresh);

    private void OnRemovalChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SessionRemovalProgress.IsBusy) or null or "")
            Dispatch(() =>
            {
                if (_removal?.IsBusy == true)
                    _issuesBeforeRemoval ??= IssueKeys().ToHashSet(StringComparer.Ordinal);
                else
                    _issuesBeforeRemoval = null;
                OnPropertyChanged(nameof(CanEdit));
                Refresh();
            });
    }

    private void OnSelectionChanged(object? sender, EventArgs args)
    {
        Dispatch(Refresh);
    }

    private void OnStopChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SessionStopProgress.IsBusy) or null or "")
            Dispatch(() =>
            {
                if (_stopProgress?.IsStoppingDefault == true)
                    _issuesBeforeStop ??= IssueKeys().ToHashSet(StringComparer.Ordinal);
                else if (_stopProgress?.IsBusy != true &&
                         !_sessionContext.IsDefaultSessionStopped(_settings.WslcPath, WslcSessionTarget.Default))
                    _issuesBeforeStop = null;
                OnPropertyChanged(nameof(CanEdit));
                Refresh();
            });
    }

    private void OnDefaultSessionStopChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        if (!_sessionContext.IsDefaultSessionStopped(_settings.WslcPath, WslcSessionTarget.Default) &&
            _stopProgress?.IsBusy != true)
            _issuesBeforeStop = null;
        Refresh();
    });

    private void OnMonitorChanged(object? sender, EngineStatusSnapshot args) => Dispatch(Refresh);

    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatch(() =>
    {
        foreach (var item in PendingAssignments)
            item.RefreshLanguage();
        foreach (var item in UnavailableTracking)
            item.RefreshLanguage();
        RefreshIssues();
        OnPropertyChanged(nameof(AttentionMessage));
    });

    private void Refresh()
    {
        var items = BuildPendingAssignments();
        if (PendingAssignments.Count == items.Count &&
            PendingAssignments.Select(item => item.Key).SequenceEqual(items.Select(item => item.Key), StringComparer.Ordinal))
        {
            for (var index = 0; index < items.Count; index++)
                PendingAssignments[index].UpdateSource(items[index].Value);
        }
        else
        {
            PendingAssignments.Clear();
            foreach (var item in items)
                PendingAssignments.Add(item);
        }
        RefreshIssues();
        NeedsAttention = PendingAssignments.Count > 0 || Issues.Count > 0;
        OnPropertyChanged(nameof(HasPendingAssignments));
        OnPropertyChanged(nameof(AttentionMessage));
        OnPropertyChanged(nameof(NotificationsDisabled));

        var current = items.Select(item => item.Key).Concat(IssueKeys()).ToHashSet(StringComparer.Ordinal);
        _notified.IntersectWith(current);
        if (_notifications.CanShowNotifications)
        {
            if (current.Except(_notified).Any())
                _notifications.NotifyAutomationActionRequired();
            _notified.UnionWith(current);
        }
    }

    private List<AutomationReviewItem> BuildPendingAssignments()
    {
        var items = new List<AutomationReviewItem>();
        foreach (var health in _settings.HealthChecks.Where(policy => policy.IsValid && _legacyOwners.OwnerFor(policy) is null))
            items.Add(new(AutomationReviewItem.ItemKind.Health, "health:" + LegacyPolicyOwnershipStore.HealthKey(health),
                health.ContainerName, health));
        foreach (var restart in _settings.RestartPolicies.Where(policy => policy.IsValid && _legacyOwners.OwnerFor(policy) is null))
            items.Add(new(AutomationReviewItem.ItemKind.Restart, "restart:" + LegacyPolicyOwnershipStore.RestartKey(restart),
                restart.ContainerName, restart));
        foreach (var entry in _autostartStore.Snapshot().Where(entry => !entry.TracksSession && entry.SessionOwner?.IsValid != true))
            items.Add(new(AutomationReviewItem.ItemKind.Autostart,
                "autostart:" + JsonSerializer.Serialize(new[] { entry.Id, entry.Name }),
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name, entry));
        foreach (var project in _composeStore.GetAll().Where(project => project.SessionOwner?.IsValid != true))
            items.Add(new(AutomationReviewItem.ItemKind.Compose, "compose:" + project.Name.ToUpperInvariant(), project.Name, project));
        return items.DistinctBy(item => item.Key, StringComparer.Ordinal).ToList();
    }

    private IEnumerable<string> IssueKeys()
    {
        if (_settings.HasLoadError) yield return "settings-load-error";
        if (_settings.HasSaveError) yield return "settings-save-error";
        if (_legacyOwners.HasLoadError) yield return "policy-store-error";
        if (_autostartStore.HasLoadError) yield return "autostart-store-error";
        if (AutostartDisabled()) yield return "autostart-disabled";
        if (CurrentPoliciesUnavailable()) yield return "current-policies-unavailable";
        if (CurrentTrackingUnavailable()) yield return "current-tracking-unavailable";
        foreach (var owner in InactivePolicyOwners())
            yield return OwnerIssueKey("inactive-policy:", owner);
        foreach (var owner in InactiveTrackingOwners())
            yield return OwnerIssueKey("inactive-tracking:", owner);
        foreach (var owner in InactiveComposeOwners())
            yield return OwnerIssueKey("inactive-compose:", owner);
        foreach (var warning in _compose.ReconciliationWarnings)
            yield return "compose-warning:" + warning;
        if (_sessionAutomation?.Error is not null) yield return "other-sessions-observation-error";
        foreach (var problem in _autostart.Problems.Where(ShowAutostartProblem))
            yield return AutostartProblemKey(problem);
    }

    private void RefreshIssues()
    {
        var trackingOwners = InactiveTrackingOwners().ToList();
        if (CurrentTrackingUnavailable())
            trackingOwners.Add(AutomationSessionOwner.Capture(_settings.WslcPath, _sessionContext.Capture()));
        var saved = _autostartStore.Snapshot();
        foreach (var problem in _autostart.Problems.Where(problem =>
                     ShowAutostartProblem(problem) &&
                     problem.Kind != ContainerAutostartService.ProblemKind.StorageFailed &&
                     saved.Any(entry => AutostartStateStore.SameOwner(entry.SessionOwner, problem.Owner))))
            if (!trackingOwners.Any(owner => AutostartStateStore.SameOwner(owner, problem.Owner)))
                trackingOwners.Add(problem.Owner);
        if (UnavailableTracking.Count != trackingOwners.Count ||
            !UnavailableTracking.Select(item => item.Owner).Zip(trackingOwners,
                AutostartStateStore.SameOwner).All(same => same))
        {
            UnavailableTracking.Clear();
            foreach (var owner in trackingOwners)
                UnavailableTracking.Add(new AutomationTrackingIssue(owner));
            OnPropertyChanged(nameof(HasUnavailableTracking));
        }
        var issues = new List<string>();
        if (_settings.HasLoadError)
            issues.Add(UiText.Get("Automation_SettingsReadFailed",
                "Saved settings are unreadable or incomplete. Automatic actions are paused and changes cannot be saved. Repair or restore settings.json and restart the app."));
        if (_settings.HasSaveError)
            issues.Add(UiText.Get("Automation_SettingsSaveFailed",
                "Settings could not be saved. Recent changes may be lost when the app closes."));
        if (_legacyOwners.HasLoadError)
            issues.Add(UiText.Get("Automation_OwnershipReadFailed", "Saved session assignments could not be read. They were not changed."));
        if (_autostartStore.HasLoadError)
            issues.Add(UiText.Get("Automation_AutostartReadFailed", "Saved automatic-start history could not be read. It was not changed."));
        if (AutostartDisabled())
            issues.Add(UiText.Get("Automation_AutostartDisabled",
                "Automatic start is turned off in Settings. Assigned containers will not start automatically."));
        if (CurrentPoliciesUnavailable())
            issues.Add(UiText.Get("Automation_CurrentPoliciesUnavailable",
                "Health and restart actions are waiting because the selected session or engine is unavailable."));
        if (CurrentTrackingUnavailable())
            issues.Add(UiText.Get("Automation_CurrentTrackingUnavailable",
                "Automatic-start tracking is waiting because the selected session or engine is unavailable."));
        foreach (var owner in InactivePolicyOwners())
            issues.Add(UiText.Get("Automation_InactivePolicy",
                "Health and restart actions for {0} ({1}) are waiting because that session or engine is unavailable.",
                SessionLabel(owner), owner.ExecutablePath));
        foreach (var owner in InactiveTrackingOwners())
            issues.Add(UiText.Get("Automation_InactiveTracking",
                "Automatic-start tracking for {0} ({1}) is waiting because that session or engine is unavailable.",
                SessionLabel(owner), owner.ExecutablePath));
        foreach (var owner in InactiveComposeOwners())
            issues.Add(UiText.Get("Automation_InactiveCompose",
                "Compose recovery for {0} ({1}) is waiting because that session or engine is unavailable.",
                SessionLabel(owner), owner.ExecutablePath));
        foreach (var warning in _compose.ReconciliationWarnings)
            issues.Add(UiText.Get("Automation_ComposeWarning", "Compose recovery needs attention: {0}", warning));
        if (_sessionAutomation?.Error is { } observationError)
            issues.Add(UiText.Get("Automation_OtherSessionsUnavailable",
                "Automatic actions in other sessions cannot be observed: {0}", observationError));
        foreach (var problem in _autostart.Problems.Where(ShowAutostartProblem))
            issues.Add(DescribeProblem(problem));
        if (!Issues.SequenceEqual(issues, StringComparer.Ordinal))
        {
            Issues.Clear();
            foreach (var issue in issues)
                Issues.Add(issue);
        }
    }

    private bool AutostartDisabled() => !_settings.RestartRunningContainersOnLaunch &&
        _autostartStore.Snapshot().Any(entry => entry.SessionOwner?.IsValid == true);

    private bool CurrentPoliciesUnavailable()
    {
        if (_monitor.Latest?.Health != EngineHealth.Down)
            return false;
        var target = _sessionContext.Capture();
        if (SuppressExpectedCurrentWarning("current-policies-unavailable"))
            return false;
        return _settings.HealthChecks.Any(policy => policy.Enabled && policy.IsValid &&
                   _legacyOwners.OwnerFor(policy)?.Matches(_settings.WslcPath, target) == true) ||
               _settings.RestartPolicies.Any(policy => policy.Enabled && policy.IsValid &&
                   _legacyOwners.OwnerFor(policy)?.Matches(_settings.WslcPath, target) == true);
    }

    private bool CurrentTrackingUnavailable() =>
        _monitor.Latest?.Health == EngineHealth.Down &&
        !SuppressExpectedCurrentWarning("current-tracking-unavailable") &&
        _autostartStore.Snapshot().Any(entry => entry.TracksSession &&
            entry.SessionOwner?.Matches(_settings.WslcPath, _sessionContext.Capture()) == true);

    private IReadOnlyList<AutomationSessionOwner> InactiveTrackingOwners()
    {
        var target = _sessionContext.Capture();
        var distinct = new List<AutomationSessionOwner>();
        foreach (var owner in _autostartStore.Snapshot().Where(entry => entry.TracksSession)
                     .Select(entry => entry.SessionOwner)
                     .Where(owner => owner?.IsValid == true && !owner.Matches(_settings.WslcPath, target) &&
                         !SuppressExpectedOwnerWarning(owner, "inactive-tracking:") &&
                         _sessionAutomation?.IsObserved(owner) != true &&
                         _sessionAutomation?.HasFreshObservationInProgress(owner) != true))
            if (!distinct.Any(existing => AutostartStateStore.SameOwner(existing, owner)))
                distinct.Add(owner!);
        return distinct;
    }

    private IReadOnlyList<AutomationSessionOwner> InactiveComposeOwners()
    {
        var target = _sessionContext.Capture();
        var distinct = new List<AutomationSessionOwner>();
        foreach (var owner in _composeStore.GetAll().Select(project => project.SessionOwner)
                     .Where(owner => owner?.IsValid == true && !owner.Matches(_settings.WslcPath, target) &&
                         !SuppressExpectedOwnerWarning(owner, "inactive-compose:") &&
                         _sessionAutomation?.IsObserved(owner) != true))
            if (!distinct.Any(existing => AutostartStateStore.SameOwner(existing, owner)))
                distinct.Add(owner!);
        return distinct;
    }

    private IReadOnlyList<AutomationSessionOwner> InactivePolicyOwners()
    {
        var selected = _sessionContext.Capture();
        var owners = _settings.HealthChecks.Where(policy => policy.Enabled && policy.IsValid)
            .Select(_legacyOwners.OwnerFor)
            .Concat(_settings.RestartPolicies.Where(policy => policy.Enabled && policy.IsValid).Select(_legacyOwners.OwnerFor))
            .Where(owner => owner?.IsValid == true && !owner.Matches(_settings.WslcPath, selected) &&
                !SuppressExpectedOwnerWarning(owner, "inactive-policy:") &&
                _sessionAutomation?.IsObserved(owner) != true)
            .Select(owner => owner!).ToList();
        var distinct = new List<AutomationSessionOwner>();
        foreach (var owner in owners)
            if (!distinct.Any(existing => AutostartStateStore.SameOwner(existing, owner)))
                distinct.Add(owner);
        return distinct;
    }

    private bool IsOwnerRemoving(AutomationSessionOwner? owner) =>
        owner?.IsValid == true && _removalGuard?.IsRemovalInProgress(owner) == true;

    private bool SuppressExpectedCurrentWarning(string key) => !string.IsNullOrWhiteSpace(_settings.WslcPath) &&
        SuppressExpectedWarning(AutomationSessionOwner.Capture(_settings.WslcPath, _sessionContext.Capture()), key);

    private bool SuppressExpectedOwnerWarning(AutomationSessionOwner? owner, string prefix) =>
        owner is not null && SuppressExpectedWarning(owner, OwnerIssueKey(prefix, owner));

    private bool SuppressExpectedWarning(AutomationSessionOwner owner, string key) =>
        _removalGuard?.IsIntentionallyStopped(owner) == true ||
        _removalGuard?.IsResuming(owner) == true ||
        IsOwnerRemoving(owner) && _issuesBeforeRemoval?.Contains(key) != true ||
        _issuesBeforeStop is not null &&
        _sessionContext.IsDefaultSessionStopped(owner.ExecutablePath, new WslcSessionTarget(owner.SessionName)) &&
        !WasPresentBeforeStop(key);

    private bool WasPresentBeforeStop(string key) =>
        _issuesBeforeStop?.Contains(key) == true ||
        key.StartsWith("inactive-tracking:", StringComparison.Ordinal) &&
            _issuesBeforeStop?.Contains("current-tracking-unavailable") == true ||
        key.StartsWith("inactive-policy:", StringComparison.Ordinal) &&
            _issuesBeforeStop?.Contains("current-policies-unavailable") == true;

    private static string OwnerIssueKey(string prefix, AutomationSessionOwner owner) =>
        prefix + JsonSerializer.Serialize(new[] { owner.ExecutablePath, owner.SessionName });

    private static string AutostartProblemKey(ContainerAutostartService.AutostartProblem problem) =>
        OwnerIssueKey("autostart-problem:" + problem.Kind + ":", problem.Owner);

    private bool ShowAutostartProblem(ContainerAutostartService.AutostartProblem problem) =>
        problem.Kind != ContainerAutostartService.ProblemKind.SessionUnavailable ||
        !SuppressExpectedWarning(problem.Owner, AutostartProblemKey(problem));

    private static string DescribeProblem(ContainerAutostartService.AutostartProblem problem)
    {
        var session = SessionLabel(problem.Owner);
        return problem.Kind switch
        {
            ContainerAutostartService.ProblemKind.EngineChanged =>
                UiText.Get("Automation_EngineChanged", "Automatic start for {0} is waiting because its engine path changed.", session),
            ContainerAutostartService.ProblemKind.SessionUnavailable =>
                UiText.Get("Automation_WaitingSession", "Automatic start for {0} is waiting for that session to become available.", session),
            ContainerAutostartService.ProblemKind.EngineUnavailable =>
                UiText.Get("Automation_WaitingEngine", "Automatic start for {0} is waiting for its engine.", session),
            ContainerAutostartService.ProblemKind.ContainerMissing =>
                UiText.Get("Automation_MissingContainers", "Automatic start for {0} could not verify the saved container ID for: {1}. Review its tracking before starting a replacement.", session, problem.Detail ?? ""),
            ContainerAutostartService.ProblemKind.NotStartable =>
                UiText.Get("Automation_NotStartable", "Automatic start for {0} is waiting because these containers cannot be started in their current state: {1}", session, problem.Detail ?? ""),
            ContainerAutostartService.ProblemKind.StartFailed =>
                UiText.Get("Automation_StartFailed", "Automatic start for {0} could not finish: {1}", session, problem.Detail ?? ""),
            ContainerAutostartService.ProblemKind.StorageFailed =>
                UiText.Get("Automation_StorageFailed", "Automatic-start history could not be saved: {0}", problem.Detail ?? ""),
            _ => throw new InvalidOperationException("Unknown automatic-start problem."),
        };
    }

    private static string SessionLabel(AutomationSessionOwner owner) =>
        owner.SessionName ?? UiText.Get("WslEngine_Session_Default", "Default CLI session");

    private static InvalidOperationException ChangedEntry() =>
        new(UiText.Get("Automation_EntryChanged", "This setting changed. Refresh before assigning a session."));

    private void Dispatch(Action action)
    {
#if WSLCD_TEST_NO_DISPATCHER
        action();
#else
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
#endif
    }

    /// <summary>Detaches subscriptions owned by the application singleton.</summary>
    public void Dispose()
    {
        if (!_started)
            return;
        _settings.Changed -= OnChanged;
        _legacyOwners.Changed -= OnChanged;
        _autostartStore.Changed -= OnChanged;
        _autostart.ProblemsChanged -= OnChanged;
        _monitor.StatusChanged -= OnMonitorChanged;
        _composeStore.Changed -= OnChanged;
        _compose.ReconciliationChanged -= OnChanged;
        if (_sessionAutomation is not null)
            _sessionAutomation.Changed -= OnChanged;
        if (_removal is not null)
            _removal.PropertyChanged -= OnRemovalChanged;
        if (_stopProgress is not null)
            _stopProgress.PropertyChanged -= OnStopChanged;
        _sessionContext.SelectionChanged -= OnSelectionChanged;
        _sessionContext.EngineSettingChanged -= OnChanged;
        _sessionContext.DefaultSessionStopChanged -= OnDefaultSessionStopChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
        _shutdown.Cancel();
        _started = false;
    }
}
