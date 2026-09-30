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
using WslContainerDesktop.Helpers;
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
    private readonly IWslcSettingsFileService _wslcSettings;
    private readonly IEngineEventStream _events;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    private string _statusMessage = "Ready";

    // ---- Engine ----
    /// <summary>Bindable state for engine status text used by the view.</summary>
    [ObservableProperty]
    private string _engineStatusText = "Checking…";

    /// <summary>Bindable state for engine status brush used by the view.</summary>
    [ObservableProperty]
    private Brush _engineStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    /// <summary>Bindable state for engine version used by the view.</summary>
    [ObservableProperty]
    private string _engineVersion = "Unknown";

    // ---- Platform ----
    /// <summary>Bindable state for wsl version used by the view.</summary>
    [ObservableProperty]
    private string _wslVersion = "Unknown";

    /// <summary>Bindable state for kernel version used by the view.</summary>
    [ObservableProperty]
    private string _kernelVersion = "Unknown";

    // ---- Updates ----
    /// <summary>True while an update-availability check is in flight.</summary>
    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>True when a newer WSL version is available for the selected channel.</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>True when the installed WSL version is current for the selected channel.</summary>
    [ObservableProperty]
    private bool _isUpToDate;

    /// <summary>Latest WSL version available on the selected channel (from the release feed).</summary>
    [ObservableProperty]
    private string _latestWslVersion = "Unknown";

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
    /// <summary>Bindable state for memory limit used by the view.</summary>
    [ObservableProperty]
    private string _memoryLimit = "—";

    /// <summary>Bindable state for processor limit used by the view.</summary>
    [ObservableProperty]
    private string _processorLimit = "—";

    /// <summary>Bindable state for swap limit used by the view.</summary>
    [ObservableProperty]
    private string _swapLimit = "—";

    /// <summary>Bindable state for config note used by the view.</summary>
    [ObservableProperty]
    private string _configNote = string.Empty;

    /// <summary>Bindable state for settings file path used by the view.</summary>
    [ObservableProperty]
    private string _settingsFilePath = "Unknown";

    /// <summary>Bindable state for container storage location used by the view.</summary>
    [ObservableProperty]
    private string _containerStorageLocation = "Unknown";

    /// <summary>Bindable state for container storage size used by the view.</summary>
    [ObservableProperty]
    private string _containerStorageSize = "Unknown";

    /// <summary>Bindable state for storage edit available used by the view.</summary>
    [ObservableProperty]
    private bool _storageEditAvailable;

    /// <summary>Bindable state for storage edit note used by the view.</summary>
    [ObservableProperty]
    private string _storageEditNote = string.Empty;

    /// <summary>Bindable state for session cpu count used by the view.</summary>
    [ObservableProperty]
    private string _sessionCpuCount = "default";

    /// <summary>Bindable state for session memory size used by the view.</summary>
    [ObservableProperty]
    private string _sessionMemorySize = "default";

    /// <summary>Bindable state for session max storage size used by the view.</summary>
    [ObservableProperty]
    private string _sessionMaxStorageSize = "default";

    /// <summary>Bindable state for session default binding address used by the view.</summary>
    [ObservableProperty]
    private string _sessionDefaultBindingAddress = "default";

    /// <summary>Bindable state for credential store used by the view.</summary>
    [ObservableProperty]
    private string _credentialStore = "default";

    /// <summary>Registered distros and their run state.</summary>
    public ObservableCollection<WslDistroStatus> Distros { get; } = new();

    /// <summary>Cancels an in-flight update check when a newer one supersedes it (e.g. toggle change).</summary>
    private CancellationTokenSource? _updateCts;

    /// <summary>Creates the WslEngine view model and stores its injected services.</summary>
    public WslEngineViewModel(IWslSystemService system, IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings, IWslcSettingsFileService wslcSettings, IEngineEventStream events)
    {
        _system = system;
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _wslcSettings = wslcSettings;
        _events = events;
        _includePreRelease = settings.WslUpdatePreRelease;
    }

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
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
            await RefreshContainerEnvironmentAsync();

            var platform = await _system.GetPlatformInfoAsync();
            WslVersion = string.IsNullOrWhiteSpace(platform.WslVersion) ? "Unknown" : DisplayVersion(platform.WslVersion);
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

    /// <summary>Refreshes container environment state for the view model.</summary>
    private async Task RefreshContainerEnvironmentAsync()
    {
        try
        {
            var file = await _wslcSettings.ReadAsync();
            SettingsFilePath = file.SettingsFilePath;
            ContainerStorageLocation = file.UsesDefaultStorage
                ? $"Default ({file.DefaultStoragePath})"
                : file.EffectiveStoragePath;
            SessionCpuCount = Blank(file.CpuCount);
            SessionMemorySize = Blank(file.MemorySize);
            SessionMaxStorageSize = Blank(file.MaxStorageSize);
            SessionDefaultBindingAddress = Blank(file.DefaultBindingAddress);
            CredentialStore = Blank(file.CredentialStore);
            var bytes = await _wslcSettings.GetStorageDiskBytesAsync(file.EffectiveStoragePath);
            ContainerStorageSize = bytes is long b ? FormatHelpers.HumanSize(b) : "Unknown";
            StorageEditAvailable = file.DirectEditAvailable;
            StorageEditNote = file.DirectEditAvailable
                ? string.Empty
                : file.SettingsFileExists
                    ? "This version of Windows redirects the app's writes to the wslc settings file. Use Edit settings file to set session.storagePath."
                    : "The wslc settings file doesn't exist yet. Use Edit settings file (or run 'wslc settings' once) to create it, then return here.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read wslc settings: {ex.Message}");
            ContainerStorageLocation = "Unknown";
            ContainerStorageSize = "Unknown";
            StorageEditAvailable = false;
            StorageEditNote = "The wslc settings file could not be read.";
        }
    }

    /// <summary>
    /// Checks the WSL release feed for a newer version on the selected channel and updates the
    /// notice banner. Never throws — a failed check is surfaced as a soft warning.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        // Supersede any in-flight check so a slower earlier channel can't overwrite a newer one.
        _updateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _updateCts = cts;
        var token = cts.Token;

        IsCheckingUpdate = true;
        UpdateAvailable = false;
        IsUpToDate = false;
        try
        {
            var info = await _system.CheckForUpdateAsync(IncludePreRelease, token);
            if (token.IsCancellationRequested)
            {
                // A newer check took over; leave the UI to that one.
                return;
            }

            if (info.CheckFailed)
            {
                UpdateAvailable = false;
                IsUpToDate = false;
                UpdateMessage = string.Empty;
                LatestWslVersion = "Unknown";
                UpdateCheckFailed = true;
                UpdateCheckFailedMessage = info.FailureReason ?? "Could not check for WSL updates.";
                return;
            }

            UpdateCheckFailed = false;
            UpdateCheckFailedMessage = string.Empty;
            LatestWslVersion = string.IsNullOrWhiteSpace(info.LatestVersion) ? "Unknown" : DisplayVersion(info.LatestVersion);
            UpdateAvailable = info.UpdateAvailable;
            IsUpToDate = !info.UpdateAvailable;
            UpdateMessage = info.UpdateAvailable
                ? $"WSL {info.LatestVersion} is available (installed {info.InstalledVersion})."
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Superseded; ignore.
        }
        finally
        {
            if (_updateCts == cts)
            {
                IsCheckingUpdate = false;
                _updateCts = null;
            }

            cts.Dispose();
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

    /// <summary>Applies apply engine status state to bindable properties.</summary>
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

        await RestartSessionCoreAsync();
    }

    /// <summary>Helper for the restart session core workflow in this view model.</summary>
    private async Task RestartSessionCoreAsync()
    {
        IsBusy = true;
        StatusMessage = "Restarting the wslc session…";
        try
        {
            _events.Restart();
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

    /// <summary>Helper for the open settings file workflow in this view model.</summary>
    private async Task OpenSettingsFileAsync()
    {
        var result = await _wslc.OpenSettingsAsync();
        if (!result.Success)
        {
            await _dialogs.ShowMessageAsync("Open settings failed", result.ErrorText);
        }
    }

    /// <summary>Command handler for edit settings file actions triggered from the view.</summary>
    [RelayCommand]
    private Task EditSettingsFileAsync() => OpenSettingsFileAsync();

    /// <summary>Provides the change storage location operation to views or collaborating view models.</summary>
    public async Task ChangeStorageLocationAsync(string folder)
    {
        var validation = _wslcSettings.ValidateStoragePath(folder);
        if (!validation.IsValid)
        {
            await _dialogs.ShowMessageAsync("Can't use this folder", validation.Message ?? "Choose an empty folder.");
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            "Change container storage",
            "Use this empty folder for new wslc session storage?\n\n" +
            "Existing images, containers and volumes are not migrated. They remain in the old location until deleted. " +
            "The wslc session must restart before the new location is used.",
            "Change");
        if (!ok)
        {
            return;
        }

        if (!await TrySetStoragePathAsync(folder))
        {
            return;
        }

        await RefreshAsync();
        if (await _dialogs.ShowConfirmAsync("Restart session now?", "Restart the wslc session now so the new storage location is used? Running containers are stopped.", "Restart"))
        {
            await RestartSessionCoreAsync();
        }
    }

    /// <summary>Attempts the try set storage path operation and reports failure without crashing the UI.</summary>
    private async Task<bool> TrySetStoragePathAsync(string? folder)
    {
        try
        {
            await _wslcSettings.SetStoragePathAsync(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Not swallowed: the reason is shown to the user, who can fall back to Edit settings file.
            await _dialogs.ShowMessageAsync("Couldn't update container storage", ex.Message);
            return false;
        }
    }

    /// <summary>Command handler for reset storage location actions triggered from the view.</summary>
    [RelayCommand]
    private async Task ResetStorageLocationAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            "Reset container storage",
            "Reset wslc session.storagePath to default? Existing data in a custom location is not migrated or deleted. " +
            "The wslc session must restart before the default location is used.",
            "Reset");
        if (!ok)
        {
            return;
        }

        if (!await TrySetStoragePathAsync(null))
        {
            return;
        }

        await RefreshAsync();
        if (await _dialogs.ShowConfirmAsync("Restart session now?", "Restart the wslc session now so the default storage location is used? Running containers are stopped.", "Restart"))
        {
            await RestartSessionCoreAsync();
        }
    }

    /// <summary>Helper for the blank workflow in this view model.</summary>
    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    /// <summary>
    /// Shows versions in one style: <c>wsl --version</c> reports <c>3.0.1.0</c> while the release feed
    /// reports <c>3.0.1</c>, so a zero fourth part is dropped before the two are shown side by side.
    /// </summary>
    internal static string DisplayVersion(string version)
    {
        var value = version.Trim();
        return Version.TryParse(value, out var parsed) && parsed.Revision == 0
            ? $"{parsed.Major}.{parsed.Minor}.{parsed.Build}"
            : value;
    }
}
