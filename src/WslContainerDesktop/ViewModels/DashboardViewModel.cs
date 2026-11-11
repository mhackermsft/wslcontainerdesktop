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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Live resource row for the dashboard's running-containers table.</summary>
public partial class DashboardStatRow : ObservableObject
{
    /// <summary>The owning session shown alongside the container name.</summary>
    [ObservableProperty]
    private string _sessionName = string.Empty;

    /// <summary>Whether the row is displayed in the table layout.</summary>
    [ObservableProperty]
    private bool _isWideLayout;

    /// <summary>Whether the latest statistics sample is available.</summary>
    [ObservableProperty]
    private bool _statsAvailable;

    /// <summary>Value for name shown or edited by the view.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Value for cpu shown or edited by the view.</summary>
    [ObservableProperty]
    private string _cpu = "-";

    /// <summary>Bindable state for cpu value used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuBarValue))]
    private double _cpuValue;
    public double CpuBarValue => Math.Clamp(CpuValue, 0, 100);

    /// <summary>Value for mem shown or edited by the view.</summary>
    [ObservableProperty]
    private string _mem = "-";

    /// <summary>Bindable state for mem value used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemBarValue))]
    private double _memValue;
    public double MemBarValue => Math.Clamp(MemValue, 0, 100);

    /// <summary>Bindable state for mem usage used by the view.</summary>
    [ObservableProperty]
    private string _memUsage = "-";

    /// <summary>Bindable state for net i o used by the view.</summary>
    [ObservableProperty]
    private string _netIO = "-";

    /// <summary>Bindable state for block i o used by the view.</summary>
    [ObservableProperty]
    private string _blockIO = "-";

    /// <summary>Bindable state for has gpu used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuTooltip))]
    private bool _hasGpu;

    /// <summary>Bindable state for gpu name used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuTooltip))]
    private string? _gpuName;

    /// <summary>Value for id shown or edited by the view.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Captured source session and container identity for owner-scoped actions.</summary>
    public WslcSessionInfo Session { get; set; } = new();
    public ContainerInfo Container { get; set; } = new();
    public string ExecutablePath { get; set; } = string.Empty;
    public AutomationSessionOwner? Owner { get; set; }
    public bool CanNavigate { get; set; }
    internal DashboardStatsIdentity Identity { get; set; }

    /// <summary>True once GPU access has been probed for this row.</summary>
    public bool GpuChecked { get; set; }

    /// <summary>Tooltip for the GPU badge.</summary>
    public string GpuTooltip => string.IsNullOrWhiteSpace(GpuName)
        ? UiText.Get("Resource_Text_cc4941571be3", "GPU passthrough enabled")
        : UiText.Get("Resource_Text_f190b111a23c", "GPU: {0}", GpuName);

    /// <summary>
    /// Returns the container name. The dashboard's clickable ListView uses this as each row's
    /// screen-reader name; without it Narrator would announce the type name.
    /// </summary>
    public override string ToString() => $"{SessionName}: {Name}";

    /// <summary>Provides the update operation to views or collaborating view models.</summary>
    public void Update(ContainerStats s)
    {
        Cpu = s.CpuPercent;
        CpuValue = s.CpuValue;
        Mem = s.MemPercent;
        MemValue = s.MemValue;
        MemUsage = s.MemUsage;
        NetIO = s.NetIO;
        BlockIO = s.BlockIO;
        StatsAvailable = true;
    }

