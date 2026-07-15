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
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the "WSL engine" health page: surfaces the state of the underlying WSL virtual machine
/// that hosts the container engine (platform version, distros, resource limits from
/// <c>.wslconfig</c>) and offers recovery actions (restart the wslc session, shut WSL down).
/// </summary>
public partial class WslEngineViewModel : ObservableObject
{
    private readonly IWslSystemService _system;
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    // ---- Engine ----
    [ObservableProperty]
    private string _engineStatusText = "Checking…";

    [ObservableProperty]
    private Brush _engineStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    [ObservableProperty]
    private string _engineVersion = "Unknown";

    // ---- Platform ----
    [ObservableProperty]
    private string _wslVersion = "Unknown";

    [ObservableProperty]
    private string _kernelVersion = "Unknown";

    // ---- .wslconfig ----
    [ObservableProperty]
    private string _memoryLimit = "—";

    [ObservableProperty]
    private string _processorLimit = "—";

    [ObservableProperty]
    private string _swapLimit = "—";

    [ObservableProperty]
    private string _configNote = string.Empty;

    /// <summary>Registered distros and their run state.</summary>
    public ObservableCollection<WslDistroStatus> Distros { get; } = new();

    public WslEngineViewModel(IWslSystemService system, IWslcService wslc, StatusMonitor monitor, DialogService dialogs)
    {
        _system = system;
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusMessage = "Reading WSL status…";
        try
        {
            ApplyEngineStatus();

            var versionResult = await _wslc.GetVersionAsync();
            EngineVersion = versionResult.Success ? versionResult.StandardOutput.Trim() : "Unreachable";

            var platform = await _system.GetPlatformInfoAsync();
            WslVersion = string.IsNullOrWhiteSpace(platform.WslVersion) ? "Unknown" : platform.WslVersion;
            KernelVersion = string.IsNullOrWhiteSpace(platform.KernelVersion) ? "Unknown" : platform.KernelVersion;

            Distros.Clear();
            foreach (var distro in platform.Distros)
            {
                Distros.Add(distro);
            }

            var config = await _system.ReadConfigAsync();
            MemoryLimit = config.MemoryDisplay;
            ProcessorLimit = config.ProcessorsDisplay;
            SwapLimit = config.SwapDisplay;
            ConfigNote = config.Exists
                ? $"Configured in {config.ConfigPath}"
                : "No .wslconfig found — WSL is using its built-in defaults.";

            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            StatusMessage = "Error";
            await _dialogs.ShowMessageAsync("Failed to read WSL status", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyEngineStatus()
    {
        var latest = _monitor.Latest;
        if (latest is null)
        {
            EngineStatusText = "Checking…";
            EngineStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));
            return;
        }

        EngineStatusText = latest.Summary;
        var color = latest.Health switch
        {
            EngineHealth.Healthy => Color.FromArgb(255, 45, 200, 95),
            EngineHealth.Degraded => Color.FromArgb(255, 240, 180, 40),
            EngineHealth.Down => Color.FromArgb(255, 230, 70, 70),
            _ => Color.FromArgb(255, 150, 150, 150),
        };
        EngineStatusBrush = new SolidColorBrush(color);
    }

    /// <summary>
    /// Terminates the current wslc session. This is the documented recovery for the bind-mount
    /// slot exhaustion the app works around during Compose staging; it also stops all running
    /// containers, so it is confirmed first.
    /// </summary>
    [RelayCommand]
    private async Task RestartSessionAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Restart WSL session",
            "Restart the wslc session? This stops all running containers and releases the " +
            "engine's per-session bind-mount slots. Containers can be started again afterwards.",
            "Restart");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Restarting the wslc session…";
        try
        {
            var result = await _wslc.RestartSessionAsync();
            _monitor.RequestRefresh();
            if (result.Success)
            {
                await _dialogs.ShowMessageAsync("Session restarted",
                    "The wslc session was terminated. It restarts automatically on the next container action.");
            }
            else
            {
                await _dialogs.ShowMessageAsync("Restart failed", result.ErrorText);
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>Shuts down every WSL distro (and the container engine), releasing all virtual disks.</summary>
    [RelayCommand]
    private async Task ShutdownWslAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Shut down WSL",
            "Shut down all WSL distros? This stops the container engine and every running " +
            "container, and closes any other WSL sessions on this machine.",
            "Shut down");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Shutting WSL down…";
        try
        {
            var result = await _system.ShutdownWslAsync();
            _monitor.RequestRefresh();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync("Shutdown failed", result.ErrorText);
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }
}
