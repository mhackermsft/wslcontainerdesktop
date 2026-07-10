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
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

public partial class ContainersViewModel : ObservableObject, IDisposable
{
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly ILogger<ContainersViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly LogStreamer _logStreamer;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _showAll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ContainerRowViewModel? _selected;

    // ---- Detail fields (populated from inspect on selection) ----
    [ObservableProperty]
    private string _detailCommand = "-";

    [ObservableProperty]
    private string _detailIp = "-";

    [ObservableProperty]
    private string _detailStarted = "-";

    [ObservableProperty]
    private string _detailNetwork = "-";

    [ObservableProperty]
    private string _detailWorkingDir = "-";

    [ObservableProperty]
    private string _detailInspectJson = string.Empty;

    // ---- Live stats (Stats tab) ----
    [ObservableProperty]
    private string _statCpu = "-";

    [ObservableProperty]
    private double _statCpuValue;

    [ObservableProperty]
    private string _statMem = "-";

    [ObservableProperty]
    private double _statMemValue;

    [ObservableProperty]
    private string _statMemUsage = "-";

    [ObservableProperty]
    private string _statNetIO = "-";

    [ObservableProperty]
    private string _statBlockIO = "-";

    [ObservableProperty]
    private int _statPids;

    public ObservableCollection<string> DetailEnvironment { get; } = new();

    public ObservableCollection<string> DetailMounts { get; } = new();

    public bool HasSelection => Selected is not null;

    /// <summary>Raised (on the UI thread) for each streamed log line of the selected container.</summary>
    public event Action<string>? LogLineReceived;

    /// <summary>Raised when the log view should be cleared (selection changed).</summary>
    public event Action? LogCleared;

    /// <summary>Raised when the current selection was removed (detail page should go back).</summary>
    public event Action? SelectionCleared;

    public ObservableCollection<ContainerRowViewModel> Containers { get; } = new();

    public ContainersViewModel(IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings, RegistryAuthRefresher authRefresher, ILogger<ContainersViewModel> logger)
    {
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _logStreamer = new LogStreamer(settings, _dispatcher);
        _logStreamer.LineReceived += line => LogLineReceived?.Invoke(line);

        _monitor.StatusChanged += OnStatusChanged;

        if (_monitor.Latest is not null)
        {
            Reconcile(_monitor.Latest.Containers);
        }
    }

    partial void OnShowAllChanged(bool value) => RequestRefresh();

    partial void OnSelectedChanged(ContainerRowViewModel? value)
    {
        // Reset detail + logs for the new selection.
        _logStreamer.Stop();
        LogCleared?.Invoke();
        DetailEnvironment.Clear();
        DetailMounts.Clear();
        DetailCommand = "-";
        DetailIp = "-";
        DetailStarted = "-";
        DetailNetwork = "-";
        DetailWorkingDir = "-";
        DetailInspectJson = string.Empty;

        if (value is null)
        {
            return;
        }

        _logStreamer.Start(value.Id);
        _ = LoadDetailsAsync(value.Id);
    }

    private async Task LoadDetailsAsync(string id)
    {
        try
        {
            var result = await _wslc.InspectContainerAsync(id);
            if (!result.Success)
            {
                return;
            }

            // Guard against a selection change while awaiting.
            if (Selected?.Id != id)
            {
                return;
            }

            var details = ContainerDetails.Parse(result.StandardOutput);
            DetailCommand = string.IsNullOrWhiteSpace(details.Command) ? "-" : details.Command;
            DetailIp = details.IpAddress;
            DetailStarted = details.StartedAt;
            DetailNetwork = details.NetworkMode;
            DetailWorkingDir = details.WorkingDir;
            DetailInspectJson = result.StandardOutput.Trim();

            DetailEnvironment.Clear();
            foreach (var e in details.Environment)
            {
                DetailEnvironment.Add(e);
            }

            DetailMounts.Clear();
            foreach (var m in details.Mounts)
            {
                DetailMounts.Add(m);
            }
        }
        catch (Exception ex)
        {
            // ignore inspect failures for detail panel
            _logger.LogDebug(ex, "Container inspect for the detail panel failed.");
        }
    }

