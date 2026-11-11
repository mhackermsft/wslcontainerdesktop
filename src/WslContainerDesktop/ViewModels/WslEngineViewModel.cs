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
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => UiText.Translate(StatusMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EngineStatusTextDisplay => UiText.Translate(EngineStatusText);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EngineVersionDisplay => UiText.Translate(EngineVersion);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string WslVersionDisplay => UiText.Translate(WslVersion);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string KernelVersionDisplay => UiText.Translate(KernelVersion);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LatestWslVersionDisplay => UiText.Translate(LatestWslVersion);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdateMessageDisplay => UiText.Translate(UpdateMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdateCheckFailedMessageDisplay => UiText.Translate(UpdateCheckFailedMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConfigNoteDisplay => UiText.Translate(ConfigNote);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SettingsFilePathDisplay => UiText.Translate(SettingsFilePath);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContainerStorageLocationDisplay => UiText.Translate(ContainerStorageLocation);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContainerStorageSizeDisplay => UiText.Translate(ContainerStorageSize);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StorageEditNoteDisplay => UiText.Translate(StorageEditNote);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SessionCpuCountDisplay => UiText.Translate(SessionCpuCount);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SessionMemorySizeDisplay => UiText.Translate(SessionMemorySize);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SessionMaxStorageSizeDisplay => UiText.Translate(SessionMaxStorageSize);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SessionDefaultBindingAddressDisplay => UiText.Translate(SessionDefaultBindingAddress);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CredentialStoreDisplay => UiText.Translate(CredentialStore);

    private readonly IWslSystemService _system;
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IWslcSettingsFileService _wslcSettings;
    private readonly IEngineEventStream _events;
    private readonly WslcSessionContext _sessionContext;
    private readonly IWslcSessionSelectionService _sessionSelection;
    private readonly ILogger<WslEngineViewModel> _logger;
    private long _sessionRefreshVersion;
    private Exception? _sessionError;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    // ---- Engine ----
    /// <summary>Bindable state for engine status text used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineStatusTextDisplay))]
    private string _engineStatusText = "Checking…";

    /// <summary>Bindable state for engine status brush used by the view.</summary>
    [ObservableProperty]
    private Brush _engineStatusBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    /// <summary>Bindable state for engine version used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineVersionDisplay))]
    private string _engineVersion = "Unknown";

    // ---- Platform ----
    /// <summary>Bindable state for wsl version used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WslVersionDisplay))]
    private string _wslVersion = "Unknown";

    /// <summary>Bindable state for kernel version used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KernelVersionDisplay))]
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
    [NotifyPropertyChangedFor(nameof(LatestWslVersionDisplay))]
    private string _latestWslVersion = "Unknown";

    /// <summary>Message shown in the update notice InfoBar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateMessageDisplay))]
    private string _updateMessage = string.Empty;

    /// <summary>True when the last update check could not be completed (e.g. offline).</summary>
    [ObservableProperty]
    private bool _updateCheckFailed;

    /// <summary>Message shown when the update check failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateCheckFailedMessageDisplay))]
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
    [NotifyPropertyChangedFor(nameof(ConfigNoteDisplay))]
    private string _configNote = string.Empty;

    /// <summary>Bindable state for settings file path used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsFilePathDisplay))]
    private string _settingsFilePath = "Unknown";

    /// <summary>Bindable state for container storage location used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainerStorageLocationDisplay))]
    private string _containerStorageLocation = "Unknown";

    /// <summary>Bindable state for container storage size used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainerStorageSizeDisplay))]
    private string _containerStorageSize = "Unknown";

    /// <summary>Bindable state for storage edit available used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeStorage))]
    private bool _storageEditAvailable;

    /// <summary>Bindable state for storage edit note used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageEditNoteDisplay))]
    private string _storageEditNote = string.Empty;

    /// <summary>Bindable state for session cpu count used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionCpuCountDisplay))]
    private string _sessionCpuCount = "default";

    /// <summary>Bindable state for session memory size used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionMemorySizeDisplay))]
    private string _sessionMemorySize = "default";

    /// <summary>Bindable state for session max storage size used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionMaxStorageSizeDisplay))]
    private string _sessionMaxStorageSize = "default";

    /// <summary>Bindable state for session default binding address used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionDefaultBindingAddressDisplay))]
    private string _sessionDefaultBindingAddress = "default";

    /// <summary>Bindable state for credential store used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CredentialStoreDisplay))]
    private string _credentialStore = "default";

    /// <summary>Registered distros and their run state.</summary>
    public ObservableCollection<WslDistroStatus> Distros { get; } = new();

    /// <summary>Independent destination choices for assigning automatic actions.</summary>
    public ObservableCollection<WslcSessionChoice> AssignmentSessions { get; } = [];

    /// <summary>App-created and externally owned sessions shown in session management.</summary>
    public ObservableCollection<SessionListItem> SessionItems { get; } = [];
    /// <summary>Tracks session removal independently of the rebuilt session list.</summary>
    public SessionRemovalProgress Removal { get; }
    /// <summary>Tracks stopping and starting sessions across session-list refreshes.</summary>
    public SessionStopProgress StopProgress { get; }
    public bool CanManageSessions => Removal.CanEdit && StopProgress.CanEdit;
    public bool CanChangeStorage => CanManageSessions && StorageEditAvailable;
    public bool CanRestartSelectedSession => CanManageSessions &&
        SessionItems.Any(item => item.Generation is not null && !item.CanResumeDefault);

    /// <summary>Explicitly chosen destination for an automatic-action assignment.</summary>
    [ObservableProperty]
    private WslcSessionChoice? _selectedAssignmentSession;

    /// <summary>Whether the session discovery warning is visible.</summary>
    [ObservableProperty]
    private bool _hasSessionWarning;

    /// <summary>Session discovery warning projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SessionWarningDisplay => _sessionError is not null
            ? StopProgress.IsBusy
                ? UiText.Get("ManagedSession_StopListFailure",
                    "The session list could not be refreshed while the session action is running. Wait for it to finish, then select Refresh if the problem remains. {0}",
                    WslcSessionInventoryErrors.Describe(_sessionError))
                : UiText.Get("WslEngine_SessionLoadFailed", "Could not load WSLC sessions: {0}",
                    WslcSessionInventoryErrors.Describe(_sessionError))
            : string.Empty;

    private void RefreshSessionWarning()
    {
        HasSessionWarning = _sessionError is not null;
        OnPropertyChanged(nameof(SessionWarningDisplay));
    }

    /// <summary>True while <c>wsl --update</c> runs, so the administrator-prompt guidance shows.</summary>
    [ObservableProperty]
    private bool _isUpdatingWsl;

    /// <summary>True while the Windows administrator prompt is open during an update.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElevationTitle))]
    private bool _isAwaitingElevation;

    /// <summary>Title of the administrator-prompt guidance shown during an update.</summary>
    public string ElevationTitle => IsAwaitingElevation
        ? WslUpdateElevation.WaitingTitle
        : WslUpdateElevation.ExpectPromptTitle;

    /// <summary>How to find and approve the administrator prompt.</summary>
    public string ElevationGuidance => WslUpdateElevation.Guidance;

    /// <summary>Tooltip on Update now: an administrator prompt will follow.</summary>
    public string BeforeUpdateNote => WslUpdateElevation.BeforeUpdateNote;

    /// <summary>True when WSL confirms no distributions are installed.</summary>
    [ObservableProperty]
    private bool _noDistrosInstalled;

    /// <summary>True when the distribution list couldn't be read.</summary>
    [ObservableProperty]
    private bool _distroListUnavailable;

    /// <summary>Cancels an in-flight update check when a newer one supersedes it (e.g. toggle change).</summary>
    private CancellationTokenSource? _updateCts;

    /// <summary>Creates the WslEngine view model and stores its injected services.</summary>
    public WslEngineViewModel(IWslSystemService system, IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings, IWslcSettingsFileService wslcSettings, IEngineEventStream events, WslcSessionContext sessionContext, ManagedSessionService managedSessions, SessionRemovalProgress removal, SessionStopProgress stopProgress, IWslcSessionSelectionService sessionSelection, ILogger<WslEngineViewModel> logger)
    {
        _system = system;
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _wslcSettings = wslcSettings;
        _events = events;
        _sessionContext = sessionContext;
        _sessionSelection = sessionSelection;
        _logger = logger;
        SessionItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanRestartSelectedSession));
        ManagedSessions = managedSessions;
        Removal = removal;
        StopProgress = stopProgress;
        Removal.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionRemovalProgress.CanEdit))
            {
                OnPropertyChanged(nameof(CanManageSessions));
                OnPropertyChanged(nameof(CanChangeStorage));
                OnPropertyChanged(nameof(CanRestartSelectedSession));
            }
        };
        StopProgress.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionStopProgress.CanEdit))
            {
                OnPropertyChanged(nameof(CanManageSessions));
                OnPropertyChanged(nameof(CanChangeStorage));
                OnPropertyChanged(nameof(CanRestartSelectedSession));
            }
            if (args.PropertyName == nameof(SessionStopProgress.IsBusy))
                OnPropertyChanged(nameof(SessionWarningDisplay));
        };
        _sessionContext.DefaultSessionStopChanged += (_, _) =>
            _monitor.Dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CanRestartSelectedSession)));
        _sessionContext.EngineSettingChanged += (_, _) =>
            _monitor.Dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CanRestartSelectedSession)));
        _includePreRelease = settings.WslUpdatePreRelease;
        RefreshLocalizedAssignmentChoices();
        RebuildAssignmentChoices([]);
        UiText.LanguageChanged += (_, _) =>
        {
            RefreshLocalizedAssignmentChoices();
            OnPropertyChanged(nameof(SessionWarningDisplay));
        };
    }

    /// <summary>Owns sessions created by the app.</summary>
    public ManagedSessionService ManagedSessions { get; }
    /// <summary>Whether session restoration failed before all saved entries were checked.</summary>
    public bool HasSessionRestoreFailure => ManagedSessions.RestoreFailure is not null;
    /// <summary>Details of an unexpected session restoration failure.</summary>
    public string SessionRestoreFailure => UiText.Get("ManagedSession_RestoreFailure",
        "Saved sessions could not all be restored: {0}", ManagedSessions.RestoreFailure ?? string.Empty);

    private void RefreshLocalizedAssignmentChoices()
    {
        var assignmentNames = AssignmentSessions
            .Where(session => session.Name is not null)
            .Select(session => session.Name!)
            .ToArray();

        RebuildAssignmentChoices(assignmentNames);
    }

    private void RebuildAssignmentChoices(IEnumerable<string> activeNames)
    {
        var hadSelection = SelectedAssignmentSession is not null;
        var selectedName = SelectedAssignmentSession?.Name;
        AssignmentSessions.Clear();
        AssignmentSessions.Add(new WslcSessionChoice(
            null, UiText.Get("WslEngine_Session_Default", "Default CLI session")));
        foreach (var name in activeNames)
            AssignmentSessions.Add(new WslcSessionChoice(name, name));
        SelectedAssignmentSession = hadSelection
            ? AssignmentSessions.FirstOrDefault(choice => string.Equals(choice.Name, selectedName, StringComparison.Ordinal))
            : null;
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
            await RefreshWslcSessionsAsync();
            await RefreshContainerEnvironmentAsync();

            var platform = await _system.GetPlatformInfoAsync();
            WslVersion = string.IsNullOrWhiteSpace(platform.WslVersion) ? "Unknown" : DisplayVersion(platform.WslVersion);
            KernelVersion = string.IsNullOrWhiteSpace(platform.KernelVersion) ? "Unknown" : platform.KernelVersion;

            Distros.Clear();
            foreach (var distro in platform.Distros)
            {
                Distros.Add(distro);
            }

            DistroListUnavailable = Distros.Count == 0 && platform.DistroListUnavailable;
            NoDistrosInstalled = Distros.Count == 0 && !platform.DistroListUnavailable;

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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_375ebb8cbe29", "Failed to read WSL status"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        // Update availability is a network round-trip; run it after the page is populated so it
        // never blocks the core status from rendering.
        await CheckForUpdateAsync();
    }

    private async Task RefreshWslcSessionsAsync()
    {
        var refreshVersion = ++_sessionRefreshVersion;
        try
        {
            var systemInfo = await _wslc.GetSystemInfoAsync();
            if (refreshVersion != _sessionRefreshVersion)
                return;
            var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
            var activeNames = systemInfo.Server.Sessions.Select(session => session.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, defaultName, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RebuildAssignmentChoices(activeNames);
            RebuildSessionItems(systemInfo.Server.Sessions, defaultName);

            _sessionError = null;
            RefreshSessionWarning();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (refreshVersion != _sessionRefreshVersion)
                return;
            _sessionError = ex;
            SessionItems.Clear();
            foreach (var definition in ManagedSessions.Sessions.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                SessionItems.Add(new SessionListItem(definition.Name, definition, null, false, false,
                    ManagedSessions.FailureFor(definition.Id), false, inventoryUnavailable: true,
                    removing: ManagedSessions.IsRemoving(definition)));
            if (_sessionContext.IsDefaultSessionStopped(_settings.WslcPath, WslcSessionTarget.Default))
                SessionItems.Add(new SessionListItem(WslcSessionActionGuard.ResolveDefaultSessionName(), null, null,
                    false, false, null, true, inventoryUnavailable: true, defaultStoppedByUser: true));
            RefreshSessionWarning();
        }
    }

    private void RebuildSessionItems(IReadOnlyList<WslcSessionInfo> active, string defaultName)
    {
        var saved = ManagedSessions.Sessions;
        SessionItems.Clear();
        foreach (var definition in saved.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var matches = active.Where(item => item.Name == definition.Name).ToArray();
            var ownedGeneration = matches.SingleOrDefault(item => ManagedSessions.Owns(definition, item));
            var running = ownedGeneration is not null;
            SessionItems.Add(new SessionListItem(definition.Name, definition,
                ownedGeneration, running, matches.Length > 0,
                ManagedSessions.FailureFor(definition.Id), false, removing: ManagedSessions.IsRemoving(definition)));
        }
        var defaultStopped = _sessionContext.IsDefaultSessionStopped(_settings.WslcPath, WslcSessionTarget.Default);
        foreach (var session in active.Where(item => !saved.Any(definition =>
                     ManagedSessions.Owns(definition, item)))
                     .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            SessionItems.Add(new SessionListItem(session.Name, null, session, false, false, null,
                session.Name == defaultName, defaultStoppedByUser: defaultStopped && session.Name == defaultName));
        if (defaultStopped && !active.Any(session => session.Name == defaultName))
            SessionItems.Add(new SessionListItem(defaultName, null, null, false, false, null,
                true, defaultStoppedByUser: true));
    }

    /// <summary>Refreshes the session list after a create, remove, or start operation.</summary>
    public Task RefreshSessionListAsync()
    {
        OnPropertyChanged(nameof(HasSessionRestoreFailure));
        OnPropertyChanged(nameof(SessionRestoreFailure));
        return RefreshWslcSessionsAsync();
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
        var channel = IncludePreRelease ? UiText.Get("Resource_Text_94f2148dac71", " (including pre-release builds)") : string.Empty;
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_6a03ca0cd0bc", "Update WSL"),
            UiText.Get("Resource_Text_ec038ed93a63", "Download and install the latest WSL update{0}? This stops all running distros ", channel) +
            UiText.Get("Resource_Text_0e2a97ecc9c6", "and containers while the update is applied.\n\n") +
            UiText.Translate(WslUpdateElevation.BeforeUpdateNote) + UiText.Get("Resource_Text_db4bcc70ee6e", " If no prompt appears, look for a flashing ") +
            UiText.Get("Resource_Text_efe270c11a51", "shield icon on the taskbar, and check that it shows Microsoft Corporation as the ") +
            UiText.Get("Resource_Text_66ebcf92eed0", "verified publisher."),
            UiText.Get("Resource_Text_fb91e24fa52d", "Update"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        IsUpdatingWsl = true;
        StatusMessage = "Updating WSL…";
        try
        {
            var update = _system.UpdateWslAsync(IncludePreRelease);
            var sawPrompt = await WslUpdateElevation.WatchAsync(update, open =>
            {
                IsAwaitingElevation = open;
                StatusMessage = open ? WslUpdateElevation.WaitingProgress : WslUpdateElevation.InstallingProgress;
            });
            var result = await update;
            IsUpdatingWsl = false;
            _monitor.RequestRefresh();
            if (result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_01c18eb220c6", "WSL update"),
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? UiText.Get("Resource_Text_04271cf5765a", "WSL is up to date.")
                        : result.StandardOutput.Trim());
            }
            else
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4de04cd91a3d", "Update failed"),
                    sawPrompt ? $"{result.ErrorText}\n\n{UiText.Translate(WslUpdateElevation.DeclinedHint)}" : result.ErrorText);
            }
        }
        finally
        {
            IsUpdatingWsl = false;
            IsAwaitingElevation = false;
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
        var choice = await SessionOperationTarget.ChooseActiveAsync(_sessionSelection, _dialogs, _logger);
        if (choice is null) return;
        if (choice.Generation is null)
        {
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c52ad8ebd665", "Restart failed"),
                UiText.Get("WslEngine_RestartNoActiveSession", "There is no active session to restart."));
            return;
        }
        if (_sessionContext.IsDefaultSessionStopped(choice.ExecutablePath, choice.Target))
        {
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c52ad8ebd665", "Restart failed"),
                UiText.Get("ManagedSession_DefaultStartRequired",
                    "The default CLI session was stopped in this app. Open WSL engine and select Start for that session."));
            return;
        }
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_c981dceb2438", "Restart WSL session"),
            UiText.Get(
                "WslEngine_RestartSessionConfirmation",
                "Restart the {0}? This stops all running containers and releases the engine's per-session bind-mount slots. Containers can be started again afterwards.",
                choice.Target.Name ?? UiText.Get("WslEngine_Session_Default", "Default CLI session")),
            UiText.Get("Resource_Text_b134bd555a2f", "Restart"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        try
        {
            await _sessionSelection.VerifyAsync(choice);
            await RestartSessionCoreAsync(target: new(choice, _sessionSelection.CreateClient(choice), _sessionSelection));
        }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Could not restart the chosen session.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c52ad8ebd665", "Restart failed"), ex.Message);
        }
    }

    /// <summary>Helper for the restart session core workflow in this view model.</summary>
    private async Task RestartSessionCoreAsync(bool selectedSession = true, SessionOperationTarget? target = null)
    {
        IsBusy = true;
        StatusMessage = "Restarting the wslc session…";
        try
        {
            if (selectedSession && (target is null ||
                string.Equals(target.Selection.Target.Name, _sessionContext.Capture().Name, StringComparison.Ordinal)))
            {
                _events.Restart();
            }

            if (target is not null) await target.VerifyAsync();
            var result = selectedSession
                ? await (target?.Client ?? _wslc).RestartSessionAsync()
                : await _wslc.RestartDefaultSessionAsync();
            _monitor.RequestRefresh();
            if (result.Success)
            {
                var message = target?.Selection.Target.Name is null
                    ? UiText.Get("Resource_Text_c77cb4166afa", "The wslc session was terminated. It restarts automatically on the next container action.")
                    : UiText.Get("WslEngine_NamedSessionTerminated",
                        "The session {0} was terminated. Start it again from the session list if available, or from the app that created it.",
                        target.Selection.DisplayName);
                var title = target?.Selection.Target.Name is null
                    ? UiText.Get("Resource_Text_67036bb84e14", "Session restarted")
                    : UiText.Get("WslEngine_SessionTerminatedTitle", "Session stopped");
                await _dialogs.ShowLocalizedMessageAsync(title, message);
            }
            else
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_c52ad8ebd665", "Restart failed"), result.ErrorText);
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
            UiText.Get("Resource_Text_158cc9f59175", "Shut down WSL"),
            UiText.Get("Resource_Text_400850b7845e", "Shut down all WSL distros? This stops the container engine and every running ") +
            UiText.Get("Resource_Text_e65f3f93250a", "container, and closes any other WSL sessions on this machine."),
            UiText.Get("Resource_Text_e75039654b0d", "Shut down"));
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
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0b5e1b6073eb", "Shutdown failed"), result.ErrorText);
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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_a4b3c40db4fd", "Open settings failed"), result.ErrorText);
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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_da88488d0792", "Can't use this folder"), UiText.Translate(validation.Message ?? "Choose an empty folder."));
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_2a606e7bd51e", "Change container storage"),
            UiText.Get("Resource_Text_511e101f8c63", "Use this empty folder for new wslc session storage?\n\n") +
            UiText.Get("Resource_Text_1bdd56ae0fa8", "Existing images, containers and volumes are not migrated. They remain in the old location until deleted. ") +
            UiText.Get("Resource_Text_8b70a928916e", "The wslc session must restart before the new location is used."),
            UiText.Get("Resource_Text_64fbd995d3b6", "Change"));
        if (!ok)
        {
            return;
        }

        if (!await TrySetStoragePathAsync(folder))
        {
            return;
        }

        await RefreshAsync();
        if (await _dialogs.ShowConfirmAsync(
                UiText.Get("Resource_Text_fe232b5bb527", "Restart default session now?"),
                UiText.Get("Resource_Text_c6792f417830", "Restart the default CLI session now so the new storage location is used? Running containers in that session are stopped."),
                UiText.Get("Resource_Text_b134bd555a2f", "Restart")))
        {
            await RestartSessionCoreAsync(selectedSession: false);
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
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_384a147ad9e2", "Couldn't update container storage"), ex.Message);
            return false;
        }
    }

    /// <summary>Command handler for reset storage location actions triggered from the view.</summary>
    [RelayCommand]
    private async Task ResetStorageLocationAsync()
    {
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_cc95aa7e0053", "Reset container storage"),
            UiText.Get("Resource_Text_33b145c8b2b7", "Reset wslc session.storagePath to default? Existing data in a custom location is not migrated or deleted. ") +
            UiText.Get("Resource_Text_f4ad4c094dc2", "The wslc session must restart before the default location is used."),
            UiText.Get("Resource_Text_44c57abd888a", "Reset"));
        if (!ok)
        {
            return;
        }

        if (!await TrySetStoragePathAsync(null))
        {
            return;
        }

        await RefreshAsync();
        if (await _dialogs.ShowConfirmAsync(
                UiText.Get("Resource_Text_fe232b5bb527", "Restart default session now?"),
                UiText.Get("Resource_Text_4321ed28ed60", "Restart the default CLI session now so the default storage location is used? Running containers in that session are stopped."),
                UiText.Get("Resource_Text_b134bd555a2f", "Restart")))
        {
            await RestartSessionCoreAsync(selectedSession: false);
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

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        Removal.RefreshLanguage();
        StopProgress.RefreshLanguage();
        foreach (var item in SessionItems) item.RefreshLanguage();
    }
}
