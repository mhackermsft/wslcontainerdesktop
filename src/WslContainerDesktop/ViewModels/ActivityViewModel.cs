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
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the Activity page: exposes the persisted event timeline (optionally filtered by
/// category) plus Clear. The underlying <see cref="IActivityLog"/> is populated in the
/// background from engine snapshot diffs, so the feed stays live without this page polling.
/// </summary>
public partial class ActivityViewModel : ObservableObject
{
    private readonly IActivityLog _log;
    private readonly ActivitySessionReader _reader;
    private readonly IWindowVisibility _visibility;
    private readonly ILogger<ActivityViewModel> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<WslcSessionInfo, DateTimeOffset> _lastReads = [];
    private CancellationTokenSource? _polling;
    private IReadOnlyList<ActivitySessionResult>? _lastResults;
    private DateTimeOffset? _updatedAt;
    private Exception? _readError;
    private string? _lastReadExecutable;
    private bool _batching;
    private bool _pollStopped;
    private bool _refreshingLanguage;

    /// <summary>Bindable state for selected filter used by the view.</summary>
    [ObservableProperty]
    private string _selectedFilter = UiText.Get("Common_Text0236", "All");

    /// <summary>Bindable state for search text used by the view.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Whether paused for view binding.</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>Coverage and freshness of the most recent all-session activity read.</summary>
    public string StatusMessage
    {
        get
        {
            string status;
            if (_pollStopped)
                status = UiText.Get("Activity_PollStopped",
                    "Activity updates stopped: {0}. Select Refresh to try again.",
                    _readError is null ? string.Empty : WslcSessionInventoryErrors.Describe(_readError));
            else if (_readError is not null)
                status = UiText.Get("Activity_ReadFailed", "Activity could not be refreshed: {0}",
                    WslcSessionInventoryErrors.Describe(_readError));
            else if (_lastResults is null)
                status = UiText.Get("Activity_LoadingSessions", "Loading activity from active sessions…");
            else if (_lastResults.Count == 0)
                status = UiText.Get("Activity_NoActiveSessions", "No active sessions to read.");
            else
            {
                var failures = _lastResults.Where(result => result.Events is null).ToArray();
                status = failures.Length == 0
                    ? UiText.Get("Activity_AllSessionsUpdated", "Activity updated for {0} active session(s) at {1}.",
                        _lastResults.Count, _updatedAt?.ToLocalTime().ToString("t") ?? "—")
                    : UiText.Get("Activity_PartialSessions",
                        "Activity is incomplete for {0}: {1}",
                        string.Join(", ", failures.Select(result => result.Selection.Target.Name ??
                            UiText.Get("WslEngine_Session_Default", "Default CLI session"))),
                        string.Join("; ", failures.Select(result => result.TimedOut
                            ? UiText.Get("Activity_ReadTimedOut", "Read timed out")
                            : result.Failure is { } failure ? WslcSessionInventoryErrors.Describe(failure)
                                : UiText.Get("Activity_ReadUnavailable", "Read unavailable"))));
            }
            return IsPaused ? UiText.Get("Activity_ListPaused", "{0} · list paused", status) : status;
        }
    }

    /// <summary>Category filter chips shown above the timeline.</summary>
    public ObservableCollection<string> Filters { get; } = new() { UiText.Get("Common_Text0236", "All"), UiText.Get("Common_Text0237", "Engine"), UiText.Get("Common_Text0238", "Container"), UiText.Get("Common_Text0239", "Network"), UiText.Get("Common_Text0240", "Image") };

    /// <summary>The events matching the current filter, most-recent-first.</summary>
    public ObservableCollection<ActivityEvent> Events { get; } = new();

    /// <summary>Bindable state for has events used by the view.</summary>
    public bool HasEvents => Events.Count > 0;

    /// <summary>Creates the Activity view model and stores its injected services.</summary>
    public ActivityViewModel(IActivityLog log, ActivitySessionReader reader,
        IWindowVisibility visibility, ILogger<ActivityViewModel> logger)
    {
        _log = log;
        _reader = reader;
        _visibility = visibility;
        _logger = logger;
        UiText.LanguageChanged += (_, _) => RefreshLocalizedText();
        _log.Events.CollectionChanged += OnLogChanged;
        Rebuild();
    }

    /// <summary>Handles selected filter changed changes and updates related view-model state.</summary>
    partial void OnSelectedFilterChanged(string value)
    {
        if (!_refreshingLanguage) Rebuild();
    }
    /// <summary>Handles search text changed changes and updates related view-model state.</summary>
    partial void OnSearchTextChanged(string value) => Rebuild();
    /// <summary>Handles is paused changed changes and updates related view-model state.</summary>
    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusMessage));
        if (!value) Rebuild();
    }

    /// <summary>Handles log changed changes and updates related view-model state.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsPaused && !_batching)
        {
            Rebuild();
        }
    }

    /// <summary>Starts visible-page activity reads without starting stopped sessions.</summary>
    public void Start()
    {
        Stop();
        _polling = new CancellationTokenSource();
        _pollStopped = false;
        _readError = null;
        OnPropertyChanged(nameof(StatusMessage));
        var token = _polling.Token;
        UiSafe.Run(() => PollAsync(token));
    }

    /// <summary>Stops activity reads when the page is no longer visible.</summary>
    public void Stop()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _visibility.WhenViewableAsync(ct);
                if (_visibility.IsViewable)
                    await RefreshCoreAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Activity polling ended after the page was closed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Activity polling stopped unexpectedly.");
            _pollStopped = true;
            _readError = ex;
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    /// <summary>Refreshes activity for currently active sessions on request.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_pollStopped && _polling is not null)
        {
            Start();
            return;
        }
        if (_polling is { } polling && _visibility.IsViewable)
            await RefreshCoreAsync(polling.Token);
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        try { await _refreshGate.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        try
        {
            if (!_visibility.IsViewable) return;
            var executable = _reader.CurrentExecutablePath;
            var prior = string.Equals(executable, _lastReadExecutable, StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<WslcSessionInfo, DateTimeOffset>(_lastReads) : [];
            var batch = await _reader.ReadAsync(prior, ct);
            ct.ThrowIfCancellationRequested();
            if (!_visibility.IsViewable) return;
            if (!string.Equals(batch.ExecutablePath, _lastReadExecutable, StringComparison.OrdinalIgnoreCase))
                _lastReads.Clear();
            _lastReadExecutable = batch.ExecutablePath;
            var results = new List<ActivitySessionResult>();
            var acceptedEvents = new List<EngineEvent>();
            var completedSessions = new List<WslcSessionInfo>();
            using var verificationBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verificationBudget.CancelAfter(TimeSpan.FromSeconds(60));
            foreach (var result in batch.Sessions)
            {
                if (result.Events is null)
                {
                    results.Add(result);
                    continue;
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(verificationBudget.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await _reader.VerifyAsync(result.Selection, timeout.Token).WaitAsync(timeout.Token);
                    ct.ThrowIfCancellationRequested();
                    if (!_visibility.IsViewable) return;
                    var owner = AutomationSessionOwner.Capture(batch.ExecutablePath, result.Selection.Target);
                    foreach (var evt in result.Events)
                    {
                        evt.SessionOwner = owner;
                        acceptedEvents.Add(evt);
                    }
                    completedSessions.Add(result.Selection.Generation!);
                    results.Add(result with { Events = [] });
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    results.Add(result with { Events = null, TimedOut = true });
                }
                catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex) && !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Activity session changed before its events could be displayed.");
                    results.Add(result with { Events = null, Failure = ex });
                }
            }
            var current = batch.Sessions.Select(result => result.Selection.Generation).ToHashSet();
            if (!string.Equals(batch.ExecutablePath, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(UiText.Get("WslcSessionInventory_ExecutableChanged",
                    "The WSLC executable changed while session inventory was being read."));
            _batching = true;
            _log.RecordEngineEvents(acceptedEvents.OrderBy(evt => evt.Timestamp).ToArray());
            _batching = false;
            foreach (var previous in _lastReads.Keys.Where(session => !current.Contains(session)).ToArray())
                _lastReads.Remove(previous);
            foreach (var completed in completedSessions)
                _lastReads[completed] = batch.Until;
            _lastResults = results;
            _updatedAt = batch.Until;
            _readError = null;
            OnPropertyChanged(nameof(StatusMessage));
            if (!IsPaused) Rebuild();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Activity refresh ended after the page was closed.");
        }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Could not refresh activity across sessions.");
            _readError = ex;
            OnPropertyChanged(nameof(StatusMessage));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure refreshing activity across sessions.");
            _readError = ex;
            OnPropertyChanged(nameof(StatusMessage));
        }
        finally
        {
            _batching = false;
            _refreshGate.Release();
        }
    }

    /// <summary>Command handler for clear actions triggered from the view.</summary>
    [RelayCommand]
    private void Clear() => _log.Clear();

    /// <summary>Refreshes rebuild state for the view model.</summary>
    private void Rebuild()
    {
        Events.Clear();
        foreach (var evt in _log.Events.Where(Matches))
        {
            Events.Add(evt);
        }

        OnPropertyChanged(nameof(HasEvents));
    }

    /// <summary>Helper for the matches workflow in this view model.</summary>
    private bool Matches(ActivityEvent evt)
    {
        var categoryMatches = Filters.IndexOf(SelectedFilter) switch
        {
            1 => evt.Category == ActivityCategory.Engine,
            2 => evt.Category == ActivityCategory.Container,
            3 => evt.Category == ActivityCategory.Network,
            4 => evt.Category == ActivityCategory.Image,
            _ => true,
        };
        return categoryMatches && MatchesSearch(evt);
    }

    /// <summary>Helper for the matches search workflow in this view model.</summary>
    private bool MatchesSearch(ActivityEvent evt)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var needle = SearchText.Trim();
        return Contains(evt.Title, needle) || Contains(evt.DisplayTitle, needle) ||
            Contains(evt.Detail, needle) || Contains(evt.DisplayDetail, needle) ||
            Contains(evt.ActorId, needle) ||
            Contains(evt.ContainerId, needle) ||
            Contains(evt.SessionDisplay, needle) ||
            evt.Attributes.Any(kvp => Contains(kvp.Key, needle) || Contains(kvp.Value, needle));
    }

    /// <summary>Helper for the contains workflow in this view model.</summary>
    private static bool Contains(string? value, string needle) =>
        value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Preserves the selected category and paused state when labels change.</summary>
    private void RefreshLocalizedText()
    {
        var index = Math.Max(0, Filters.IndexOf(SelectedFilter));
        var translated = new[] { UiText.Get("Common_Text0236", "All"), UiText.Get("Common_Text0237", "Engine"),
            UiText.Get("Common_Text0238", "Container"), UiText.Get("Common_Text0239", "Network"), UiText.Get("Common_Text0240", "Image") };
        _refreshingLanguage = true;
        try
        {
            for (var i = 0; i < translated.Length; i++)
                Filters[i] = translated[i];
            SelectedFilter = Filters[index];
        }
        finally
        {
            _refreshingLanguage = false;
        }
        OnPropertyChanged(nameof(StatusMessage));
        if (!IsPaused)
            Rebuild();
        else
        {
            var visible = Events.ToArray();
            Events.Clear();
            foreach (var entry in visible) Events.Add(entry);
        }
    }
}
