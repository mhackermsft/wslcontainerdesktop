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
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.ViewModels;

/// <summary>Live resource row for the dashboard's running-containers table.</summary>
public partial class DashboardStatRow : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _cpu = "-";

    [ObservableProperty]
    private double _cpuValue;

    [ObservableProperty]
    private string _mem = "-";

    [ObservableProperty]
    private double _memValue;

    [ObservableProperty]
    private string _memUsage = "-";

    [ObservableProperty]
    private string _netIO = "-";

    [ObservableProperty]
    private string _blockIO = "-";

    public string Id { get; set; } = string.Empty;

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

public partial class DashboardViewModel : ObservableObject
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _statsCts;

    [ObservableProperty]
    private string _engineStatus = "Checking…";

    [ObservableProperty]
    private bool _engineHealthy;

    [ObservableProperty]
    private string _engineVersion = "-";

    [ObservableProperty]
    private int _runningContainers;

    [ObservableProperty]
    private int _totalContainers;

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private int _volumeCount;

    [ObservableProperty]
    private string _totalCpu = "0%";

    [ObservableProperty]
    private double _totalCpuValue;

    [ObservableProperty]
    private string _totalMemUsage = "-";

    public ObservableCollection<DashboardStatRow> LiveStats { get; } = new();

    public DashboardViewModel(IWslcService wslc, StatusMonitor monitor)
    {
        _wslc = wslc;
        _monitor = monitor;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _monitor.StatusChanged += OnStatusChanged;

        if (_monitor.Latest is not null)
        {
            Apply(_monitor.Latest);
        }
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e) => Apply(e);

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
        catch
        {
            // best effort
        }
    }

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
                catch
                {
                    // ignore
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

    private void ApplyStats(IReadOnlyList<ContainerStats> stats)
    {
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
    }

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