    /// <summary>Clears a stale or unavailable sample without implying zero usage.</summary>
    public void ClearStats()
    {
        Cpu = Mem = MemUsage = NetIO = BlockIO = "-";
        CpuValue = MemValue = 0;
        StatsAvailable = false;
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Backs the Dashboard page with engine health, inventory counts and a short-lived polling loop for live container statistics.</summary>
public partial class DashboardViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EngineVersionDisplay => EngineVersion;

    private readonly IWslcService _wslc;
    private readonly IWindowVisibility _visibility;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly CombinedSessionViewModel _combined;
    private readonly DashboardStatsReader _statsReader;
    private readonly WslcSessionContainerActions _sessionActions;
    private readonly SemaphoreSlim _gpuGate = new(4);

    private CancellationTokenSource? _statsCts;
    private IReadOnlyList<DashboardSessionStats> _statsSamples = [];
    private Exception? _statsError;
    private bool _hasStatsSample;
    private bool _isWideLayout;
    private string? _versionError;
    private DateTimeOffset? _lastStatsAt;
    private string? _lastInventoryExecutable;
    private bool _pollStopped;

    /// <summary>Bindable state for engine version used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineVersionDisplay))]
    private string _engineVersion = "-";

    /// <summary>Bindable state for running containers used by the view.</summary>
    [ObservableProperty]
    private int _runningContainers;

    /// <summary>Bindable state for total containers used by the view.</summary>
    [ObservableProperty]
    private int _totalContainers;

    /// <summary>Bindable state for image count used by the view.</summary>
    [ObservableProperty]
    private int _imageCount;

    /// <summary>Bindable state for volume count used by the view.</summary>
    [ObservableProperty]
    private int _volumeCount;

    /// <summary>Bindable state for total cpu used by the view.</summary>
    [ObservableProperty]
    private string _totalCpu = "—";

    /// <summary>Bindable state for live stats used by the view.</summary>
    public ObservableCollection<DashboardStatRow> LiveStats { get; } = new();

    private int ActiveSessions => _combined.Sessions.Count(session => session.IsAvailable && session.IsSelected);
    private bool CountsAvailable => _combined.HasContainerData;
    private bool ContainerInventoryComplete => CountsAvailable && _combined.HasCompleteContainerInventory;

    /// <summary>Session-qualified inventory totals, or a placeholder while unavailable.</summary>
    public string RunningContainersDisplay => CountsAvailable ? RunningContainers.ToString() : "—";
    public string TotalContainersDisplay => CountsAvailable ? TotalContainers.ToString() : "—";
    public string ImageCountDisplay => _combined.HasImageData ? ImageCount.ToString() : "—";
    public string VolumeCountDisplay => _combined.HasVolumeData ? VolumeCount.ToString() : "—";
    public string ActiveSessionsDisplay => _combined.HasSuccessfulDiscovery ? ActiveSessions.ToString() : "—";
    private string NetworkCountDisplay => _combined.HasNetworkData ? _combined.Inventory.Networks.Count.ToString() : "—";
    private string EndpointCountDisplay => CountsAvailable ? _combined.Inventory.Endpoints.Count.ToString() : "—";

    /// <summary>Freshness and completeness of the all-session inventory.</summary>
    public string InventoryStatus => _combined.IsBusy || !_combined.HasSuccessfulDiscovery || !CountsAvailable
        ? _combined.StatusText
        : _combined.OldestRead is { } oldest
            ? UiText.Get("Dashboard_AllSessionsFreshness",
                "{0} active sessions · {1} networks · {2} endpoints · Oldest inventory read: {3}",
                ActiveSessions, NetworkCountDisplay, EndpointCountDisplay, oldest.ToLocalTime().ToString("g"))
            : UiText.Get("Dashboard_AllSessionsCount", "{0} active sessions · {1} networks · {2} endpoints",
                ActiveSessions, NetworkCountDisplay, EndpointCountDisplay);
    public string InventoryWarning => string.Join(Environment.NewLine, new[]
    {
        _combined.WarningText,
        _versionError is null ? string.Empty : UiText.Get("Dashboard_VersionUnavailable",
            "The engine version could not be read: {0}", _versionError),
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    /// <summary>Visible errors for missing or incomplete live statistics.</summary>
    public string StatsWarning => _pollStopped
        ? UiText.Get("Dashboard_PollStopped", "Live dashboard updates stopped: {0}. Refresh to retry.", StatsErrorText)
        : _statsError is not null
        ? UiText.Get("Dashboard_StatsReadFailed", "Live statistics could not be read: {0}", StatsErrorText)
        : !CountsAvailable
            ? UiText.Get("Dashboard_StatsUnavailable", "Running containers or their statistics are not available yet.")
        : !_hasStatsSample && LiveStats.Count > 0
            ? UiText.Get("Dashboard_StatsLoading", "Reading live statistics…")
        : string.Join(Environment.NewLine, _statsSamples.Where(sample => sample.Failure is not null)
            .Select(sample => UiText.Get("Dashboard_StatsSessionFailure", "{0}: {1}",
                sample.Session.Name, DescribeFailure(sample))));

    private string StatsErrorText => _statsError is null ? string.Empty : WslcSessionInventoryErrors.Describe(_statsError);

    public string StatsFreshness => _hasStatsSample && _lastStatsAt is { } time
        ? UiText.Get("Dashboard_StatsFreshness", "Statistics updated: {0}", time.ToLocalTime().ToString("T"))
        : string.Empty;

    public string EmptyStatsMessage => !CountsAvailable || !ContainerInventoryComplete
        ? UiText.Get("Dashboard_StatsUnavailable", "Running containers or their statistics are not available yet.")
        : UiText.Get("Dashboard_NoRunning", "No running containers to report statistics for.");

    /// <summary>Creates the Dashboard view model and stores its injected services.</summary>
    public DashboardViewModel(IWslcService wslc, IWindowVisibility visibility,
        ILogger<DashboardViewModel> logger, CombinedSessionViewModel combined,
        DashboardStatsReader statsReader, WslcSessionContainerActions sessionActions)
    {
        _wslc = wslc;
        _visibility = visibility;
        _logger = logger;
        _combined = combined;
        _statsReader = statsReader;
        _sessionActions = sessionActions;
        _combined.PropertyChanged += OnCombinedChanged;
    }

    private void OnCombinedChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.Inventory))
        {
            RunningContainers = _combined.Inventory.Containers.Count(row => row.Value.State == ContainerState.Running);
            TotalContainers = _combined.Inventory.Containers.Count;
            ImageCount = _combined.Inventory.Images.Count;
            VolumeCount = _combined.Inventory.Volumes.Count;
            if (!SyncRunningRows())
            {
                _statsSamples = [];
                _statsError = null;
                _hasStatsSample = false;
                _lastStatsAt = null;
                TotalCpu = "—";
                foreach (var row in LiveStats) row.ClearStats();
            }
            else
            {
                UpdateTotalCpu();
            }
        }
        else if (_combined.HasRefreshFailure)
        {
            _statsSamples = [];
            _statsError = null;
            _hasStatsSample = false;
            _lastStatsAt = null;
            TotalCpu = "—";
            foreach (var row in LiveStats) row.ClearStats();
        }
        NotifyDashboardText();
    }

