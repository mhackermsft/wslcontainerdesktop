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
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
#if !WSLCD_TEST_NO_DISPATCHER
using Microsoft.UI.Dispatching;
#endif
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Maintains an explicitly refreshed, session-qualified resource view.</summary>
public sealed partial class CombinedSessionViewModel : ObservableObject, IDisposable
{
    private readonly WslcSessionInventoryReader _reader;
    private readonly ILogger<CombinedSessionViewModel> _logger;
    private readonly WslcSessionContext? _sessionContext;
#if !WSLCD_TEST_NO_DISPATCHER
    private readonly DispatcherQueue _dispatcher;
#endif
    private readonly List<WslcSessionInventory> _loaded = [];
    private CancellationTokenSource? _refresh;
    private sealed class AllRefresh
    {
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task Task { get; set; } = Task.CompletedTask;
        internal int Consumers { get; set; }
        internal bool ContainersOnly { get; init; }
    }

    private AllRefresh? _allRefresh;
    private DateTimeOffset? _lastFullRefresh;
    private TaskCompletionSource? _refreshFinished;
    private bool _hasDiscovered;
    private bool _hasLoaded;
    private bool _cancelled;
    private bool _updatingSelection;
    private Exception? _error;
    private string? _discoveredExecutable;
    private string? _loadedExecutable;
    private IReadOnlyList<CombinedSessionRow> _containerRows = [];
    private IReadOnlyList<CombinedSessionRow> _imageRows = [];
    private IReadOnlyList<CombinedSessionRow> _volumeRows = [];
    private IReadOnlyList<CombinedSessionRow> _networkRows = [];
    private IReadOnlyList<CombinedSessionRow> _endpointRows = [];