    /// <summary>Stops the live log stream (call when navigating away from the page).</summary>
    public void StopStreaming() => _logStreamer.Stop();

    /// <summary>Restarts the live log stream for the current selection (e.g., page re-entry).</summary>
    public void ResumeStreaming()
    {
        if (Selected is not null && _logStreamer.CurrentContainerId != Selected.Id)
        {
            LogCleared?.Invoke();
            _logStreamer.Start(Selected.Id);
        }
    }

    private CancellationTokenSource? _statsCts;

    /// <summary>Starts polling live resource stats for the selected container.</summary>
    public void StartStatsPolling()
    {
        StopStatsPolling();

        var target = Selected;
        if (target is null)
        {
            return;
        }

        _statsCts = new CancellationTokenSource();
        var token = _statsCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var stats = await _wslc.GetStatsAsync(target.Id, token).ConfigureAwait(false);
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (Selected?.Id != target.Id)
                        {
                            return;
                        }

                        if (stats is null)
                        {
                            StatCpu = "-";
                            StatCpuValue = 0;
                            StatMem = "-";
                            StatMemValue = 0;
                            StatMemUsage = "-";
                            StatNetIO = "-";
                            StatBlockIO = "-";
                            StatPids = 0;
                        }
                        else
                        {
                            StatCpu = stats.CpuPercent;
                            StatCpuValue = stats.CpuValue;
                            StatMem = stats.MemPercent;
                            StatMemValue = stats.MemValue;
                            StatMemUsage = stats.MemUsage;
                            StatNetIO = stats.NetIO;
                            StatBlockIO = stats.BlockIO;
                            StatPids = stats.Pids;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // ignore transient stats errors
                    _logger.LogDebug(ex, "Transient container stats poll error.");
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

    /// <summary>
    /// Tears down the log stream (and its wsl.exe child) and any stats polling. Invoked when the
    /// DI container is disposed at app shutdown, since this view model is a singleton.
    /// </summary>
    public void Dispose()
    {
        StopStatsPolling();
        _logStreamer.Dispose();
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        Reconcile(e.Containers);
        StatusMessage = e.Summary;
    }

    private void Reconcile(IReadOnlyList<ContainerInfo> incoming)
    {
        var filtered = ShowAll
            ? incoming
            : incoming.Where(c => c.State == ContainerState.Running).ToList();

        var byId = filtered.ToDictionary(c => c.Id, StringComparer.Ordinal);

        // Remove rows that no longer exist.
        for (var i = Containers.Count - 1; i >= 0; i--)
        {
            if (!byId.ContainsKey(Containers[i].Id))
            {
                Containers.RemoveAt(i);
            }
        }

        // Update existing / add new, preserving incoming order.
        var existing = Containers.ToDictionary(r => r.Id, StringComparer.Ordinal);
        for (var i = 0; i < filtered.Count; i++)
        {
            var model = filtered[i];
            if (existing.TryGetValue(model.Id, out var row))
            {
                row.Update(model);
                var currentIndex = Containers.IndexOf(row);
                if (currentIndex != i && i < Containers.Count)
                {
                    Containers.Move(currentIndex, i);
                }
            }
            else
            {
                var newRow = new ContainerRowViewModel(model);
                if (i <= Containers.Count)
                {
                    Containers.Insert(i, newRow);
                }
                else
                {
                    Containers.Add(newRow);
                }
            }
        }

        // Resolve network names for rows that don't have one yet (via inspect, cached per row).
        foreach (var row in Containers.Where(r => !r.NetworkResolved))
        {
            _ = ResolveNetworkAsync(row);
        }

        // Probe GPU passthrough once per running container (via a cheap exec check).
        foreach (var row in Containers.Where(r => r.IsRunning && !r.GpuChecked))
        {
            _ = ResolveGpuAsync(row);
        }
    }

    private async Task ResolveGpuAsync(ContainerRowViewModel row)
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
            // leave GpuChecked true; a transient failure shouldn't hammer exec every poll
        }
    }