    private void NotifyDashboardText()
    {
        OnPropertyChanged(nameof(RunningContainersDisplay));
        OnPropertyChanged(nameof(TotalContainersDisplay));
        OnPropertyChanged(nameof(ImageCountDisplay));
        OnPropertyChanged(nameof(VolumeCountDisplay));
        OnPropertyChanged(nameof(ActiveSessionsDisplay));
        OnPropertyChanged(nameof(InventoryStatus));
        OnPropertyChanged(nameof(InventoryWarning));
        OnPropertyChanged(nameof(StatsWarning));
        OnPropertyChanged(nameof(StatsFreshness));
        OnPropertyChanged(nameof(EmptyStatsMessage));
    }

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _sessionActions.InvalidateGpuInfo();
        foreach (var row in LiveStats) row.GpuChecked = false;
        if (_pollStopped && _statsCts is not null)
        {
            StartStatsPolling();
            return;
        }
        using var refresh = new VisibleRead(_visibility, ct, _statsCts?.Token ?? default);
        try
        {
            await RefreshInventoryAsync(refresh.Token);
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug("Dashboard inventory refresh stopped because it is no longer visible.");
        }
    }

    private DateTimeOffset _versionReadAt;
    private string? _versionExecutable;

    private async Task RefreshInventoryAsync(CancellationToken ct, bool automatic = false)
    {
        ct.ThrowIfCancellationRequested();
        if (!automatic || DateTimeOffset.UtcNow - _versionReadAt >= TimeSpan.FromMinutes(5) ||
            !string.Equals(_versionExecutable, _combined.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var executable = _combined.CurrentExecutablePath;
                var version = await _wslc.GetVersionAsync(ct);
                EngineVersion = version.Success ? version.StandardOutput.Trim() : "-";
                _versionError = version.Success ? null : version.ErrorText;
                _versionReadAt = DateTimeOffset.UtcNow;
                _versionExecutable = executable;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or
                                       System.ComponentModel.Win32Exception or TimeoutException)
            {
                _logger.LogWarning(ex, "Dashboard engine version refresh failed.");
                EngineVersion = "-";
                _versionError = ex.Message;
            }
            OnPropertyChanged(nameof(InventoryWarning));
        }
        if (automatic) await _combined.RefreshDashboardAsync(ct);
        else await _combined.RefreshAllAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Provides the start stats polling operation to views or collaborating view models.</summary>
    public void StartStatsPolling()
    {
        StopStatsPolling();
        _statsCts = new CancellationTokenSource();
        _pollStopped = false;
        _statsError = null;
        OnPropertyChanged(nameof(StatsWarning));
        var token = _statsCts.Token;
        _ = PollAsync(token);
    }

    private async Task PollAsync(CancellationToken token)
    {
        var nextInventoryRead = DateTimeOffset.MinValue;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _visibility.WhenViewableAsync(token);
                token.ThrowIfCancellationRequested();
                if (!_visibility.IsViewable)
                    continue;
                using var visible = new VisibleRead(_visibility, token);
                var readToken = visible.Token;
                try
                {
                    if (DateTimeOffset.UtcNow >= nextInventoryRead)
                    {
                        await RefreshInventoryAsync(readToken, automatic: true);
                        readToken.ThrowIfCancellationRequested();
                        nextInventoryRead = DateTimeOffset.UtcNow.AddSeconds(15);
                    }
                    readToken.ThrowIfCancellationRequested();
                    var inventory = _combined.Inventory;
                    var executable = _combined.CurrentExecutablePath;
                    try
                    {
                        if (CountsAvailable)
                        {
                            var stats = await _statsReader.ReadAsync(inventory.Containers, executable, readToken);
                            readToken.ThrowIfCancellationRequested();
                            if (_visibility.IsViewable && ReferenceEquals(inventory, _combined.Inventory) &&
                                string.Equals(executable, _combined.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                                ApplyStats(stats, token);
                        }
                    }
                    catch (OperationCanceledException) when (readToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or
                                               System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
                    {
                        _logger.LogWarning(ex, "Could not read all-session dashboard statistics.");
                        _statsError = ex;
                        _statsSamples = [];
                        _hasStatsSample = false;
                        _lastStatsAt = null;
                        TotalCpu = "—";
                        foreach (var row in LiveStats) row.ClearStats();
                        OnPropertyChanged(nameof(StatsWarning));
                        OnPropertyChanged(nameof(StatsFreshness));
                    }
                    var reportingSessions = inventory.Containers.Where(row => row.Value.State == ContainerState.Running)
                        .Select(row => row.Session).Distinct().Count();
                    await Task.Delay(DashboardStatsReader.PollingDelay(reportingSessions), readToken);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && visible.IsCancellationRequested)
                {
                    nextInventoryRead = DateTimeOffset.MinValue;
                    _logger.LogDebug("Dashboard reads paused because the window is no longer visible.");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("Dashboard polling stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard polling stopped unexpectedly.");
            _pollStopped = true;
            _statsError = ex;
            _statsSamples = [];
            _hasStatsSample = false;
            _lastStatsAt = null;
            TotalCpu = "—";
            foreach (var row in LiveStats) row.ClearStats();
            OnPropertyChanged(nameof(StatsWarning));
            OnPropertyChanged(nameof(StatsFreshness));
        }
    }

    internal bool SyncRunningRows()
    {
        var desired = new List<DashboardStatRow>();
        var existingRows = LiveStats.Where(row => row.Identity.IsValid)
            .GroupBy(row => row.Identity, DashboardStatsIdentity.Comparer)
            .ToDictionary(group => group.Key, group => new Queue<DashboardStatRow>(group),
                DashboardStatsIdentity.Comparer);
        var executable = _combined.CurrentExecutablePath;
        var unchanged = _lastInventoryExecutable is not null &&
            string.Equals(_lastInventoryExecutable, executable, StringComparison.OrdinalIgnoreCase);
        foreach (var resource in _combined.Inventory.Containers
                     .Where(row => row.Value.State == ContainerState.Running)
                     .OrderBy(row => row.Session.Name, StringComparer.Ordinal)
                     .ThenBy(row => row.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            var container = resource.Value;
            var identity = DashboardStatsIdentity.Capture(resource.Session, container, executable);
            DashboardStatRow? row = null;
            if (identity.IsValid && existingRows.TryGetValue(identity, out var candidates) && candidates.Count > 0)
                row = candidates.Dequeue();
            if (row is null)
            {
                unchanged = false;
                row = new DashboardStatRow();
            }
            row.Session = resource.Session;
            row.SessionName = resource.Session.Name;
            row.Container = container;
            row.Id = container.Id;
            row.Name = container.Name;
            row.ExecutablePath = executable;
            row.Identity = identity;
            row.IsWideLayout = _isWideLayout;
            row.CanNavigate = identity.IsValid;
            row.Owner = row.CanNavigate
                ? AutomationSessionOwner.Capture(executable, new WslcSessionTarget(resource.Session.Name)) : null;
            unchanged &= row.CanNavigate;
            desired.Add(row);
        }
        unchanged &= desired.Count == LiveStats.Count;
        unchanged &= desired.Select(row => (row.Session, row.Id)).Distinct().Count() == desired.Count;
        _lastInventoryExecutable = executable;
        CollectionSync.ReplaceAll(LiveStats, desired);
        return unchanged;
    }

    internal void ApplyStats(IReadOnlyList<DashboardSessionStats> samples, CancellationToken token)
    {
        var indexed = samples.SelectMany(session => session.Containers.Select(sample => (session.Session, Sample: sample)))
            .ToLookup(item => (item.Session, item.Sample.Container.Id, item.Sample.Container.CreatedAt,
                item.Sample.Container.CreatedAtKnown, item.Sample.Container.Image), item => item.Sample);
        foreach (var row in LiveStats)
        {
            var matches = indexed[(row.Session, row.Id, row.Container.CreatedAt,
                row.Container.CreatedAtKnown, row.Container.Image)].Take(2).ToArray();
            if (matches.Length == 1 && matches[0].Stats is { } stats)
                row.Update(stats);
            else
                row.ClearStats();
        }
        _statsSamples = samples;
        _statsError = null;
        _hasStatsSample = true;
        _lastStatsAt = DateTimeOffset.UtcNow;
        UpdateTotalCpu();
        OnPropertyChanged(nameof(StatsWarning));
        OnPropertyChanged(nameof(StatsFreshness));
        foreach (var row in LiveStats.Where(row => row.CanNavigate && row.StatsAvailable && !row.GpuChecked))
            _ = ProbeGpuAsync(row, token);
    }

    private void UpdateTotalCpu()
    {
        TotalCpu = _hasStatsSample && ContainerInventoryComplete &&
            _statsSamples.All(sample => sample.Failure is null) && LiveStats.All(row => row.StatsAvailable)
            ? $"{LiveStats.Sum(row => row.CpuValue):0.#}%" : "—";
    }

    private static string DescribeFailure(DashboardSessionStats sample) => sample.Failure switch
    {
        DashboardStatsFailure.SessionChanged => UiText.Get("Dashboard_StatsSessionChanged", "The session changed or is no longer available."),
        DashboardStatsFailure.IdentityUnknown => UiText.Get("Dashboard_StatsIdentityUnknown", "Container identities are incomplete or ambiguous."),
        DashboardStatsFailure.Missing => UiText.Get("Dashboard_StatsMissing", "Some running containers did not report statistics."),
        DashboardStatsFailure.InvalidSample => UiText.Get("Dashboard_StatsInvalid", "The engine returned invalid statistics."),
        DashboardStatsFailure.TimedOut => UiText.Get("Dashboard_StatsTimedOut", "The statistics request timed out."),
        DashboardStatsFailure.ReadFailed => UiText.Get("Dashboard_StatsReadFailed",
            "Live statistics could not be read: {0}", sample.Detail ?? string.Empty),
        _ => throw new InvalidOperationException("Unknown dashboard statistics failure."),
    };

    private readonly HashSet<DashboardStatRow> _pendingGpu = [];

    private async Task ProbeGpuAsync(DashboardStatRow row, CancellationToken token)
    {
        if (!_pendingGpu.Add(row)) return;
        using var visible = new VisibleRead(_visibility, token);
        token = visible.Token;
        row.GpuChecked = true;
        var acquired = false;
        try
        {
            await _gpuGate.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            if (!LiveStats.Contains(row) || !_visibility.IsViewable)
            {
                row.GpuChecked = false;
                return;
            }
            var target = WslcContainerActionTarget.Capture(row.Session, row.Container);
            var (hasGpu, gpuName) = await _sessionActions.ReadGpuInfoAsync(target, token,
                row.Container.StateChangedAtKnown ? row.Container.StateChangedAt : 0);
            if (!token.IsCancellationRequested && LiveStats.Contains(row) &&
                string.Equals(row.ExecutablePath, _combined.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                row.HasGpu = hasGpu;
                row.GpuName = gpuName;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            row.GpuChecked = false;
            _logger.LogDebug("Dashboard GPU check was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogDebug(ex, "Could not read dashboard GPU details for {Container} in {Session}.", row.Name, row.SessionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected dashboard GPU check failure for {Container} in {Session}.", row.Name, row.SessionName);
        }
        finally
        {
            if (acquired) _gpuGate.Release();
            _pendingGpu.Remove(row);
        }
    }

    /// <summary>Changes between the table and compact performance layouts.</summary>
    public void SetPerformanceLayout(bool wide)
    {
        _isWideLayout = wide;
        foreach (var row in LiveStats) row.IsWideLayout = _isWideLayout;
    }

    /// <summary>Provides the stop stats polling operation to views or collaborating view models.</summary>
    public void StopStatsPolling()
    {
        _statsCts?.Cancel();
        _statsCts?.Dispose();
        _statsCts = null;
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        NotifyDashboardText();
        foreach (var item in LiveStats) item.RefreshLocalizedText();
    }
}