    /// <summary>Creates a combined view without starting discovery or background polling.</summary>
    public CombinedSessionViewModel(WslcSessionInventoryReader reader, ILogger<CombinedSessionViewModel> logger,
        WslcSessionContext? sessionContext = null)
    {
        _reader = reader;
        _logger = logger;
        _sessionContext = sessionContext;
#if !WSLCD_TEST_NO_DISPATCHER
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("CombinedSessionViewModel must be created on the UI thread.");
#endif
        if (_sessionContext is not null)
            _sessionContext.EngineSettingChanged += OnEngineSettingChanged;
        UiText.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Discovered sessions and their display selections.</summary>
    public ObservableCollection<CombinedSessionChoice> Sessions { get; } = [];

    internal string CurrentExecutablePath => _reader.CurrentExecutablePath;
    internal bool HasRefreshFailure => _error is not null || _cancelled;
    internal bool HasSuccessfulDiscovery => _hasDiscovered && !HasRefreshFailure;
    internal DateTimeOffset? LastDiscoveryAt { get; private set; }
    internal IReadOnlyList<WslcSessionInventory> SessionObservations => _loaded;
    internal bool HasContainerData => HasSuccessfulPart(item => item.Containers.IsComplete);
    internal bool HasImageData => HasSuccessfulPart(item => item.Images.IsComplete);
    internal bool HasVolumeData => HasSuccessfulPart(item => item.Volumes.IsComplete);
    internal bool HasNetworkData => HasSuccessfulPart(item => item.Networks.IsComplete);
    internal bool HasCompleteContainerInventory => HasSuccessfulDiscovery &&
        Sessions.Where(choice => choice.IsSelected).All(choice => choice.IsAvailable &&
            _loaded.Any(item => item.Session == choice.Session && !item.SessionChanged && item.Containers.IsComplete));
    private bool HasSuccessfulPart(Func<WslcSessionInventory, bool> predicate)
    {
        var selected = Sessions.Where(choice => choice.IsSelected && choice.IsAvailable)
            .Select(choice => choice.Session).ToHashSet();
        return HasSuccessfulDiscovery && (selected.Count == 0 ||
            _loaded.Any(item => selected.Contains(item.Session) && !item.SessionChanged && predicate(item)));
    }
    internal DateTimeOffset? OldestRead
    {
        get
        {
            var selected = Sessions.Where(choice => choice.IsSelected).Select(choice => choice.Session).ToHashSet();
            return _loaded.Where(item => selected.Contains(item.Session) && item.ObservedAt is not null)
                .Select(item => item.ObservedAt).Min();
        }
    }

    /// <summary>Currently displayed resource rows and their owner identities.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(WarningText))]
    [NotifyPropertyChangedFor(nameof(ContainerRows))]
    [NotifyPropertyChangedFor(nameof(EndpointRows))]
    private WslcCombinedInventory _inventory = WslcCombinedInventory.Create([]);

    /// <summary>Container rows retaining their originating session.</summary>
    public IReadOnlyList<CombinedSessionRow> ContainerRows => _containerRows;
    /// <summary>Image rows retaining their originating session.</summary>
    public IReadOnlyList<CombinedSessionRow> ImageRows => _imageRows;
    /// <summary>Volume rows retaining their originating session.</summary>
    public IReadOnlyList<CombinedSessionRow> VolumeRows => _volumeRows;
    /// <summary>Network rows retaining their originating session.</summary>
    public IReadOnlyList<CombinedSessionRow> NetworkRows => _networkRows;
    /// <summary>Endpoint rows retaining their originating session.</summary>
    public IReadOnlyList<CombinedSessionRow> EndpointRows => _endpointRows;

    /// <summary>Whether a row still belongs to the displayed inventory from the current executable.</summary>
    public bool IsCurrentRow(CombinedSessionRow row) =>
        _loadedExecutable is not null &&
        string.Equals(_loadedExecutable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        _containerRows.Concat(_imageRows).Concat(_volumeRows).Concat(_networkRows).Concat(_endpointRows)
            .Any(current => ReferenceEquals(current, row));

    partial void OnInventoryChanged(WslcCombinedInventory? oldValue, WslcCombinedInventory newValue)
    {
        _containerRows = newValue.Containers.Select(CombinedSessionRow.FromContainer).ToArray();
        if (!ReferenceEquals(oldValue?.Images, newValue.Images))
        {
            _imageRows = newValue.Images.Select(CombinedSessionRow.FromImage).ToArray();
            OnPropertyChanged(nameof(ImageRows));
        }
        if (!ReferenceEquals(oldValue?.Volumes, newValue.Volumes))
        {
            _volumeRows = newValue.Volumes.Select(CombinedSessionRow.FromVolume).ToArray();
            OnPropertyChanged(nameof(VolumeRows));
        }
        if (!ReferenceEquals(oldValue?.Networks, newValue.Networks))
        {
            _networkRows = newValue.Networks.Select(CombinedSessionRow.FromNetwork).ToArray();
            OnPropertyChanged(nameof(NetworkRows));
        }
        _endpointRows = newValue.Endpoints.Select(CombinedSessionRow.FromEndpoint).ToArray();
    }

    /// <summary>True while discovery or inventory loading is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isBusy;

    /// <summary>Whether session discovery and explicit inventory refresh are available.</summary>
    public bool CanRefresh => !IsBusy;

    /// <summary>Whether at least one currently selected session has an explicit observation.</summary>
    public bool HasLoadedSelection
    {
        get
        {
            var selected = Sessions.Where(choice => choice.IsSelected).Select(choice => choice.Session).ToHashSet();
            return _hasLoaded && _loaded.Any(item => selected.Contains(item.Session));
        }
    }

    /// <summary>Selects every discovered session without loading its resources.</summary>
    [RelayCommand]
    public void SelectAll() => SetSelection(true);

    /// <summary>Clears the display filter without changing the engine management scope.</summary>
    [RelayCommand]
    public void ClearSelection() => SetSelection(false);

    /// <summary>Localized status without modifying session names or engine diagnostics.</summary>
    public string StatusText => IsBusy
        ? UiText.Get("CombinedSession_Loading", "Reading session information…")
        : _error is not null
            ? UiText.Get("CombinedSession_Error", "Session refresh failed: {0}", WslcSessionInventoryErrors.Describe(_error))
            : _cancelled
                ? UiText.Get("CombinedSession_Cancelled", "Refresh cancelled; displayed resources may be stale.")
            : !_hasDiscovered
                ? UiText.Get("CombinedSession_Initial", "Open a resource page to load all active sessions.")
                : Sessions.Count == 0
                    ? UiText.Get("CombinedSession_NoActive", "No active WSLC sessions were found.")
                : !_hasLoaded || Sessions.All(choice => !choice.IsSelected)
                    ? UiText.Get("CombinedSession_Select", "Select sessions and load their resources.")
                    : SummaryText();

    private string SummaryText()
    {
        var summary = UiText.Get("CombinedSession_Summary",
            "{0} of {1} containers running · {2} images · {3} volumes · {4} networks · {5} endpoints",
            Inventory.Containers.Count(row => row.Value.State == ContainerState.Running),
            Inventory.Containers.Count, Inventory.Images.Count, Inventory.Volumes.Count,
            Inventory.Networks.Count, Inventory.Endpoints.Count);
        var selected = Sessions.Where(choice => choice.IsSelected).Select(choice => choice.Session).ToHashSet();
        var oldest = _loaded.Where(item => selected.Contains(item.Session) && item.ObservedAt is not null)
            .Select(item => item.ObservedAt!.Value).OrderBy(value => value).FirstOrDefault();
        return oldest == default ? summary : UiText.Get("CombinedSession_OldestRead",
            "{0}\nOldest read: {1}", summary, oldest.ToLocalTime().ToString("g"));
    }

    /// <summary>Localized warning when a selected inventory or its endpoint metadata is incomplete.</summary>
    public string WarningText
    {
        get
        {
            var selected = Sessions.Where(choice => choice.IsSelected).Select(choice => choice.Session).ToHashSet();
            var missing = selected.Count(session => !_loaded.Any(item => item.Session == session));
            var warnings = new List<string>();
            if (missing > 0 && _hasLoaded)
                warnings.Add(UiText.Get("CombinedSession_NotLoaded", "{0} selected session(s) have not been loaded.", missing));
            if (Inventory.Failures.Count > 0)
            {
                warnings.Add(UiText.Get("CombinedSession_Partial",
                    "Some session inventories are unavailable; displayed totals may be incomplete."));
                warnings.AddRange(Inventory.Failures.Select(DescribeFailure));
            }
            if (_hasLoaded && !Inventory.EndpointsComplete)
                warnings.Add(UiText.Get("CombinedSession_UnknownPorts",
                    "Some running containers have unknown published ports; endpoints may be incomplete."));
            if (Inventory.Containers.Any(row => row.Session.CreatorPid <= 0 || !row.Value.CreatedAtKnown ||
                    row.Value.CreatedAt <= 0 || string.IsNullOrWhiteSpace(row.Value.Id) || string.IsNullOrWhiteSpace(row.Value.Name)) ||
                Inventory.Images.Any(row => row.Session.CreatorPid <= 0 || string.IsNullOrWhiteSpace(row.Value.Id)) ||
                Inventory.Volumes.Any(row => row.Session.CreatorPid <= 0 || string.IsNullOrWhiteSpace(row.Value.Name)) ||
                Inventory.Networks.Any(row => row.Session.CreatorPid <= 0 ||
                    !row.Value.IsBuiltIn && string.IsNullOrWhiteSpace(row.Value.Id)))
                warnings.Add(UiText.Get("CombinedSession_UnknownContainerIdentity",
                    "Some actions are unavailable because session or resource identity is unknown."));
            return string.Join(Environment.NewLine, warnings);
        }
    }

    private static string DescribeFailure(WslcCombinedInventory.Failure failure)
    {
        if (failure.Kind == WslcCombinedInventory.FailureKind.Session)
            return UiText.Get("CombinedSession_SessionChanged",
                "{0}: the session changed or is no longer available.", failure.Session.Name);
        var kind = failure.Kind switch
        {
            WslcCombinedInventory.FailureKind.Containers => UiText.Get("CombinedSession_KindContainers", "containers"),
            WslcCombinedInventory.FailureKind.Images => UiText.Get("CombinedSession_KindImages", "images"),
            WslcCombinedInventory.FailureKind.Volumes => UiText.Get("CombinedSession_KindVolumes", "volumes"),
            WslcCombinedInventory.FailureKind.Networks => UiText.Get("CombinedSession_KindNetworks", "networks"),
            _ => throw new InvalidOperationException(UiText.Get("CombinedSession_UnknownFailure",
                "The session inventory failure type is unknown.")),
        };
        if (failure.TimedOut)
            return UiText.Get("CombinedSession_PartTimedOut", "{0}: {1} timed out.", failure.Session.Name, kind);
        if (failure.UnexpectedCancellation)
            return UiText.Get("CombinedSession_PartFailed", "{0}: {1} unavailable: {2}",
                failure.Session.Name, kind, UiText.Get("CombinedSession_UnexpectedCancellation",
                    "The engine cancelled the request unexpectedly."));
        return string.IsNullOrWhiteSpace(failure.Detail)
            ? UiText.Get("CombinedSession_PartUnavailable", "{0}: {1} unavailable.", failure.Session.Name, kind)
            : UiText.Get("CombinedSession_PartFailed", "{0}: {1} unavailable: {2}", failure.Session.Name, kind, failure.Detail);
    }

    /// <summary>Discovers active sessions without reading their container resources.</summary>
    [RelayCommand]
    public async Task DiscoverAsync(CancellationToken ct = default)
    {
        if (IsBusy)
            return;
        using var refresh = BeginRefresh(ct);
        try
        {
            var executable = _reader.CurrentExecutablePath;
            var discovered = await _reader.DiscoverAsync(refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (!string.Equals(executable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
            if (_loadedExecutable is not null &&
                !string.Equals(_loadedExecutable, executable, StringComparison.OrdinalIgnoreCase))
            {
                _loaded.Clear();
                _loadedExecutable = null;
                _hasLoaded = false;
            }
            var existing = Sessions.ToDictionary(choice => choice.Session);
            var current = discovered.ToHashSet();
            var choices = discovered.Select(session =>
                existing.TryGetValue(session, out var previous)
                    ? new CombinedSessionChoice(session, previous.IsSelected, true)
                    : new CombinedSessionChoice(session, !_hasDiscovered, true)).ToList();
            choices.AddRange(Sessions.Where(choice => choice.IsSelected && !current.Contains(choice.Session))
                .Select(choice => new CombinedSessionChoice(choice.Session, true, false)));

            foreach (var choice in Sessions)
                choice.PropertyChanged -= OnChoiceChanged;
            Sessions.Clear();
            foreach (var choice in choices)
            {
                choice.PropertyChanged += OnChoiceChanged;
                Sessions.Add(choice);
            }
            _loaded.RemoveAll(item => !current.Contains(item.Session));
            _hasDiscovered = true;
            LastDiscoveryAt = DateTimeOffset.UtcNow;
            _discoveredExecutable = executable;
            _error = null;
            _cancelled = false;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            _logger.LogDebug("WSLC session discovery was cancelled.");
            _cancelled = true;
        }
        catch (OperationCanceledException ex)
        {
            SetUnexpectedCancellation(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not discover WSLC sessions.");
            _error = ex;
            OnPropertyChanged(nameof(StatusText));
        }
        finally
        {
            EndRefresh(refresh);
        }
    }

    /// <summary>Explicitly refreshes every active session for a resource page.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default, bool containersOnly = false)
    {
        ct.ThrowIfCancellationRequested();
        while (_allRefresh is { Task.IsCompleted: false } cancelled && cancelled.Cancellation.IsCancellationRequested)
        {
            try { await cancelled.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        var refresh = _allRefresh;
        if (!containersOnly && refresh is { Task.IsCompleted: false, ContainersOnly: true })
        {
            try { await RefreshAllAsync(ct, containersOnly: true); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await RefreshAllAsync(ct);
            return;
        }
        if (refresh is null || refresh.Task.IsCompleted)
        {
            refresh = new AllRefresh { Consumers = 1, ContainersOnly = containersOnly };
            _allRefresh = refresh;
            refresh.Task = RefreshAllCoreAsync(refresh.Cancellation.Token, containersOnly);
        }
        else
        {
            refresh.Consumers++;
        }

        try { await refresh.Task.WaitAsync(ct); }
        finally
        {
            if (--refresh.Consumers == 0)
            {
                try
                {
                    if (!refresh.Task.IsCompleted)
                    {
                        refresh.Cancellation.Cancel();
                        try { await refresh.Task; }
                        catch (OperationCanceledException) when (refresh.Cancellation.IsCancellationRequested) { }
                    }
                }
                finally
                {
                    if (ReferenceEquals(_allRefresh, refresh)) _allRefresh = null;
                    refresh.Cancellation.Dispose();
                }
            }
        }
    }

    /// <summary>Reuses a recent complete inventory on navigation; explicit refreshes always read the engine.</summary>
    public Task RefreshForNavigationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (CanReuseFullInventory(TimeSpan.FromSeconds(15)))
        {
            SelectAll();
            return Task.CompletedTask;
        }
        return RefreshAllAsync(ct);
    }

    internal Task RefreshDashboardAsync(CancellationToken ct) =>
        RefreshAllAsync(ct, containersOnly: CanReuseFullInventory(TimeSpan.FromMinutes(1)));

    private bool CanReuseFullInventory(TimeSpan age) =>
        !IsBusy && !HasRefreshFailure && _lastFullRefresh is { } refreshed &&
        DateTimeOffset.UtcNow - refreshed < age &&
        string.Equals(_loadedExecutable ?? _discoveredExecutable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        _loaded.Count == Sessions.Count &&
        Sessions.All(choice => choice.IsAvailable &&
            _loaded.Any(item => item.Session == choice.Session && !item.SessionChanged &&
                item.Containers.IsComplete && item.Images.IsComplete && item.Volumes.IsComplete && item.Networks.IsComplete));

    private async Task RefreshAllCoreAsync(CancellationToken ct, bool containersOnly)
    {
        while (IsBusy && _refreshFinished is { } existing)
            await existing.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        await DiscoverAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (_error is not null || _cancelled)
            return;
        if (containersOnly && Sessions.Any(choice => choice.IsAvailable &&
                !_loaded.Any(item => item.Session == choice.Session && !item.SessionChanged)))
            containersOnly = false;
        SelectAll();
        await LoadCoreAsync(ct, containersOnly);
        ct.ThrowIfCancellationRequested();
        if (!containersOnly && !HasRefreshFailure &&
            _loaded.All(item => !item.SessionChanged && item.Containers.IsComplete && item.Images.IsComplete &&
                item.Volumes.IsComplete && item.Networks.IsComplete))
            _lastFullRefresh = DateTimeOffset.UtcNow;
    }

    /// <summary>Updates the already-loaded management session from the shared monitor without polling other sessions.</summary>
    public void UpdateManagedContainers(WslcSessionInfo? session, string executable,
        IReadOnlyList<ContainerInfo> containers)
    {
        if (IsBusy || _sessionContext is null || _loadedExecutable is null ||
            !string.Equals(_loadedExecutable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(executable, _loadedExecutable, StringComparison.OrdinalIgnoreCase) ||
            session is not { ID: > 0, CreatorPid: > 0 })
            return;
        var index = _loaded.FindIndex(item => !item.SessionChanged &&
            string.Equals(item.Session.Name, session.Name, StringComparison.Ordinal));
        if (index < 0)
            return;
        if (_loaded[index].Session != session)
        {
            _loaded[index] = _loaded[index] with { SessionChanged = true };
            foreach (var choice in Sessions.Where(choice =>
                         string.Equals(choice.Name, session.Name, StringComparison.Ordinal)))
                choice.IsAvailable = false;
            ApplyFilter();
            return;
        }
        if (ReferenceEquals(_loaded[index].Containers.Items, containers) && _loaded[index].Containers.IsComplete)
            return;
        _loaded[index] = _loaded[index] with { Containers = new WslcInventoryPart<ContainerInfo>(containers) };
        ApplyFilter();
    }

    /// <summary>Explicitly reads the selected sessions. This can wake an idle session VM.</summary>
    [RelayCommand]
    public Task LoadAsync(CancellationToken ct = default) => LoadCoreAsync(ct, containersOnly: false);

    private async Task LoadCoreAsync(CancellationToken ct, bool containersOnly)
    {
        if (IsBusy)
            return;
        var executable = _reader.CurrentExecutablePath;
        if (_discoveredExecutable is null ||
            !string.Equals(_discoveredExecutable, executable, StringComparison.OrdinalIgnoreCase))
        {
            SetExecutableChanged();
            return;
        }
        var sessions = Sessions.Where(choice => choice.IsSelected).Select(choice => choice.Session).ToArray();
        if (sessions.Length == 0)
        {
            _hasLoaded = false;
            _error = null;
            _cancelled = false;
            ApplyFilter();
            return;
        }
        using var refresh = BeginRefresh(ct);
        try
        {
            var loaded = await _reader.ReadAsync(sessions, refresh.Token, containersOnly);
            refresh.Token.ThrowIfCancellationRequested();
            if (!string.Equals(executable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
            if (containersOnly)
                loaded = loaded.Select(item =>
                {
                    var previous = _loaded.FirstOrDefault(old => old.Session == item.Session && !old.SessionChanged);
                    return previous is null || item.SessionChanged ? item : item with
                    {
                        Images = previous.Images, Volumes = previous.Volumes, Networks = previous.Networks,
                        ObservedAt = previous.ObservedAt,
                    };
                }).ToArray();
            _loaded.Clear();
            _loaded.AddRange(loaded);
            _loadedExecutable = executable;
            _hasLoaded = true;
            _error = null;
            _cancelled = false;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            _logger.LogDebug("Combined WSLC inventory refresh was cancelled.");
            _cancelled = true;
        }
        catch (OperationCanceledException ex)
        {
            SetUnexpectedCancellation(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not read combined WSLC session inventory.");
            _error = ex;
            OnPropertyChanged(nameof(StatusText));
        }
        finally
        {
            EndRefresh(refresh);
        }
    }

    /// <summary>Cancels a pending user-initiated read when the page is left.</summary>
    [RelayCommand]
    public void CancelRefresh() => _refresh?.Cancel();

    /// <summary>Removes stale rows for one session until that session is read again.</summary>
    public void InvalidateSession(WslcSessionInfo session)
    {
        _loaded.RemoveAll(item => item.Session == session);
        _lastFullRefresh = null;
        ApplyFilter();
    }

    /// <summary>Refreshes one session after a user action without waking other selected sessions.</summary>
    public async Task RefreshSessionAsync(WslcSessionInfo session)
    {
        while (IsBusy && _refreshFinished is { } existing)
            await existing.Task;
        var executable = _reader.CurrentExecutablePath;
        if (_loadedExecutable is null ||
            !string.Equals(_loadedExecutable, executable, StringComparison.OrdinalIgnoreCase))
        {
            SetExecutableChanged();
            return;
        }
        using var refresh = BeginRefresh();
        try
        {
            var result = await _reader.ReadAsync([session], refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (!string.Equals(executable, _reader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
            _loaded.RemoveAll(item => item.Session == session);
            _loaded.AddRange(result);
            _hasLoaded = true;
            _error = null;
            _cancelled = false;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            _logger.LogDebug("The post-action session refresh was cancelled.");
            _cancelled = true;
        }
        catch (OperationCanceledException ex)
        {
            _loaded.RemoveAll(item => item.Session == session);
            SetUnexpectedCancellation(ex);
            ApplyFilter();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not refresh a session after an action.");
            _loaded.RemoveAll(item => item.Session == session);
            _error = ex;
            ApplyFilter();
        }
        finally
        {
            EndRefresh(refresh);
        }
    }

    /// <summary>Releases event subscriptions and cancels outstanding reads.</summary>
    public void Dispose()
    {
        CancelRefresh();
        if (_sessionContext is not null)
            _sessionContext.EngineSettingChanged -= OnEngineSettingChanged;
        UiText.LanguageChanged -= OnLanguageChanged;
        foreach (var choice in Sessions)
            choice.PropertyChanged -= OnChoiceChanged;
    }

    private CancellationTokenSource BeginRefresh(CancellationToken ct = default)
    {
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refresh = refresh;
        _refreshFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        return refresh;
    }

    private void EndRefresh(CancellationTokenSource refresh)
    {
        if (!ReferenceEquals(_refresh, refresh))
            return;
        _refresh = null;
        IsBusy = false;
        _refreshFinished?.TrySetResult();
        _refreshFinished = null;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasLoadedSelection));
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_updatingSelection && args.PropertyName == nameof(CombinedSessionChoice.IsSelected))
            ApplyFilter();
    }

    private void SetUnexpectedCancellation(OperationCanceledException exception)
    {
        _logger.LogWarning(exception, "WSLC cancelled a session read unexpectedly.");
        _error = exception;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnEngineSettingChanged(object? sender, EventArgs args)
    {
        void Invalidate()
        {
            CancelRefresh();
            foreach (var choice in Sessions)
                choice.PropertyChanged -= OnChoiceChanged;
            Sessions.Clear();
            _loaded.Clear();
            _hasDiscovered = false;
            _hasLoaded = false;
            _discoveredExecutable = null;
            _loadedExecutable = null;
            SetExecutableChanged();
            ApplyFilter();
        }
#if WSLCD_TEST_NO_DISPATCHER
        Invalidate();
#else
        if (_dispatcher.HasThreadAccess)
            Invalidate();
        else
            _dispatcher.TryEnqueue(Invalidate);
#endif
    }

    private void SetExecutableChanged()
    {
        _error = new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
        OnPropertyChanged(nameof(StatusText));
    }

    private void SetSelection(bool selected)
    {
        _updatingSelection = true;
        try
        {
            foreach (var choice in Sessions)
                choice.IsSelected = selected && choice.IsAvailable;
        }
        finally
        {
            _updatingSelection = false;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = Sessions.Where(choice => choice.IsSelected).Select(choice => choice.Session).ToHashSet();
        Inventory = WslcCombinedInventory.Create(_loaded.Where(item => selected.Contains(item.Session)).ToArray(), Inventory);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasLoadedSelection));
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var choice in Sessions)
            choice.RefreshLocalizedText();
        foreach (var row in _containerRows.Concat(_imageRows).Concat(_volumeRows).Concat(_networkRows).Concat(_endpointRows))
            row.RefreshLocalizedText();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WarningText));
    }
}
