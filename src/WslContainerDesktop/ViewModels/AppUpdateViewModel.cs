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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// App-wide state of the in-app updater, shown as the notification bar at the top of the main
/// window and in the About section of Settings. Singleton; every member runs on the UI thread.
/// </summary>
public partial class AppUpdateViewModel : ObservableObject
{
    private static readonly TimeSpan[] LaunchRetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(20),
    ];

    private readonly IAppUpdateService _updates;
    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly ILogger<AppUpdateViewModel> _logger;

    private AppUpdateRelease? _release;
    private Task<AppUpdateRelease?>? _checkInFlight;
    private int _installAttempt;

    [ObservableProperty]
    private bool _isBarOpen;

    [ObservableProperty]
    private string _barTitle = string.Empty;

    [ObservableProperty]
    private string _barMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _barSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private bool _isBarClosable = true;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isUpdateButtonVisible;

    [ObservableProperty]
    private string _updateButtonText = "Update now";

    [ObservableProperty]
    private bool _isReleaseNotesVisible;

    /// <summary>One-line updater status for the Settings page.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    private bool _isBusy;

    public AppUpdateViewModel(IAppUpdateService updates, INotificationService notifications, ISettingsService settings, ILogger<AppUpdateViewModel> logger)
    {
        _updates = updates;
        _notifications = notifications;
        _settings = settings;
        _logger = logger;
        StatusText = updates.CurrentVersion is null
            ? "Updates are available only for the installed app."
            : "Not checked yet.";
    }

    /// <summary>Runs immediately before Windows closes the app to install an update.</summary>
    public Action? BeforeInstall { get; set; }

    /// <summary>
    /// Launch-time check: quiet unless a newer release exists, in which case the bar opens and a
    /// Windows notification is shown (so it is seen even when the app starts in the tray). A failed
    /// check is retried a few times, since at sign-in the network is often not ready yet.
    /// </summary>
    public async Task CheckOnLaunchAsync()
    {
        if (_updates.CurrentVersion is null)
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var release = await CheckCoreAsync();
                if (release is not null)
                {
                    _notifications.NotifyUpdateAvailable(release.DisplayVersion, _updates.CanInstallInPlace);
                }

                return;
            }
            catch (AppUpdateException ex)
            {
                // A failed background check is not worth interrupting anyone for; Settings shows why.
                _logger.LogInformation(ex, "Launch update check failed (attempt {Attempt}).", attempt + 1);
                if (attempt >= LaunchRetryDelays.Length)
                {
                    return;
                }
            }

            await Task.Delay(LaunchRetryDelays[attempt]);

            // Stop if the user turned the check off, or a manual check or update has since run.
            if (!_settings.CheckForUpdatesOnLaunch || _release is not null || IsBusy)
            {
                return;
            }
        }
    }

    /// <summary>Reports the result of an update the previous session was closed to install.</summary>
    public void ReportLaunchOutcome(AppUpdateOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            ShowBar(
                InfoBarSeverity.Success,
                "Update installed",
                $"WSL Container Desktop is now version {Display(outcome.CurrentVersion)}.");
            StatusText = $"Updated to version {Display(outcome.CurrentVersion)}.";
        }
        else
        {
            ShowBar(
                InfoBarSeverity.Warning,
                "The update did not finish",
                $"Version {Display(outcome.TargetVersion)} was not installed; you are still on {Display(outcome.CurrentVersion)}. You can try again from Settings.");
            StatusText = $"The update to {Display(outcome.TargetVersion)} did not finish.";
        }
    }

    /// <summary>The "Update now" button of the Windows notification.</summary>
    public async Task UpdateFromNotificationAsync()
    {
        if (_release is null)
        {
            await CheckForUpdatesAsync();
        }

        if (_release is not null && _updates.CanInstallInPlace)
        {
            await UpdateNowAsync();
        }
    }

    /// <summary>The "View release" button of the Windows notification.</summary>
    public async Task OpenReleaseNotesFromNotificationAsync()
    {
        if (_release is null)
        {
            await CheckForUpdatesAsync();
        }

        await ViewReleaseNotesAsync();
    }

    public void DismissBar() => IsBarOpen = false;

    private bool CanRun() => !IsBusy;

    /// <summary>Manual check from Settings; also re-opens the bar when an update is waiting.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            await CheckCoreAsync();
        }
        catch (AppUpdateException ex)
        {
            // CheckCoreAsync already put the reason in StatusText.
            _logger.LogInformation(ex, "Update check failed.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateNowAsync()
    {
        if (IsBusy || _release is not { } release || !_updates.CanInstallInPlace)
        {
            return;
        }

        IsBusy = true;
        IsBarClosable = false;
        IsUpdateButtonVisible = false;
        ShowBar(InfoBarSeverity.Informational, $"Downloading version {release.DisplayVersion}", "Starting download…");
        SetProgress(0);

        // Progress<T> posts each report to the UI queue, so a report can arrive after the install
        // attempt has already finished and overwrite its result. Only the running attempt may update.
        var attempt = ++_installAttempt;
        var progress = new Progress<AppUpdateProgress>(p =>
        {
            if (IsBusy && attempt == _installAttempt)
            {
                OnProgress(p);
            }
        });
        try
        {
            await _updates.InstallAsync(release, progress, BeforeInstall);

            // Normally unreachable: Windows closes the app once installation starts.
            ClearProgress();
            ShowBar(InfoBarSeverity.Success, "Update installed", $"Restart WSL Container Desktop to start using version {release.DisplayVersion}.");
            StatusText = $"Version {release.DisplayVersion} is installed; restart the app to use it.";
            _release = null;
            IsReleaseNotesVisible = false;
        }
        catch (AppUpdateException ex)
        {
            ClearProgress();
            ShowBar(InfoBarSeverity.Error, "Update failed", ex.Message);
            UpdateButtonText = "Try again";

            // Retrying a refused release would download the same package only to refuse it again.
            IsUpdateButtonVisible = ex.CanRetry;
            StatusText = $"The update to {release.DisplayVersion} failed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while installing update {Version}.", release.Version);
            ClearProgress();
            ShowBar(InfoBarSeverity.Error, "Update failed", "Something unexpected went wrong. The app log has the details.");
            UpdateButtonText = "Try again";
            IsUpdateButtonVisible = true;
            StatusText = $"The update to {release.DisplayVersion} failed.";
        }
        finally
        {
            IsBarClosable = true;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ViewReleaseNotesAsync()
    {
        if (_release is null)
        {
            return;
        }

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(_release.ReleasePage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the release page.");
        }
    }

    /// <summary>Shares one in-flight GitHub request between the launch check, Settings and toast clicks.</summary>
    private async Task<AppUpdateRelease?> CheckCoreAsync()
    {
        if (_checkInFlight is null)
        {
            StatusText = "Checking for updates…";
            _checkInFlight = _updates.CheckAsync();
        }

        try
        {
            var release = await _checkInFlight;
            if (release is not null)
            {
                OfferUpdate(release);
            }
            else
            {
                StatusText = $"You're up to date (version {Display(_updates.CurrentVersion)}).";
            }

            return release;
        }
        catch (AppUpdateException ex)
        {
            StatusText = ex.Message;
            throw;
        }
        finally
        {
            _checkInFlight = null;
        }
    }

    private void OfferUpdate(AppUpdateRelease release)
    {
        _release = release;
        StatusText = $"Version {release.DisplayVersion} is available.";
        if (IsBusy)
        {
            return;
        }

        IsReleaseNotesVisible = true;
        UpdateButtonText = "Update now";
        IsUpdateButtonVisible = _updates.CanInstallInPlace;
        ShowBar(
            InfoBarSeverity.Informational,
            $"Version {release.DisplayVersion} is available",
            _updates.CanInstallInPlace
                ? $"You have {Display(_updates.CurrentVersion)}. Updating downloads the new version, closes the app, installs it and reopens it. Running containers keep running."
                : $"You have {Display(_updates.CurrentVersion)}. This copy is not a signed release install, so it can't update itself; get the release from GitHub.");
    }

    private void OnProgress(AppUpdateProgress p)
    {
        switch (p.Phase)
        {
            case AppUpdatePhase.Downloading:
                var percent = (int)Math.Round((p.Fraction ?? 0) * 100);
                BarMessage = $"{percent}% downloaded.";
                SetProgress(percent);
                break;
            case AppUpdatePhase.Preparing:
                BarTitle = "Getting ready to install";
                BarMessage = "The download is complete. The update will install in less than a minute.";
                SetProgress(null);
                break;
            case AppUpdatePhase.Verifying:
                BarTitle = "Checking the download";
                BarMessage = "Making sure the package is a newer WSL Container Desktop from the same publisher.";
                SetProgress(null);
                break;
            case AppUpdatePhase.Installing:
                BarTitle = "Installing the update";
                BarMessage = "WSL Container Desktop will close, install the update and reopen in a moment.";
                SetProgress(null);
                break;
        }
    }

    private void ShowBar(InfoBarSeverity severity, string title, string message)
    {
        BarSeverity = severity;
        BarTitle = title;
        BarMessage = message;

        // Re-raise even if already open, so a bar the user closed comes back.
        IsBarOpen = false;
        IsBarOpen = true;
    }

    private void SetProgress(double? percent)
    {
        IsProgressVisible = true;
        IsProgressIndeterminate = percent is null;
        ProgressValue = percent ?? 0;
    }

    private void ClearProgress()
    {
        IsProgressVisible = false;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
    }

    private static string Display(Version? v) => v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
}
