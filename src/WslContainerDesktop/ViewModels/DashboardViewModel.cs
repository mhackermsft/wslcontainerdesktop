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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.ViewModels;

/// <summary>Live resource row for the dashboard's running-containers table.</summary>
public partial class DashboardStatRow : ObservableObject
{
    /// <summary>Value for name shown or edited by the view.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Value for cpu shown or edited by the view.</summary>
    [ObservableProperty]
    private string _cpu = "-";

    /// <summary>Bindable state for cpu value used by the view.</summary>
    [ObservableProperty]
    private double _cpuValue;

    /// <summary>Value for mem shown or edited by the view.</summary>
    [ObservableProperty]
    private string _mem = "-";

    /// <summary>Bindable state for mem value used by the view.</summary>
    [ObservableProperty]
    private double _memValue;

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

    /// <summary>True once GPU access has been probed for this row.</summary>
    public bool GpuChecked { get; set; }

    /// <summary>Tooltip for the GPU badge.</summary>
    public string GpuTooltip => string.IsNullOrWhiteSpace(GpuName)
        ? "GPU passthrough enabled"
        : $"GPU: {GpuName}";

    /// <summary>
    /// Returns the container name. The dashboard's clickable ListView uses this as each row's
    /// screen-reader name; without it Narrator would announce the type name.
    /// </summary>
    public override string ToString() => Name;

    /// <summary>Provides the update operation to views or collaborating view models.</summary>
    public void Update(ContainerStats s)
    {
        Name = s.Name;
        Cpu = s.CpuPercent;
        CpuValue = s.CpuValue;
        Mem = s.MemPercent;
        MemValue = s.MemValue;
        MemUsage = s.MemUsage;
        NetIO = s.NetIO;
        BlockIO = s.BlockIO;
    }
}

/// <summary>Backs the Dashboard page with engine health, inventory counts and a short-lived polling loop for live container statistics.</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _statsCts;

    /// <summary>Bindable state for engine status used by the view.</summary>
    [ObservableProperty]
    private string _engineStatus = "Checking…";

    /// <summary>Bindable state for engine healthy used by the view.</summary>
    [ObservableProperty]
    private bool _engineHealthy;

    /// <summary>Bindable state for engine version used by the view.</summary>
    [ObservableProperty]
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
    private string _totalCpu = "0%";

    /// <summary>Bindable state for total cpu value used by the view.</summary>
    [ObservableProperty]
    private double _totalCpuValue;

    /// <summary>Bindable state for total mem usage used by the view.</summary>
    [ObservableProperty]
    private string _totalMemUsage = "-";

    /// <summary>Bindable state for live stats used by the view.</summary>
    public ObservableCollection<DashboardStatRow> LiveStats { get; } = new();

    /// <summary>Creates the Dashboard view model and stores its injected services.</summary>
    public DashboardViewModel(IWslcService wslc, StatusMonitor monitor, ILogger<DashboardViewModel> logger)
    {
        _wslc = wslc;
        _monitor = monitor;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _monitor.StatusChanged += OnStatusChanged;

        if (_monitor.Latest is not null)
        {
            Apply(_monitor.Latest);
        }
    }

    /// <summary>Handles status changed changes and updates related view-model state.</summary>
    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        var wasHealthy = EngineHealthy;
        Apply(e);

        // The version and counts are only read on navigation; re-read them when the engine
        // becomes usable again (engine recovery, or the WSL requirement being met after an update).
        if (!wasHealthy && EngineHealthy)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Applies apply state to bindable properties.</summary>
    private void Apply(EngineStatusSnapshot snapshot)
    {
        EngineHealthy = snapshot.Health == EngineHealth.Healthy;
        EngineStatus = snapshot.Health switch
        {
            EngineHealth.Healthy => "Running",
            EngineHealth.Down => "Unreachable",
            _ => "Unknown",
        };
        RunningContainers = snapshot.RunningCount;
        TotalContainers = snapshot.TotalCount;
    }

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var version = await _wslc.GetVersionAsync();
            EngineVersion = version.Success ? version.StandardOutput.Trim() : "Unreachable";

            var images = await _wslc.ListImagesAsync();
            ImageCount = images.Count;

            var volumes = await _wslc.ListVolumesAsync();
            VolumeCount = volumes.Count;
        }
        catch (Exception ex)
        {
            // best effort
            _logger.LogDebug(ex, "Dashboard counts refresh failed.");
        }
    }

    /// <summary>Provides the start stats polling operation to views or collaborating view models.</summary>
    public void StartStatsPolling()
    {
        StopStatsPolling();
        _statsCts = new CancellationTokenSource();
        var token = _statsCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var stats = await _wslc.GetStatsAsync(token).ConfigureAwait(false);
                    _dispatcher.TryEnqueue(() => ApplyStats(stats));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // ignore transient stats errors
                    _logger.LogDebug(ex, "Transient dashboard stats poll error.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    /// <summary>Applies apply stats state to bindable properties.</summary>
    private void ApplyStats(IReadOnlyList<ContainerStats> rawStats)
    {
        // `wslc stats` has been observed to report stale/orphaned entries for containers that no
        // longer exist per `wslc ps` (the StatusMonitor snapshot, which the Containers page trusts).
        // Cross-check against that snapshot's running-container IDs so the dashboard can't grow
        // "live" rows for containers that aren't actually running.
        //
        // The two commands do not agree on ID width: `stats` reports the full 64-character ID while
        // `list` reports the 12-character short form, so an exact comparison matches nothing and the
        // dashboard showed "no running containers" with 0% CPU no matter what was running. Correlate
        // short and long forms instead of assuming one shape.
        var knownRunningIds = _monitor.Latest?.Containers
            .Where(c => c.State == ContainerState.Running)
            .Select(c => c.Id)
            .ToList();
        var stats = ContainerIdentity.RunningOnly(rawStats, knownRunningIds, s => s.Id);

        var byId = stats.ToDictionary(s => s.Id, StringComparer.Ordinal);

        for (var i = LiveStats.Count - 1; i >= 0; i--)
        {
            if (!byId.ContainsKey(LiveStats[i].Id))
            {
                LiveStats.RemoveAt(i);
            }
        }

        var existing = LiveStats.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var s in stats)
        {
            if (existing.TryGetValue(s.Id, out var row))
            {
                row.Update(s);
            }
            else
            {
                var newRow = new DashboardStatRow { Id = s.Id };
                newRow.Update(s);
                LiveStats.Add(newRow);
            }
        }

        var totalCpu = stats.Sum(s => s.CpuValue);
        TotalCpuValue = Math.Min(totalCpu, 100);
        TotalCpu = $"{totalCpu:0.#}%";

        // Probe GPU passthrough once per row (cheap exec check), like the Containers page.
        foreach (var row in LiveStats.Where(r => !r.GpuChecked))
        {
            _ = ProbeGpuAsync(row);
        }
    }

    /// <summary>Helper for the probe gpu workflow in this view model.</summary>
    private async Task ProbeGpuAsync(DashboardStatRow row)
    {
        row.GpuChecked = true;
        try
        {
            var (hasGpu, gpuName) = await _wslc.GetGpuInfoAsync(row.Id);
            row.HasGpu = hasGpu;
            row.GpuName = gpuName;
        }
        catch
        {
            // best-effort; leave GpuChecked true to avoid hammering exec
        }
    }

    /// <summary>Provides the stop stats polling operation to views or collaborating view models.</summary>
    public void StopStatsPolling()
    {
        try
        {
            _statsCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _statsCts?.Dispose();
        _statsCts = null;
    }
}