    private async Task ResolveNetworkAsync(ContainerRowViewModel row)
    {
        row.NetworkResolved = true;
        try
        {
            var result = await _wslc.InspectContainerAsync(row.Id);
            if (!result.Success)
            {
                row.NetworkResolved = false;
                return;
            }

            var details = ContainerDetails.Parse(result.StandardOutput);
            row.Network = string.IsNullOrWhiteSpace(details.NetworkMode) ? "-" : details.NetworkMode;
        }
        catch
        {
            row.NetworkResolved = false;
        }
    }

    private void RequestRefresh() => _monitor.RequestRefresh();

    [RelayCommand]
    private void Refresh() => RequestRefresh();

    [RelayCommand]
    private async Task RunAsync()
    {
        var dialog = new RunContainerDialog(_wslc, _settings.Registries);
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || dialog.Options is null)
        {
            return;
        }

        await ExecuteAsync($"Creating container from {dialog.Options.Image}…",
            async () =>
            {
                // `wslc run` auto-pulls if needed — refresh Azure auth just-in-time.
                await _authRefresher.EnsureFreshForReferenceAsync(dialog.Options.Image);
                return await _wslc.RunContainerAsync(dialog.Options);
            });
    }

    [RelayCommand]
    private Task StartAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Starting {row.Name}…", () => _wslc.StartContainerAsync(row.Id));

    [RelayCommand]
    private Task StopAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Stopping {row.Name}…", () => _wslc.StopContainerAsync(row.Id));

    [RelayCommand]
    private Task RestartAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Restarting {row.Name}…", () => _wslc.RestartContainerAsync(row.Id));

    [RelayCommand]
    private Task KillAsync(ContainerRowViewModel? row) =>
        row is null ? Task.CompletedTask :
        ExecuteAsync($"Killing {row.Name}…", () => _wslc.KillContainerAsync(row.Id));

    [RelayCommand]
    private async Task RemoveAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Remove container",
            $"Remove container \"{row.Name}\" ({row.ShortId})? This cannot be undone.",
            "Remove");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync($"Removing {row.Name}…", () => _wslc.RemoveContainerAsync(row.Id));

        // If we removed the currently-selected container, notify the detail page to navigate back.
        if (Selected?.Id == row.Id)
        {
            SelectionCleared?.Invoke();
        }
    }

    [RelayCommand]
    private async Task LogsAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Fetching logs for {row.Name}…";
        try
        {
            var result = await _wslc.GetLogsAsync(row.Id);
            var text = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? (string.IsNullOrWhiteSpace(result.StandardError) ? "(no output)" : result.StandardError)
                : result.StandardOutput;
            await _dialogs.ShowMessageAsync($"Logs · {row.Name}", text);
            StatusMessage = "Ready";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Terminal(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _wslc.OpenTerminal(row.Id);
        StatusMessage = $"Opened terminal for {row.Name}";
    }

    [RelayCommand]
    private void FollowLogs(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _wslc.FollowLogs(row.Id);
        StatusMessage = $"Streaming logs for {row.Name}";
    }

    [RelayCommand]
    private async Task InspectAsync(ContainerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.InspectContainerAsync(row.Id);
            await _dialogs.ShowMessageAsync($"Inspect · {row.Name}",
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenPort(ContainerRowViewModel? row)
    {
        var port = row?.PrimaryHttpPort;
        if (port is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = port.HostUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore browser launch failures
        }
    }

    [RelayCommand]
    private async Task PruneAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Prune containers",
            "Remove all stopped containers?",
            "Prune");
        if (!ok)
        {
            return;
        }

        await ExecuteAsync("Pruning stopped containers…", () => _wslc.PruneContainersAsync());
    }

    private async Task ExecuteAsync(string message, Func<Task<CommandResult>> action)
    {
        IsBusy = true;
        StatusMessage = message;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Operation failed", result.ErrorText);
                StatusMessage = "Operation failed";
            }
            else
            {
                StatusMessage = "Done";
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Operation failed", ex.Message);
            StatusMessage = "Operation failed";
        }
        finally
        {
            IsBusy = false;
            _monitor.RequestRefresh();
        }
    }
}
