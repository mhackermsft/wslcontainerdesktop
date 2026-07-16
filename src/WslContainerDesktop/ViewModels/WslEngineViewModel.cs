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
    private readonly ISettingsService _settings;

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

    // ---- Updates ----
    /// <summary>True while an update-availability check is in flight.</summary>
    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>True when a newer WSL version is available for the selected channel.</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>Message shown in the update notice InfoBar.</summary>
    [ObservableProperty]
    private string _updateMessage = string.Empty;

    /// <summary>True when the last update check could not be completed (e.g. offline).</summary>
    [ObservableProperty]
    private bool _updateCheckFailed;

    /// <summary>Message shown when the update check failed.</summary>
    [ObservableProperty]
    private string _updateCheckFailedMessage = string.Empty;

    /// <summary>Include pre-release WSL builds when checking for and applying updates.</summary>
    [ObservableProperty]
    private bool _includePreRelease;

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

    public WslEngineViewModel(IWslSystemService system, IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings)
    {
        _system = system;
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _includePreRelease = settings.WslUpdatePreRelease;
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

        // Update availability is a network round-trip; run it after the page is populated so it
        // never blocks the core status from rendering.
        await CheckForUpdateAsync();
    }

    /// <summary>
    /// Checks the WSL release feed for a newer version on the selected channel and updates the
    /// notice banner. Never throws — a failed check is surfaced as a soft warning.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        IsCheckingUpdate = true;
        try
        {
            var info = await _system.CheckForUpdateAsync(IncludePreRelease);
            if (info.CheckFailed)
            {
                UpdateAvailable = false;
                UpdateMessage = string.Empty;
                UpdateCheckFailed = true;
                UpdateCheckFailedMessage = info.FailureReason ?? "Could not check for WSL updates.";
                return;
            }

            UpdateCheckFailed = false;
            UpdateCheckFailedMessage = string.Empty;
            UpdateAvailable = info.UpdateAvailable;
            UpdateMessage = info.UpdateAvailable
                ? $"WSL {info.LatestVersion} is available (installed {info.InstalledVersion})."
                : string.Empty;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>Persists the pre-release preference and re-checks availability on the new channel.</summary>
    partial void OnIncludePreReleaseChanged(bool value)
    {
        _settings.WslUpdatePreRelease = value;
        _settings.Save();
        _ = CheckForUpdateAsync();
    }

    /// <summary>
    /// Applies the available WSL update via <c>wsl --update</c> (adding <c>--pre-release</c> when the
    /// pre-release channel is selected). This downloads and installs the update, so it is confirmed
    /// first; WSL is shut down as part of the update.
    /// </summary>
    [RelayCommand]
    private async Task UpdateWslAsync()
    {
        var channel = IncludePreRelease ? " (including pre-release builds)" : string.Empty;
        var ok = await _dialogs.ShowConfirmAsync(
            "Update WSL",
            $"Download and install the latest WSL update{channel}? This stops all running distros " +
            "and containers while the update is applied.",
            "Update");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Updating WSL…";
        try
        {
            var result = await _system.UpdateWslAsync(IncludePreRelease);
            _monitor.RequestRefresh();
            if (result.Success)
            {
                await _dialogs.ShowMessageAsync("WSL update",
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? "WSL is up to date."
                        : result.StandardOutput.Trim());
            }
            else
            {
                await _dialogs.ShowMessageAsync("Update failed", result.ErrorText);
            }
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
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
