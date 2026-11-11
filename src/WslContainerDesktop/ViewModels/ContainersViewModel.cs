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
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>View model for the main Containers page, coordinating container inventory, detail tabs, file browsing, logs, stats, bulk actions, and AI diagnosis.</summary>
/// <remarks>It listens to the shared <see cref="StatusMonitor"/> for live inventory, calls <c>wslc</c> through services, and keeps bound collections stable so selection and grouping do not flicker during polling.</remarks>
public partial class ContainersViewModel : ObservableObject, IDisposable
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => IsBusy ? UiText.Translate(StatusMessage)
        : _combined.IsBusy || _combined.HasRefreshFailure || !_combined.HasLoadedSelection ? _combined.StatusText
        : UiText.Get("Containers_CombinedSummary", "{0} containers across {1} active sessions.",
            Containers.Count, _combined.Sessions.Count(session => session.IsAvailable && session.IsSelected));

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilesStatusMessageDisplay => UiText.Translate(FilesStatusMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilesUnavailableReasonDisplay => UiText.Translate(FilesUnavailableReason);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ChangesStatusMessageDisplay => UiText.Translate(ChangesStatusMessage);

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AiConfidenceDisplay => UiText.Translate(AiConfidence);

    private const int MaxInlinePreviewBytes = 65_536;
    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly HealthWatchdog _watchdog;
    private readonly RestartPolicyWatchdog _restartWatchdog;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly IRunProfileStore _profiles;
    private readonly IComposeProjectStore _composeStore;
    private readonly IAiDiagnosticsService _aiDiagnostics;
    private readonly IAiAvailabilityService _aiAvailability;
    private readonly IAiCapabilityService _aiCapabilities;
    private readonly IWindowVisibility _visibility;
    private readonly ILogger<ContainersViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly LogStreamer _logStreamer;
    private readonly WslcSessionContext _sessionContext;
    private readonly LegacyPolicyOwnershipStore _legacyOwners;
    private readonly CombinedSessionViewModel _combined;
    private readonly WslcSessionContainerActions _sessionActions;
    private readonly WslcSessionResourceActions _sessionResources;
    private readonly WslcSessionClientFactory _sessionClients;
    private readonly WslcSessionSelectionService _sessionSelection;
    private readonly SessionAutomationCoordinator _sessionAutomation;
    private bool _ambiguousContainers;
    private bool _defaultIdentityUnavailable;
    private bool _managementUnavailable;
    private bool _containerPageVisible;
    private long _sizeRefreshGeneration;
    private CancellationTokenSource? _sizeRefreshCts;
    private readonly List<string> _sizeFailureSessions = [];

    /// <summary>Whether the container list has room for its table layout.</summary>
    [ObservableProperty]
    private bool _isWideLayout;

    /// <summary>Generated busy flag used by container commands that should not overlap.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private bool _isBusy;

    /// <summary>Status text displayed above the container list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    /// <summary>Generated filter flag: true shows stopped containers as well as running ones.</summary>
    [ObservableProperty]
    private bool _showAll = true;

    /// <summary>Generated flag that switches the list into bulk-selection mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    /// <summary>Generated count of rows selected for bulk actions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => UiText.Get("Resource_Text_e5084bffcc17", "{0} selected", SelectedCount);

    /// <summary>Generated selected row; changing it refreshes detail tabs and log streaming.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ContainerRowViewModel? _selected;

    // ---- Detail fields (populated from inspect on selection) ----
    /// <summary>Command line from container inspect for the details tab.</summary>
    [ObservableProperty]
    private string _detailCommand = "-";

    /// <summary>Container IP address from inspect, or a placeholder when unavailable.</summary>
    [ObservableProperty]
    private string _detailIp = "-";

    /// <summary>Human-readable start time shown in the details tab.</summary>
    [ObservableProperty]
    private string _detailStarted = "-";

    /// <summary>Primary network mode shown in the details tab.</summary>
    [ObservableProperty]
    private string _detailNetwork = "-";

    /// <summary>Working directory reported by container inspect.</summary>
    [ObservableProperty]
    private string _detailWorkingDir = "-";

    /// <summary>Writable-layer size shown in details.</summary>
    [ObservableProperty]
    private string _detailSizeRw = "-";

    /// <summary>Root filesystem size shown in details.</summary>
    [ObservableProperty]
    private string _detailSizeRootFs = "-";

    /// <summary>Raw inspect JSON displayed for advanced troubleshooting.</summary>
    [ObservableProperty]
    private string _detailInspectJson = string.Empty;

    /// <summary>Current container directory shown in the Files tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigateUp))]
    private string _filesCurrentPath = "/";

    /// <summary>Status or error text for the Files tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesStatusMessageDisplay))]
    private string _filesStatusMessage = "Open the Files tab to browse the container filesystem.";

    /// <summary>Generated busy flag for file-list and copy operations.</summary>
    [ObservableProperty]
    private bool _isFilesBusy;

    /// <summary>
    /// True when the file list can't be shown (container stopped, or listing failed, e.g. no shell).
    /// The Files tab then explains why and offers Download by path, which only needs native copy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesBrowseAvailable))]
    private bool _filesBrowseUnavailable;

    /// <summary>Inverse of <see cref="FilesBrowseUnavailable"/>; gates actions that need a listing shell (New folder).</summary>
    public bool FilesBrowseAvailable => !FilesBrowseUnavailable;

    /// <summary>Why the file list is unavailable, shown in the empty-state panel (the footer keeps later status).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesUnavailableReasonDisplay))]
    private string _filesUnavailableReason = string.Empty;

    // Downloading a symbolic link almost always means "the file it points to"; copying the bare link
    // would produce a Windows symlink or fail. -L resolves the link inside the container (source only).
    private const bool FollowSymlinksOnDownload = true;

    private const string StoppedFilesMessage =
        "This container isn't running, so its files can't be listed. You can still upload files.";

    /// <summary>Generated flag that controls whether streamed logs include timestamps.</summary>
    [ObservableProperty]
    private bool _logTimestamps;

    /// <summary>Generated selected filesystem entry in the Files tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private ContainerFileEntry? _selectedFile;

    // ---- Filesystem changes (Changes tab) ----
    /// <summary>Generated busy flag for the filesystem Changes tab.</summary>
    [ObservableProperty]
    private bool _isChangesBusy;

    /// <summary>Status or error text for the Changes tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangesStatusMessageDisplay))]
    private string _changesStatusMessage = "Open the Changes tab to compare the container against its image.";

    /// <summary>Generated busy flag for evidence collection or AI diagnosis requests.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiPanelVisible))]
    private bool _isAiBusy;

    /// <summary>Generated flag that shows the redacted evidence preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiPanelVisible))]
    private bool _aiHasPreview;

    /// <summary>Redacted evidence payload prepared for the AI diagnosis panel.</summary>
    [ObservableProperty]
    private string _aiPreviewPayload = string.Empty;

    /// <summary>Typed feedback for the AI diagnosis panel — informational while collecting/sending,
    /// success on preview/diagnosis/copy, error with friendly + technical detail on failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiPanelVisible))]
    private AiFeedback _diagnosisFeedback = AiFeedback.None;

    /// <summary>AI-generated high-level diagnosis summary.</summary>
    [ObservableProperty]
    private string _aiSummary = string.Empty;

    /// <summary>AI-generated likely cause text.</summary>
    [ObservableProperty]
    private string _aiLikelyCause = string.Empty;

    /// <summary>AI-generated explanation of the recommended fix.</summary>
    [ObservableProperty]
    private string _aiSuggestedFixDescription = string.Empty;

    /// <summary>AI-generated confidence label.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiConfidenceDisplay))]
    private string _aiConfidence = string.Empty;

    /// <summary>Generated flag indicating that diagnosis results are available.</summary>
    [ObservableProperty]
    private bool _aiHasDiagnosis;

    /// <summary>Generated flag that collapses or expands the AI diagnosis panel.</summary>
    [ObservableProperty]
    private bool _aiPanelCollapsed;

    /// <summary>Generated flag that enables diagnosis actions when an AI provider with tools is available.</summary>
    [ObservableProperty]
    private bool _isAiAvailable;

    /// <summary>Shows the AI diagnosis panel once evidence collection starts, not only once a
    /// preview exists, so "Collecting and redacting evidence…" is actually visible.</summary>
    public bool IsAiPanelVisible => AiHasPreview || IsAiBusy || DiagnosisFeedback.IsVisible;

    private AiPromptRequest? _pendingAiRequest;

    private bool _hasAttemptedChangesLoad;

    // ---- Live stats (Stats tab) ----
    /// <summary>Formatted CPU usage text for the Stats tab.</summary>
    [ObservableProperty]
    private string _statCpu = "-";

    /// <summary>Numeric CPU usage value for progress-style UI.</summary>
    [ObservableProperty]
    private double _statCpuValue;

    /// <summary>Formatted memory usage percentage for the Stats tab.</summary>
    [ObservableProperty]
    private string _statMem = "-";

    /// <summary>Numeric memory usage percentage for progress-style UI.</summary>
    [ObservableProperty]
    private double _statMemValue;

    /// <summary>Formatted current and limit memory usage text.</summary>
    [ObservableProperty]
    private string _statMemUsage = "-";

    /// <summary>Formatted network I/O text from container stats.</summary>
    [ObservableProperty]
    private string _statNetIO = "-";

    /// <summary>Formatted block I/O text from container stats.</summary>
    [ObservableProperty]
    private string _statBlockIO = "-";

    /// <summary>Process count from container stats.</summary>
    [ObservableProperty]
    private int _statPids;

    private bool _hasAttemptedFilesLoad;

    // Staging task for drag-out: when a file is selected, a background copy to a temp folder begins.
    private Task<string?>? _dragStagingTask;
    private ContainerFileEntry? _dragStagingEntry;

    /// <summary>Environment variables from inspect, shown in the details tab.</summary>
    public ObservableCollection<string> DetailEnvironment { get; } = new();

    /// <summary>Mount rows from inspect, shown in the details tab.</summary>
    public ObservableCollection<string> DetailMounts { get; } = new();

    /// <summary>Network attachments for the selected container.</summary>
    public ObservableCollection<NetworkAttachment> DetailNetworks { get; } = new();

    /// <summary>Filesystem entries for the current directory in the Files tab.</summary>
    public ObservableCollection<ContainerFileEntry> DetailFiles { get; } = new();

    /// <summary>Filesystem differences between the container and its image.</summary>
    public ObservableCollection<ContainerFsChange> DetailChanges { get; } = new();

    /// <summary>Citations describing which redacted evidence was sent for diagnosis.</summary>
    public ObservableCollection<string> AiEvidenceCitations { get; } = new();

    /// <summary>AI-suggested commands displayed as copyable guidance.</summary>
    public ObservableCollection<string> AiSuggestedCommands { get; } = new();

    /// <summary>AI-suggested file edits displayed as copyable guidance.</summary>
    public ObservableCollection<string> AiSuggestedFileEdits { get; } = new();

    /// <summary>True when a container row is selected.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>True when a file or directory is selected in the Files tab.</summary>
    public bool HasSelectedFile => SelectedFile is not null;

    /// <summary>True when the Files tab can navigate to a parent directory.</summary>
    public bool CanNavigateUp => FilesCurrentPath != "/";

    /// <summary>Returns the in-progress or completed temp-file staging task for the currently selected file entry.</summary>
    public Task<string?>? DragStagingTask => _dragStagingTask;

    /// <summary>Raised (on the UI thread) for each streamed log line of the selected container.</summary>
    public event Action<string>? LogLineReceived;

    /// <summary>Raised when the log view should be cleared (selection changed).</summary>
    public event Action? LogCleared;

    /// <summary>Raised when the current selection was removed (detail page should go back).</summary>
    public event Action? SelectionCleared;

    /// <summary>Container rows displayed by the list before grouping is applied.</summary>
    public ObservableCollection<ContainerRowViewModel> Containers { get; } = new();

    /// <summary>Search and sort state for the container list.</summary>
    public ResourceGridView<ContainerRowViewModel> Grid { get; }

    /// <summary>
    /// The Containers list grouped by compose project (with a trailing "Standalone" group for
    /// containers that aren't part of any project). Bound by the view via a grouped
    /// <c>CollectionViewSource</c>. Kept in sync with <see cref="Containers"/> on every reconcile.
    /// </summary>
    public ObservableCollection<ContainerGroup> Groups { get; } = new();

    /// <summary>Creates the Containers page model and subscribes to inventory, health, and AI availability updates.</summary>
    public ContainersViewModel(IWslcService wslc, StatusMonitor monitor, HealthWatchdog watchdog, RestartPolicyWatchdog restartWatchdog, DialogService dialogs, ISettingsService settings, RegistryAuthRefresher authRefresher, IRunProfileStore profiles, IComposeProjectStore composeStore, IAiDiagnosticsService aiDiagnostics, IAiAvailabilityService aiAvailability, IAiCapabilityService aiCapabilities, IWindowVisibility visibility, ILogger<ContainersViewModel> logger, WslcSessionContext sessionContext, LegacyPolicyOwnershipStore legacyOwners, CombinedSessionViewModel combined, WslcSessionContainerActions sessionActions, WslcSessionResourceActions sessionResources, WslcSessionClientFactory sessionClients, WslcSessionSelectionService sessionSelection, SessionAutomationCoordinator sessionAutomation)
    {
        _wslc = wslc;
        _monitor = monitor;
        _watchdog = watchdog;
        _restartWatchdog = restartWatchdog;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _profiles = profiles;
        _composeStore = composeStore;
        _aiDiagnostics = aiDiagnostics;
        _aiAvailability = aiAvailability;
        _aiCapabilities = aiCapabilities;
        _visibility = visibility;
        _logger = logger;
        _sessionContext = sessionContext;
        _legacyOwners = legacyOwners;
        _combined = combined;
        _sessionActions = sessionActions;
        _sessionResources = sessionResources;
        _sessionClients = sessionClients;
        _sessionSelection = sessionSelection;
        _sessionAutomation = sessionAutomation;
        _detailReads = new(4, ReadVisibleDetailAsync,
            ex => _logger.LogWarning(ex, "Unexpected visible container detail failure."));
        Grid = new(Containers, row =>
            [row.SessionName, row.Name, row.Id, row.Image, row.PortsDisplayText, row.NetworkDisplay,
             row.SizeShortDisplay, row.Created.ToString(), FormatHelpers.RelativeTime(row.Created), row.Project,
             row.State.ToString(), row.State.ToLocalizedDisplayString()],
            new("session", "ResourceGrid_Session", "Session", row => row.SessionName),
            new("name", "ResourceGrid_Name", "Name", row => row.Name),
            new("state", "ResourceGrid_Status", "Status", row => row.State.ToLocalizedDisplayString()),
            new("image", "ResourceGrid_Image", "Image", row => row.Image),
            new("ports", "ResourceGrid_Ports", "Ports", row => row.Model.Ports.Count == 0
                ? null : row.Model.Ports.Min(port => port.HostPort)),
            new("network", "ResourceGrid_Network", "Network", row => row.NetworkDisplay),
            new("size", "ResourceGrid_Size", "Size", row => row.Model.SizeRwBytes),
            new("created", "ResourceGrid_Created", "Created", row => row.Created));
        Grid.ProjectionUpdated += (_, _) => SyncGroups();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _logStreamer = new LogStreamer(settings, _dispatcher, sessionContext);
        _logStreamer.LineReceived += line => LogLineReceived?.Invoke(line);

        _monitor.StatusChanged += OnStatusChanged;
        _watchdog.HealthChanged += OnHealthChanged;
        _sessionAutomation.HealthChanged += OnSessionHealthChanged;
        _sessionAutomation.Changed += OnSessionHealthChanged;
        _aiAvailability.Changed += OnAiAvailabilityChanged;
        _sessionContext.SelectionChanged += OnSessionContextChanged;
        _sessionContext.EngineSettingChanged += OnSessionContextChanged;
        _combined.PropertyChanged += OnCombinedChanged;
        _visibility.Changed += OnWindowVisibilityChanged;
        IsAiAvailable = _aiAvailability.IsAvailable;

        ReconcileCombined();
    }

    partial void OnShowAllChanged(bool value) => ReconcileCombined();

    private void OnSessionContextChanged(object? sender, EventArgs args)
    {
        void Clear()
        {
            StopStatsPolling();
            _logStreamer.Stop();
            var hadSelection = Selected is not null;
            Selected = null;
            ReconcileCombined();
            OnPropertyChanged(nameof(ManagementDestination));
            if (hadSelection)
                SelectionCleared?.Invoke();
        }

        if (_dispatcher.HasThreadAccess)
            Clear();
        else
            _dispatcher.TryEnqueue(Clear);
    }

    partial void OnSelectedChanged(ContainerRowViewModel? value)
    {
        // Reset detail + logs for the new selection.
        _logStreamer.Stop();
        LogCleared?.Invoke();
        DetailEnvironment.Clear();
        DetailMounts.Clear();
        DetailNetworks.Clear();
        DetailCommand = "-";
        DetailIp = "-";
        DetailStarted = "-";
        DetailNetwork = "-";
        DetailWorkingDir = "-";
        DetailSizeRw = "-";
        DetailSizeRootFs = "-";
        DetailInspectJson = string.Empty;
        ResetFilesState(value);
        ResetChangesState(value);
        ResetAiState();

        if (value is null || !IsCurrentRow(value))
        {
            return;
        }

        UiSafe.Run(async () =>
        {
            await StartLogStreamAsync(value);
            await LoadDetailsAsync(value.Id);
        });
    }

    partial void OnLogTimestampsChanged(bool value) => RestartSelectedLogStream();

    private void RestartSelectedLogStream()
    {
        if (Selected is not { } selected || !IsCurrentRow(selected))
        {
            return;
        }

        UiSafe.Run(() => StartLogStreamAsync(selected));
    }

    private void ResetAiState()
    {
        IsAiBusy = false;
        AiHasPreview = false;
        AiPreviewPayload = string.Empty;
        AiPanelCollapsed = false;
        DiagnosisFeedback = AiFeedback.None;
        AiSummary = string.Empty;
        AiLikelyCause = string.Empty;
        AiSuggestedFixDescription = string.Empty;
        AiConfidence = string.Empty;
        AiHasDiagnosis = false;
        _pendingAiRequest = null;
        AiEvidenceCitations.Clear();
        AiSuggestedCommands.Clear();
        AiSuggestedFileEdits.Clear();
    }

    /// <summary>Builds classification context for the currently selected diagnosis provider.</summary>
    private AiErrorContext DiagnosisContext(string operation) =>
        AiErrorContext.For(_settings.AiProvider, operation);

    partial void OnSelectedFileChanged(ContainerFileEntry? value)
    {
        // When a non-directory file is selected, pre-copy it to a temp location so it can be
        // dragged out to Windows Explorer with minimal delay.
        _dragStagingTask = null;
        _dragStagingEntry = null;

        if (value is { IsDirectory: false } && Selected is { } container && IsCurrentRow(container))
        {
            _dragStagingEntry = value;
            _dragStagingTask = StageDragFileAsync(container, value);
        }
    }

    private async Task LoadDetailsAsync(string id)
    {
        var selected = Selected;
        if (selected is null || selected.Id != id || !IsCurrentRow(selected))
            return;
        try
        {
            var client = await ClientForRowAsync(selected);
            var result = await client.InspectContainerAsync(id, includeSize: true);
            if (!result.Success)
            {
                return;
            }

            // Guard against a selection change while awaiting.
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected))
            {
                return;
            }

            var details = ContainerDetails.Parse(result.StandardOutput);
            DetailCommand = string.IsNullOrWhiteSpace(details.Command) ? "-" : details.Command;
            DetailIp = details.IpAddress;
            DetailStarted = details.StartedAt;
            DetailNetwork = details.NetworkMode;
            DetailWorkingDir = details.WorkingDir;
            DetailSizeRw = details.SizeRwDisplay;
            DetailSizeRootFs = details.SizeRootFsDisplay;
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

            DetailNetworks.Clear();
            foreach (var network in details.Networks)
            {
                DetailNetworks.Add(network);
            }
        }
        catch (Exception ex)
        {
            // ignore inspect failures for detail panel
            _logger.LogDebug(ex, "Container inspect for the detail panel failed.");
        }
    }

    /// <summary>Loads the Files tab once for the selected running container.</summary>
    public Task EnsureFilesLoadedAsync()
    {
        if (_hasAttemptedFilesLoad || Selected is null)
        {
            return Task.CompletedTask;
        }

        return LoadFilesAsync(FilesCurrentPath);
    }

    /// <summary>Reloads the current Files tab directory.</summary>
    public Task RefreshFilesAsync() => LoadFilesAsync(FilesCurrentPath);

    /// <summary>Loads the Changes tab once for the selected container.</summary>
    public Task EnsureChangesLoadedAsync()
    {
        if (_hasAttemptedChangesLoad || Selected is null)
        {
            return Task.CompletedTask;
        }

        return LoadChangesAsync();
    }

    /// <summary>Reloads the Changes tab for the selected container.</summary>
    public Task RefreshChangesAsync() => LoadChangesAsync();

    private async Task LoadChangesAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        _hasAttemptedChangesLoad = true;

        if (!selected.IsRunning)
        {
            DetailChanges.Clear();
            ChangesStatusMessage = "The container must be running to compare it against its image.";
            return;
        }

        IsChangesBusy = true;
        ChangesStatusMessage = "Comparing the container against its image…";
        try
        {
            var client = await ClientForRowAsync(selected);
            var changes = await client.DiffContainerAsync(selected.Id, selected.Image);

            // Guard against a selection change while awaiting.
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected))
            {
                return;
            }

            DetailChanges.Clear();
            foreach (var change in changes)
            {
                DetailChanges.Add(change);
            }

            ChangesStatusMessage = changes.Count == 0
                ? "No filesystem changes relative to the image."
                : $"{changes.Count} change(s) relative to the image.";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
            {
                DetailChanges.Clear();
                ChangesStatusMessage = ex.Message;
            }

            _logger.LogDebug(ex, "Container filesystem diff failed.");
        }
        finally
        {
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
            {
                IsChangesBusy = false;
            }
        }
    }

    private void ResetChangesState(ContainerRowViewModel? selected)
    {
        _hasAttemptedChangesLoad = false;
        IsChangesBusy = false;
        DetailChanges.Clear();
        ChangesStatusMessage = selected?.IsRunning == true
            ? "Open the Changes tab to compare the container against its image."
            : "The container must be running to compare it against its image.";
    }

    /// <summary>Navigates the Files tab to the parent container directory.</summary>
    public Task NavigateUpAsync()
    {
        if (!CanNavigateUp)
        {
            return Task.CompletedTask;
        }

        return LoadFilesAsync(GetParentPath(FilesCurrentPath));
    }

    /// <summary>Opens a directory in the Files tab or previews/downloads a file entry.</summary>
    public async Task OpenFileEntryAsync(ContainerFileEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await LoadFilesAsync(entry.Path);
            return;
        }

        await OpenFileAsync(entry);
    }

    /// <summary>Copies the file to a temp location and opens it read-only with the OS default handler.</summary>
    /// <summary>Copies a selected container file to the app cache and opens it with the default Windows handler.</summary>
    public async Task OpenFileAsync(ContainerFileEntry? entry = null)
    {
        using var selection = _sessionContext.LockSelection();
        entry ??= SelectedFile;
        var selected = Selected;
        if (entry is null || entry.IsDirectory || selected is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        IsFilesBusy = true;
        FilesStatusMessage = $"Opening {entry.Name}…";
        try
        {
            var tempDir = GetTempDir(selected, entry.Path);
            var client = await ClientForRowAsync(selected);
            var result = await client.CopyFromContainerAsync(selected.Id, entry.Path, tempDir, followSymlinks: FollowSymlinksOnDownload);
            if (!result.Success)
            {
                FilesStatusMessage = DescribeFileCommandFailure(result, "Could not open this file.");
                return;
            }
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected))
                return;

            var localPath = ContainerDownloadPath.Create(entry.Path, tempDir).Target;
            ContainerDownloadPath.RequireUnlinkedAncestors(localPath, CancellationToken.None);
            if (!File.Exists(localPath))
            {
                FilesStatusMessage = "File was not found after copy.";
                return;
            }

            // Mark read-only to discourage edits that can't be written back.
            try
            {
                new FileInfo(localPath).IsReadOnly = true;
            }
            catch
            {
                // Ignore; read-only is advisory.
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = localPath,
                UseShellExecute = true,
            });

            FilesStatusMessage = $"Opened {entry.Name}";
        }
        catch (Exception ex)
        {
            FilesStatusMessage = ex.Message;
        }
        finally
        {
            IsFilesBusy = false;
        }
    }

    /// <summary>Copies the selected entry's container path to the Windows clipboard.</summary>
    /// <summary>Copies a container path from the Files tab to the Windows clipboard.</summary>
    public void CopyPathToClipboard(ContainerFileEntry? entry = null)
    {
        entry ??= SelectedFile;
        if (entry is null)
        {
            return;
        }

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(entry.Path);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        FilesStatusMessage = $"Copied path: {entry.Path}";
    }

    /// <summary>Prompts for a new name and renames the selected entry.</summary>
    /// <summary>Prompts for a new name and renames a file or directory inside the container.</summary>
    public async Task RenameAsync(ContainerFileEntry? entry = null)
    {
        using var selection = _sessionContext.LockSelection();
        entry ??= SelectedFile;
        var selected = Selected;
        if (entry is null || selected is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        var directory = FilesCurrentPath;
        var dialog = new Dialogs.SimpleInputDialog(UiText.Get("Resource_Text_d3f4cb898fbe", "Rename"), UiText.Get("Resource_Text_9e627c7b1aec", "New name"), entry.Name)
        {
            Value = entry.Name,
        };
        var dialogResult = await _dialogs.ShowDialogAsync(dialog);
        if (dialogResult != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        var newName = dialog.Value.Trim();
        if (string.IsNullOrEmpty(newName) || newName == entry.Name)
        {
            return;
        }

        var newPath = CombineContainerPath(directory, newName);
        await ExecuteAsync($"Renaming {entry.Name}…",
            async () => await (await ClientForRowAsync(selected)).RenamePathAsync(selected.Id, entry.Path, newPath));

        if (ReferenceEquals(Selected, selected))
            await LoadFilesAsync(FilesCurrentPath);
    }

    /// <summary>Prompts for a folder name and creates a new directory in the current path.</summary>
    /// <summary>Prompts for a folder name and creates it in the current container directory.</summary>
    public async Task CreateFolderAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        var directory = FilesCurrentPath;
        var dialog = new Dialogs.SimpleInputDialog(UiText.Get("Resource_Text_a711999b72aa", "New folder"), UiText.Get("Resource_Text_b2ce023b4255", "Folder name"), "new-folder");
        var dialogResult = await _dialogs.ShowDialogAsync(dialog);
        if (dialogResult != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var newPath = CombineContainerPath(directory, name);
        await ExecuteAsync($"Creating folder {name}…",
            async () => await (await ClientForRowAsync(selected)).CreateDirectoryAsync(selected.Id, newPath));

        if (ReferenceEquals(Selected, selected) && selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    /// <summary>Uploads one host file or folder into the current container directory.</summary>
    public async Task CopyIntoCurrentDirectoryAsync(string hostPath)
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || string.IsNullOrWhiteSpace(hostPath) || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        var directory = FilesCurrentPath;
        await ExecuteAsync($"Copying {Path.GetFileName(hostPath)} into {selected.Name}…",
            async () => await (await ClientForRowAsync(selected)).CopyToContainerAsync(selected.Id, hostPath, directory));

        if (ReferenceEquals(Selected, selected) && selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    /// <summary>Copies multiple host paths into the current container directory (used for multi-file drag-in).</summary>
    /// <summary>Uploads multiple host paths into the current container directory.</summary>
    public async Task CopyMultipleIntoCurrentDirectoryAsync(IEnumerable<string> hostPaths)
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        var paths = hostPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        IsFilesBusy = true;
        var directory = FilesCurrentPath;
        FilesStatusMessage = $"Uploading {paths.Count} item(s) to {selected.Name}…";
        var failed = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                var client = await ClientForRowAsync(selected);
                var result = await client.CopyToContainerAsync(selected.Id, path, directory);
                if (!result.Success)
                {
                    failed.Add($"{Path.GetFileName(path)}: {result.ErrorText}");
                }
            }

            if (failed.Count > 0)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_146781e43eff", "Upload incomplete"),
                    UiText.Get("Resource_Text_f64e32064699", "The following items could not be copied:\n{0}", string.Join('\n', failed)));
            }
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Container upload was interrupted for {Container} in {Session}.", selected.Name, selected.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_146781e43eff", "Upload incomplete"), ex.Message);
        }
        finally
        {
            IsFilesBusy = false;
        }

        if (ReferenceEquals(Selected, selected) && selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    /// <summary>Downloads the selected container file or directory to a host directory.</summary>
    public async Task CopySelectedFileOutAsync(string hostDirectory)
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        var file = SelectedFile;
        if (selected is null || file is null || string.IsNullOrWhiteSpace(hostDirectory) ||
            !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        await ExecuteAsync($"Copying {file.Name} to the host…",
            async () => await (await ClientForRowAsync(selected)).CopyFromContainerAsync(selected.Id, file.Path, hostDirectory, followSymlinks: FollowSymlinksOnDownload));
    }

    /// <summary>Downloads a known path without requiring a shell-backed directory listing.</summary>
    /// <summary>Downloads an explicitly supplied container path to a host directory.</summary>
    public async Task CopyPathOutAsync(string hostDirectory)
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        var dialog = new Dialogs.SimpleInputDialog(
            UiText.Get("Resource_Text_124ff13ed4b4", "Download by path"),
            UiText.Get("Resource_Text_526dc383e386", "Full path of a file or folder inside the container"),
            "/var/log/app.log")
        {
            Value = SelectedFile?.Path ?? FilesCurrentPath,
        };
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }
        var path = dialog.Value;
        await ExecuteAsync($"Copying from {selected.Name}...",
            async () => await (await ClientForRowAsync(selected)).CopyFromContainerAsync(selected.Id, path, hostDirectory, followSymlinks: FollowSymlinksOnDownload));
    }

    /// <summary>Confirms and deletes the selected file or directory inside the container.</summary>
    public async Task DeleteSelectedFileAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        var file = SelectedFile;
        if (selected is null || file is null || !await EnsureCurrentRowsAsync([selected]))
        {
            return;
        }

        var kind = file.IsDirectory ? UiText.Get("Resource_Text_afffdd08d81d", "folder") : UiText.Get("Resource_Text_971c419dd609", "file");
        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_291562a2b23c", "Delete from container"),
            UiText.Get("Resource_Text_26bcd52e50cf", "Delete {0} \"{1}\" from {2}? This cannot be undone.", kind, file.Name, selected.Name),
            UiText.Get("Resource_Text_f6fdbe48dc54", "Delete"));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync($"Deleting {file.Name}…",
            async () => await (await ClientForRowAsync(selected)).DeletePathAsync(selected.Id, file.Path));

        if (ReferenceEquals(Selected, selected) && selected.IsRunning)
        {
            await LoadFilesAsync(FilesCurrentPath);
        }
    }

    private async Task LoadFilesAsync(string path)
    {
        var selected = Selected;
        if (selected is null || !IsCurrentRow(selected))
        {
            return;
        }

        _hasAttemptedFilesLoad = true;
        var requestedPath = NormalizeContainerPath(path);

        if (!selected.IsRunning)
        {
            DetailFiles.Clear();
            SelectedFile = null;
            FilesCurrentPath = requestedPath;
            FilesUnavailableReason = StoppedFilesMessage;
            FilesStatusMessage = string.Empty;
            FilesBrowseUnavailable = true;
            return;
        }

        IsFilesBusy = true;
        // Set FilesCurrentPath before the await so that a subsequent navigation to a
        // different path can be detected as a stale response in the post-await guard below.
        FilesCurrentPath = requestedPath;
        FilesStatusMessage = $"Loading {requestedPath}…";
        try
        {
            var client = await ClientForRowAsync(selected);
            var result = await client.ListFilesAsync(selected.Id, requestedPath);

            // Guard against stale responses: verify both the container and the path still match.
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected) || FilesCurrentPath != requestedPath)
            {
                return;
            }

            if (!result.Success)
            {
                DetailFiles.Clear();
                SelectedFile = null;
                FilesUnavailableReason = DescribeFileCommandFailure(result, "Could not list this directory.");
                FilesStatusMessage = string.Empty;
                FilesBrowseUnavailable = true;
                return;
            }

            var (currentPath, entries) = ContainerFileEntry.ParseListing(result.StandardOutput, requestedPath);
            DetailFiles.Clear();
            foreach (var entry in entries)
            {
                DetailFiles.Add(entry);
            }

            FilesCurrentPath = currentPath;
            SelectedFile = null;
            FilesBrowseUnavailable = false;
            FilesStatusMessage = entries.Count == 0
                ? $"No files in {currentPath}"
                : $"{entries.Count} item(s) in {currentPath}";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
            {
                FilesUnavailableReason = ex.Message;
                FilesStatusMessage = string.Empty;
                DetailFiles.Clear();
                SelectedFile = null;
                FilesBrowseUnavailable = true;
            }
            _logger.LogDebug(ex, "Container file listing failed.");
        }
        finally
        {
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
                IsFilesBusy = false;
        }
    }

    private void ResetFilesState(ContainerRowViewModel? selected)
    {
        _hasAttemptedFilesLoad = false;
        IsFilesBusy = false;
        _dragStagingTask = null;
        _dragStagingEntry = null;
        DetailFiles.Clear();
        SelectedFile = null;
        FilesCurrentPath = "/";
        FilesBrowseUnavailable = selected is not null && !selected.IsRunning;
        FilesUnavailableReason = FilesBrowseUnavailable ? StoppedFilesMessage : string.Empty;
        FilesStatusMessage = selected?.IsRunning == true
            ? "Open the Files tab to browse the container filesystem."
            : string.Empty;
    }

    /// <summary>Background-copies the selected file to a temp folder for drag-out support.</summary>
    private async Task<string?> StageDragFileAsync(ContainerRowViewModel selected, ContainerFileEntry entry)
    {
        try
        {
            WslcSessionContext.SelectionSnapshot snapshot;
            string tempDir;
            Task<CommandResult> transfer;
            using (var selection = _sessionContext.LockSelection())
            {
                if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected))
                    return null;
                snapshot = _sessionContext.CaptureSnapshot();
                tempDir = GetTempDir(selected, entry.Path);
                var client = await ClientForRowAsync(selected);
                transfer = client.CopyFromContainerAsync(selected.Id, entry.Path, tempDir,
                    followSymlinks: FollowSymlinksOnDownload);
            }
            var result = await transfer;
            if (!result.Success)
            {
                _logger.LogWarning("Could not stage container file for dragging: {Error}", result.ErrorText);
                return null;
            }

            var localPath = ContainerDownloadPath.Create(entry.Path, tempDir).Target;
            ContainerDownloadPath.RequireUnlinkedAncestors(localPath, CancellationToken.None);
            return snapshot == _sessionContext.CaptureSnapshot() && ReferenceEquals(Selected, selected) &&
                IsCurrentRow(selected) && File.Exists(localPath) ? localPath : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stage container file for dragging.");
            return null;
        }
    }

    private const string TempRootFolderName = "container-file-previews";

    /// <summary>Root of the temp tree used to stage files opened/downloaded from containers.</summary>
    private static string TempRoot => Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, TempRootFolderName);

    private string GetTempDir(ContainerRowViewModel container, string path)
    {
        var target = container.SessionSource is { } source ? new WslcSessionTarget(source.SessionName) : _sessionContext.Capture();
        var key = ContainerPreviewKey.Create(_settings.WslcPath, target, container.Id, path);
        var dir = Path.Combine(TempRoot, key);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Best-effort deletion of the temp tree used to stage files opened/downloaded from containers.
    /// Files staged for a read-only "open" are marked read-only, so their attributes are cleared
    /// before deletion. Run at startup (rather than only on Dispose) so files left behind by a
    /// crash or forced exit are still reclaimed.
    /// </summary>
    /// <summary>Removes cached file previews created by the Files tab.</summary>
    public static void ClearTempFiles(ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(TempRoot))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // A locked/open file can't be reset; the delete below will skip it and we retry next launch.
                }
            }

            Directory.Delete(TempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear staged container temp files at {Path}.", TempRoot);
        }
    }

    private static string NormalizeContainerPath(string path) =>
        string.IsNullOrEmpty(path) ? "/" : path;

    private static string GetParentPath(string path)
    {
        var normalized = NormalizeContainerPath(path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized) || normalized == "/")
        {
            return "/";
        }

        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    private static string CombineContainerPath(string directory, string name)
    {
        var dir = string.IsNullOrEmpty(directory) ? "/" : directory;
        return dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name;
    }

    private static string DescribeFileCommandFailure(CommandResult result, string fallback)
    {
        var combined = (result.StandardError + "\n" + result.StandardOutput).Trim();
        if (combined.Contains("__WSLCD_NOT_DIR__", StringComparison.Ordinal))
        {
            return "The selected path is not a directory.";
        }

        if (combined.Contains("__WSLCD_NOT_FILE__", StringComparison.Ordinal))
        {
            return "The selected path is not a regular file.";
        }

        if (combined.Contains("__WSLCD_TOO_LARGE__", StringComparison.Ordinal))
        {
            return $"Preview is limited to files up to {MaxInlinePreviewBytes / 1024} KiB.";
        }

        if (combined.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("stopped", StringComparison.OrdinalIgnoreCase))
        {
            return StoppedFilesMessage;
        }

        return string.IsNullOrWhiteSpace(combined) ? fallback : combined;
    }

    /// <summary>Stops the live log stream (call when navigating away from the page).</summary>
    /// <summary>Stops following logs for the selected container.</summary>
    public void StopStreaming() => _logStreamer.Stop();

    /// <summary>Restarts the live log stream for the current selection (e.g., page re-entry).</summary>
    /// <summary>Restarts log streaming for the selected row when the log panel becomes visible.</summary>
    public void ResumeStreaming()
    {
        if (Selected is { } selected && IsCurrentRow(selected) && _logStreamer.CurrentContainerId != selected.Id)
        {
            UiSafe.Run(() => StartLogStreamAsync(selected));
        }
    }

    private async Task StartLogStreamAsync(ContainerRowViewModel selected)
    {
        try
        {
            await ClientForRowAsync(selected);
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected) || selected.SessionSource is not { } source)
                return;
            LogCleared?.Invoke();
            _logStreamer.Start(selected.Id, timestamps: LogTimestamps,
                session: new WslcSessionTarget(source.SessionName), executablePath: _combined.CurrentExecutablePath);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Could not start logs for {Container} in {Session}.", selected.Name, selected.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"), ex.Message);
        }
    }

    private CancellationTokenSource? _statsCts;

    /// <summary>Starts polling live resource stats for the selected container.</summary>
    /// <summary>Starts polling live stats for the selected running container.</summary>
    public void StartStatsPolling()
    {
        StopStatsPolling();

        var target = Selected;
        if (target is null || !IsCurrentRow(target))
        {
            return;
        }

        var snapshot = _sessionContext.CaptureSnapshot();
        var source = target.SessionSource!;
        var captured = WslcContainerActionTarget.Capture(source.Session, target.Model);
        _statsCts = new CancellationTokenSource();
        var token = _statsCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                VisibleRead? visible = null;
                try
                {
                    // Live stats only feed the visible page; skip them while nobody can see the window.
                    await _visibility.WhenViewableAsync(token).ConfigureAwait(false);
                    visible = new VisibleRead(_visibility, token);
                    var readToken = visible.Token;
                    {
                        using var selection = _sessionContext.LockSelection();
                        if (snapshot != _sessionContext.CaptureSnapshot())
                            break;
                        var stats = await _sessionActions.ReadStatsAsync(captured, readToken).ConfigureAwait(false);
                        _dispatcher.TryEnqueue(() =>
                        {
                            if (readToken.IsCancellationRequested || !_visibility.IsViewable ||
                                !ReferenceEquals(Selected, target) || !IsCurrentRow(target))
                                return;

                            if (stats is null)
                            {
                                ClearStats();
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
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && visible?.IsCancellationRequested == true)
                {
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Transient container stats poll error.");
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!token.IsCancellationRequested && ReferenceEquals(Selected, target) && IsCurrentRow(target))
                            ClearStats();
                    });
                }
                finally { visible?.Dispose(); }

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

    private void ClearStats()
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

    /// <summary>Stops the live stats poller and clears its cancellation token.</summary>
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
    /// <summary>Unsubscribes from shared services and stops background work owned by this view model.</summary>
    public void Dispose()
    {
        _containerPageVisible = false;
        StopStatsPolling();
        _sizeRefreshCts?.Cancel();
        _detailLifetime?.Cancel();
        _detailLifetime?.Dispose();
        ClearVisibleDetailRows();
        _detailReads.Dispose();
        _visibility.Changed -= OnWindowVisibilityChanged;
        _monitor.StatusChanged -= OnStatusChanged;
        _watchdog.HealthChanged -= OnHealthChanged;
        _sessionAutomation.HealthChanged -= OnSessionHealthChanged;
        _sessionAutomation.Changed -= OnSessionHealthChanged;
        _aiAvailability.Changed -= OnAiAvailabilityChanged;
        _sessionContext.SelectionChanged -= OnSessionContextChanged;
        _sessionContext.EngineSettingChanged -= OnSessionContextChanged;
        _combined.PropertyChanged -= OnCombinedChanged;
        _logStreamer.Dispose();
    }

    // Availability service raises on the UI thread; still marshal defensively.
    private void OnAiAvailabilityChanged(object? sender, EventArgs e)
        => _dispatcher.TryEnqueue(() => IsAiAvailable = _aiAvailability.IsAvailable);

    private void OnStatusChanged(object? sender, EngineStatusSnapshot e)
    {
        _managementUnavailable = e.Health == Tray.EngineHealth.Down;
        if (e.Health is Tray.EngineHealth.Healthy or Tray.EngineHealth.Degraded)
        {
            try
            {
                _combined.UpdateManagedContainers(e.Session, e.ExecutablePath, e.Containers);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Could not identify the selected session for live container updates.");
                _defaultIdentityUnavailable = true;
            }
        }
        OnPropertyChanged(nameof(CombinedWarning));
        StatusMessage = e.Summary;
    }

    private bool IsCurrentRow(ContainerRowViewModel row) =>
        row.SessionActionsAvailable && row.SessionSource is { } source &&
        Containers.Contains(row) && _combined.IsCurrentRow(source);

    private async Task<bool> EnsureCurrentRowsAsync(IReadOnlyCollection<ContainerRowViewModel> rows)
    {
        if (_combined.IsBusy)
        {
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
                UiText.Get("CombinedSession_RefreshBusy", "Another session refresh is already running."));
            return false;
        }
        if (rows.All(IsCurrentRow))
            return true;
        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
            UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        return false;
    }

    /// <summary>Warnings about incomplete inventories and unsafe row identities.</summary>
    public string CombinedWarning => string.Join(Environment.NewLine, new[]
    {
        _combined.WarningText,
        _ambiguousContainers ? UiText.Get("Containers_AmbiguousInventory",
            "Some containers have duplicate or missing identities; their actions are unavailable.") : string.Empty,
        _defaultIdentityUnavailable ? UiText.Get("CombinedSession_IdentityUnavailable",
            "The default session identity could not be determined; no action was started.") : string.Empty,
        _managementUnavailable ? UiText.Get("Containers_ManagementUnavailable",
            "The selected session is unavailable; its displayed containers may be out of date.") : string.Empty,
        _sizeFailureSessions.Count > 0 ? UiText.Get("Containers_SizeUnavailable",
            "Container sizes may be unavailable or out of date in: {0}", string.Join(", ", _sizeFailureSessions)) : string.Empty,
    }.Where(text => !string.IsNullOrWhiteSpace(text)));

    /// <summary>Session used by actions that are not tied to an existing row.</summary>
    public string ManagementDestination => UiText.Get("Containers_ManagementDestination",
        "Choose a session when pruning. Existing containers stay in their own session.");

    /// <summary>Marks whether the combined container list is visible for user-requested enrichment.</summary>
    public void SetContainerPageVisible(bool visible)
    {
        _containerPageVisible = visible;
        _detailLifetime?.Cancel();
        _detailLifetime?.Dispose();
        _detailLifetime = visible ? new CancellationTokenSource() : null;
        _detailReads.SetEnabled(visible && _visibility.IsViewable);
        if (!visible)
        {
            _sizeRefreshCts?.Cancel();
            ClearVisibleDetailRows();
        }
    }

    private CancellationTokenSource? _detailLifetime;

    private void OnWindowVisibilityChanged(object? sender, EventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _detailReads.SetEnabled(_containerPageVisible && _visibility.IsViewable);
            if (!_containerPageVisible || !_visibility.IsViewable) return;
            ReconcileCombined();
        });
    }

    /// <summary>Updates the container list layout from its available width.</summary>
    public void SetContainerListWidth(double width)
    {
        IsWideLayout = width >= 800;
        foreach (var row in Containers)
            row.IsWideLayout = IsWideLayout;
    }

    private void OnCombinedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.ContainerRows))
            ReconcileCombined();
        if (args.PropertyName is nameof(CombinedSessionViewModel.WarningText) or nameof(CombinedSessionViewModel.StatusText))
        {
            OnPropertyChanged(nameof(CombinedWarning));
            OnPropertyChanged(nameof(StatusMessageDisplay));
        }
    }

    private bool IsManagedRow(ContainerRowViewModel row)
    {
        if (row.SessionSource is not { } source)
            return false;
        try
        {
            return WslcSessionActionGuard.IsManaged(_sessionContext, source.Session,
                WslcSessionActionGuard.ResolveDefaultSessionName);
        }
        catch (InvalidOperationException ex)
        {
            if (!_defaultIdentityUnavailable)
                _logger.LogWarning(ex, "Could not identify the default container session.");
            _defaultIdentityUnavailable = true;
            return false;
        }
    }

    private void ReconcileCombined()
    {
        using (Grid.DeferRefresh())
        {
            var desired = ContainerSessionRows.Reconcile(Containers.ToArray(), _combined.ContainerRows, ShowAll,
                out _ambiguousContainers);
            foreach (var row in desired)
                row.IsWideLayout = IsWideLayout;
            if (!Containers.SequenceEqual(desired, ReferenceEqualityComparer.Instance))
                CollectionSync.ReplaceAll(Containers, desired);
            if (Selected is { } selected && !desired.Contains(selected, ReferenceEqualityComparer.Instance))
            {
                Selected = null;
                SelectionCleared?.Invoke();
            }
            _defaultIdentityUnavailable = false;
            ApplyImageNames();
            AssignProjects();
            Grid.Refresh();
            RefreshHealth();
        }
        foreach (var row in _visibleDetailRows.Where(row => !Containers.Contains(row)).ToArray())
            SetContainerRowVisible(row, false);
        foreach (var row in _visibleDetailRows.ToArray())
            SetContainerRowVisible(row, true);
        _detailReads.Refresh();
        OnPropertyChanged(nameof(CombinedWarning));
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    private async Task<IWslcService> ClientForRowAsync(ContainerRowViewModel row, CancellationToken ct = default,
        bool allowBusy = false)
    {
        if (_combined.IsBusy && !allowBusy)
            throw new InvalidOperationException(UiText.Get("CombinedSession_RefreshBusy", "Another session refresh is already running."));
        if (!IsCurrentRow(row) || row.SessionSource is not { } source)
            throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        return await _sessionActions.CreateVerifiedClientAsync(
            WslcContainerActionTarget.Capture(source.Session, row.Model), ct);
    }

    private static bool IsSessionActionFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or ArgumentException;

    private AutomationSessionOwner OwnerForRow(ContainerRowViewModel row)
    {
        if (row.SessionSource is not { } source)
            throw new InvalidOperationException(UiText.Get("CombinedSession_UnstableContainer",
                "The container identity is incomplete; refresh before taking action."));
        var defaultName = WslcSessionActionGuard.ResolveDefaultSessionName();
        if (string.Equals(source.SessionName, defaultName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(source.SessionName, defaultName, StringComparison.Ordinal))
            throw new InvalidOperationException(UiText.Get("CombinedSession_IdentityUnavailable",
                "The default session identity could not be determined; no action was started."));
        var target = string.Equals(source.SessionName, defaultName, StringComparison.Ordinal)
            ? WslcSessionTarget.Default : new WslcSessionTarget(source.SessionName);
        return AutomationSessionOwner.Capture(_settings.WslcPath, target);
    }

    private Task ShowSessionActionErrorAsync(Exception ex)
    {
        _logger.LogWarning(ex, "A session-qualified container action failed.");
        return _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"),
            ex is OperationCanceledException
                ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                : ex.Message);
    }

    private async Task<CommandResult> ExecuteSessionLifecycleAsync(ContainerRowViewModel row, WslcContainerAction action,
        bool removeAnonymousVolumes = false)
    {
        if (_combined.IsBusy)
            throw new InvalidOperationException(UiText.Get("CombinedSession_RefreshBusy", "Another session refresh is already running."));
        if (!IsCurrentRow(row) || row.SessionSource is not { } source)
            throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        var target = WslcContainerActionTarget.Capture(source.Session, row.Model);
        CommandResult result;
        try
        {
            result = await _sessionActions.ExecuteAsync(target, action, removeAnonymousVolumes: removeAnonymousVolumes);
        }
        catch
        {
            _combined.InvalidateSession(source.Session);
            throw;
        }
        if (!result.Success)
            _combined.InvalidateSession(source.Session);
        else
            await _combined.RefreshSessionAsync(source.Session);
        return result;
    }

    /// <summary>
    /// Shows an image name instead of a bare image ID in the list. Compose starts containers by
    /// exact image ID (so a project never silently picks up a different image), and the engine then
    /// reports that ID as the container's image. Display only: <see cref="ContainerRowViewModel.Model"/>
    /// keeps the real value for commands. Names come only from the same session's snapshot.
    /// </summary>
    private void ApplyImageNames()
    {
        foreach (var row in Containers)
        {
            if (row.SessionSource is not { } source || ImageIdKey(row.Model.Image) is not { } key)
                continue;
            var image = _combined.Inventory.Images.FirstOrDefault(candidate => candidate.Session == source.Session &&
                ImageIdKey(candidate.Value.Id) == key && !string.IsNullOrWhiteSpace(candidate.Value.Repository) &&
                candidate.Value.Repository != "<none>");
            if (image is not null)
                row.Image = image.Value.Reference;
        }
    }

    /// <summary>
    /// Returns the 12-character short form when <paramref name="image"/> is an image ID (bare hex or
    /// <c>sha256:</c>-prefixed), or null when it is already a name such as <c>nginx:alpine</c>.
    /// </summary>
    internal static string? ImageIdKey(string? image)
    {
        var value = (image ?? string.Empty).Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];
        return value.Length >= 12 && value.All(char.IsAsciiHexDigit) ? value[..12].ToLowerInvariant() : null;
    }

    /// <summary>
    /// Assigns each row's <see cref="ContainerRowViewModel.Project"/> by matching the container name
    /// against the deterministic <c>project_service</c> names of every stored compose project.
    /// </summary>
    private void AssignProjects()
    {
        string? defaultName;
        try { defaultName = WslcSessionActionGuard.ResolveDefaultSessionName(); }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not identify the default session for Compose grouping.");
            _defaultIdentityUnavailable = true;
            defaultName = null;
        }
        var projects = _composeStore.GetAll();
        foreach (var row in Containers)
        {
            if (row.SessionSource is not { } source || defaultName is null &&
                source.Session.Name.StartsWith("wslc-cli-", StringComparison.OrdinalIgnoreCase))
            {
                row.Project = null;
                continue;
            }
            var target = string.Equals(source.Session.Name, defaultName, StringComparison.Ordinal)
                ? WslcSessionTarget.Default : new WslcSessionTarget(source.Session.Name);
            row.Project = projects.FirstOrDefault(project =>
                project.SessionOwner?.Matches(_settings.WslcPath, target) == true &&
                project.Services.Any(service => project.ContainerNameFor(service.Name) == row.Name))?.Name;
        }
    }

    /// <summary>
    /// Reconciles <see cref="Groups"/> against the current <see cref="Containers"/> so the grouped
    /// list matches: one group per compose project (alphabetical), then a trailing "Standalone"
    /// group. Groups and rows are reused in place (matched by key/id) so scroll position and item
    /// recycling survive the periodic refresh.
    /// </summary>
    private void SyncGroups()
    {
        const string standaloneKey = "\uffff"; // sorts last

        // Desired: key -> ordered rows. Project groups keyed by project name; standalone last.
        var desired = new SortedDictionary<string, List<ContainerRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Grid.Items)
        {
            var key = string.IsNullOrEmpty(row.Project) ? standaloneKey : row.Project!;
            if (!desired.TryGetValue(key, out var list))
            {
                list = new List<ContainerRowViewModel>();
                desired[key] = list;
            }

            list.Add(row);
        }

        if (Grid.SelectedSort is not { Key.Length: > 0 })
        {
            foreach (var list in desired.Values)
            {
                list.Sort((a, b) =>
                {
                    var name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return name != 0 ? name : string.Compare(a.SessionName, b.SessionName, StringComparison.Ordinal);
                });
            }
        }

        // Remove groups no longer present.
        for (var i = Groups.Count - 1; i >= 0; i--)
        {
            if (!desired.ContainsKey(Groups[i].Key))
            {
                Groups.RemoveAt(i);
            }
        }

        // Add/reorder groups to match desired order, then sync each group's items.
        var desiredIndex = 0;
        foreach (var (key, rows) in desired)
        {
            var existing = Groups.FirstOrDefault(g => g.Key == key);
            if (existing is null)
            {
                var isProject = key != standaloneKey;
                var title = isProject ? key : "Standalone";
                existing = new ContainerGroup(key, title, isProject);
                Groups.Insert(desiredIndex, existing);
            }
            else
            {
                var currentIndex = Groups.IndexOf(existing);
                if (currentIndex != desiredIndex)
                {
                    Groups.Move(currentIndex, desiredIndex);
                }
            }

            SyncGroupItems(existing, rows);
            desiredIndex++;
        }
    }

    /// <summary>Updates a single group's rows in place to match <paramref name="desired"/> order.</summary>
    private static void SyncGroupItems(ContainerGroup group, List<ContainerRowViewModel> desired)
    {
        var desiredRows = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = group.Count - 1; i >= 0; i--)
        {
            if (!desiredRows.Contains(group[i]))
            {
                group.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            if (i < group.Count && ReferenceEquals(group[i], row))
            {
                continue;
            }

            var currentIndex = -1;
            for (var j = i; j < group.Count; j++)
            {
                if (ReferenceEquals(group[j], row))
                {
                    currentIndex = j;
                    break;
                }
            }

            if (currentIndex >= 0)
            {
                group.Move(currentIndex, i);
            }
            else
            {
                group.Insert(i, row);
            }
        }
    }

    /// <summary>Applies configured-flag and the latest watchdog health state onto each row.</summary>
    private void RefreshHealth()
    {
        foreach (var row in Containers)
        {
            if (row.SessionSource is not { } source)
            {
                row.HasHealthCheck = false;
                row.Health = ContainerHealthState.Unknown;
                row.HealthRestartCount = 0;
                row.HealthMaxRestarts = 0;
                row.HealthDetail = string.Empty;
                continue;
            }
            AutomationSessionOwner owner;
            try { owner = OwnerForRow(row); }
            catch (InvalidOperationException ex)
            {
                if (!_defaultIdentityUnavailable)
                    _logger.LogWarning(ex, "Could not identify the container's session for health.");
                _defaultIdentityUnavailable = true;
                row.HasHealthCheck = false;
                row.Health = ContainerHealthState.Unknown;
                row.HealthDetail = string.Empty;
                continue;
            }
            var states = IsManagedRow(row)
                ? _watchdog.Latest
                : _sessionAutomation.GetHealth(source.Session, owner.ExecutablePath);
            var snapshot = states?.Containers.FirstOrDefault(item =>
                string.Equals(item.ContainerName, row.Name, StringComparison.Ordinal) &&
                ContainerIdentity.ResolveId([row.Id], item.ContainerId) is not null &&
                item.ContainerGeneration == row.Model.StateChangedAt);
            row.HasHealthCheck = _settings.HealthChecks.Any(h => h.Enabled && h.IsValid &&
                h.DesiredHealth?.IsDisabled != true && string.Equals(h.ContainerName, row.Name, StringComparison.Ordinal) &&
                AutostartStateStore.SameOwner(_legacyOwners.OwnerFor(h), owner)) || snapshot is not null;
            if (snapshot is not null)
            {
                row.Health = snapshot.State;
                row.HealthRestartCount = snapshot.RestartCount;
                row.HealthMaxRestarts = snapshot.MaxRestarts;
                row.HealthDetail = snapshot.Detail;
            }
            else
            {
                row.Health = ContainerHealthState.Unknown;
                row.HealthRestartCount = 0;
                row.HealthMaxRestarts = 0;
                row.HealthDetail = string.Empty;
            }
        }
    }

    private void OnHealthChanged(object? sender, HealthSnapshot e) => RefreshHealth();
    private void OnSessionHealthChanged(object? sender, EventArgs e) => RefreshHealth();

    private readonly HashSet<ContainerRowViewModel> _pendingGpu = [];
    private readonly HashSet<ContainerRowViewModel> _pendingNetwork = [];
    private readonly HashSet<ContainerRowViewModel> _visibleDetailRows = [];
    private readonly VisibleReadQueue<(ContainerRowViewModel Row, bool Gpu)> _detailReads;

    /// <summary>Limits optional detail work to rows actually intersecting the list viewport.</summary>
    public void SetContainerRowVisible(ContainerRowViewModel row, bool visible)
    {
        visible &= _containerPageVisible;
        if (visible) _visibleDetailRows.Add(row);
        else _visibleDetailRows.Remove(row);
        _detailReads.SetVisible((row, false), visible && row.SessionActionsAvailable &&
            (!row.NetworkResolved || _pendingNetwork.Contains(row)));
        _detailReads.SetVisible((row, true), visible && row.SessionActionsAvailable && row.IsRunning &&
            (!row.GpuChecked || _pendingGpu.Contains(row)));
    }

    private void ClearVisibleDetailRows()
    {
        _visibleDetailRows.Clear();
        _detailReads.Clear();
    }

    private async Task ReadVisibleDetailAsync((ContainerRowViewModel Row, bool Gpu) item, CancellationToken ct)
    {
        if (item.Gpu && !item.Row.GpuChecked) await ResolveGpuAsync(item.Row, ct);
        else if (!item.Gpu && !item.Row.NetworkResolved) await ResolveNetworkAsync(item.Row, ct);
    }

    private async Task ResolveGpuAsync(ContainerRowViewModel row, CancellationToken ct)
    {
        if (!_pendingGpu.Add(row)) return;
        using var visible = new VisibleRead(_visibility, _detailLifetime?.Token ?? default, ct);
        row.GpuChecked = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(visible.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _visibility.WhenViewableAsync(timeout.Token);
            if (!_containerPageVisible)
            {
                row.GpuChecked = false;
                return;
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRow(row) || row.SessionSource is not { } source) return;
            var (hasGpu, gpuName) = await _sessionActions.ReadGpuInfoAsync(
                WslcContainerActionTarget.Capture(source.Session, row.Model), timeout.Token,
                row.Model.StateChangedAtKnown ? row.Model.StateChangedAt : 0);
            timeout.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRow(row)) return;
            row.HasGpu = hasGpu;
            row.GpuName = gpuName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve GPU details for {Container} in {Session}.", row.Name, row.SessionName);
            row.GpuChecked = false;
        }
        finally { _pendingGpu.Remove(row); }
    }

    private async Task ResolveNetworkAsync(ContainerRowViewModel row, CancellationToken ct)
    {
        if (!_pendingNetwork.Add(row)) return;
        using var visible = new VisibleRead(_visibility, _detailLifetime?.Token ?? default, ct);
        row.NetworkResolved = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(visible.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _visibility.WhenViewableAsync(timeout.Token);
            if (!_containerPageVisible)
            {
                row.NetworkResolved = false;
                return;
            }
            timeout.Token.ThrowIfCancellationRequested();
            var client = await ClientForRowAsync(row, timeout.Token, allowBusy: true);
            var result = await client.InspectContainerAsync(row.Id, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                _logger.LogDebug("Network detail inspect failed for {Container}: {Error}", row.Name, result.ErrorText);
                row.NetworkResolved = false;
                return;
            }

            var details = ContainerDetails.Parse(result.StandardOutput);
            if (!IsCurrentRow(row)) return;
            row.Network = string.IsNullOrWhiteSpace(details.NetworkMode) ? "-" : details.NetworkMode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve network details for {Container} in {Session}.", row.Name, row.SessionName);
            row.NetworkResolved = false;
        }
        finally { _pendingNetwork.Remove(row); }
    }

    private void RequestRefresh() => _monitor.RequestRefresh();

    /// <summary>Requests a fresh engine poll and refreshes size metadata.</summary>
    [RelayCommand]
    private async Task Refresh()
    {
        _sessionActions.InvalidateGpuInfo();
        foreach (var row in Containers) row.GpuChecked = false;
        RequestRefresh();
        await RefreshCombinedAsync();
    }

    /// <summary>Reads all active sessions on user navigation or refresh.</summary>
    public async Task RefreshCombinedAsync()
    {
        using var visible = new VisibleRead(_visibility, _detailLifetime?.Token ?? default);
        try
        {
            await _combined.RefreshAllAsync(visible.Token);
            if (!_combined.HasRefreshFailure && _containerPageVisible)
                await RefreshSizesAsync();
        }
        catch (OperationCanceledException) when (visible.IsCancellationRequested)
        {
            _logger.LogDebug("Container inventory refresh paused because the page is no longer visible.");
        }
    }

    /// <summary>Reuses recent inventories without starting an expensive size scan on navigation.</summary>
    public async Task RefreshOnNavigationAsync()
    {
        using var visible = new VisibleRead(_visibility, _detailLifetime?.Token ?? default);
        try
        {
            await _combined.RefreshForNavigationAsync(visible.Token);
            ReconcileCombined();
        }
        catch (OperationCanceledException) when (visible.IsCancellationRequested)
        {
            _logger.LogDebug("Container inventory refresh paused because the page is no longer visible.");
        }
    }

    /// <summary>Finds an unambiguous container for navigation without changing management scope.</summary>
    public async Task<ContainerRowViewModel?> FindContainerForNavigationAsync(
        string id, string? name, AutomationSessionOwner? owner = null,
        WslcContainerActionTarget? expectedTarget = null)
    {
        var selection = _sessionContext.CaptureSnapshot();
        await _combined.RefreshAllAsync();
        if (_combined.HasRefreshFailure || owner is null && selection != _sessionContext.CaptureSnapshot())
            return null;
        var all = ContainerSessionRows.Reconcile([], _combined.ContainerRows, showAll: true, out _);
        ContainerRowViewModel? match;
        if (owner is null)
            match = ContainerSessionRows.FindUniqueManaged(all, id, name, IsManagedRow);
        else
        {
            string? defaultName;
            try { defaultName = owner.SessionName is null ? WslcSessionActionGuard.ResolveDefaultSessionName() : null; }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Could not identify the notification's default session.");
                return null;
            }
            match = ContainerSessionRows.FindForOwner(all, id, name, owner, _combined.CurrentExecutablePath, defaultName);
        }
        if (match?.SessionSource is not { } source)
            return null;
        if (expectedTarget is not null && !ContainerSessionRows.MatchesCapturedIdentity(match, expectedTarget))
            return null;
        if (!ShowAll && match.State != ContainerState.Running)
            ShowAll = true;
        return Containers.SingleOrDefault(row => IsCurrentRow(row) && row.SessionSource?.Session == source.Session &&
            string.Equals(row.Id, match.Id, StringComparison.Ordinal));
    }

    /// <summary>Refreshes container size metadata without showing an error dialog on failure.</summary>
    public async Task RefreshSizesAsync()
    {
        _sizeRefreshCts?.Cancel();
        using var refresh = new CancellationTokenSource();
        using var visible = new VisibleRead(_visibility, refresh.Token, _detailLifetime?.Token ?? default);
        _sizeRefreshCts = refresh;
        var generation = ++_sizeRefreshGeneration;
        _sizeFailureSessions.Clear();
        OnPropertyChanged(nameof(CombinedWarning));
        var executable = _combined.CurrentExecutablePath;
        try
        {
            foreach (var group in Containers.Where(row => row.SessionSource is not null)
                         .GroupBy(row => row.SessionSource!.Session).ToArray())
            {
                if (visible.IsCancellationRequested || !_containerPageVisible)
                    break;
                var session = group.Key;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(visible.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await _visibility.WhenViewableAsync(timeout.Token);
                    if (!_containerPageVisible)
                        break;
                    await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                        session, executable, timeout.Token);
                    var client = _sessionClients.Create(session, executable, managed: false);
                    var sized = await client.ListContainersAsync(all: true, ct: timeout.Token, includeSize: true);
                    await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                        session, executable, timeout.Token);
                    if (generation != _sizeRefreshGeneration)
                        break;
                    foreach (var row in group)
                    {
                        if (!IsCurrentRow(row) || row.SessionSource is not { } source)
                            continue;
                        var target = WslcContainerActionTarget.Capture(source.Session, row.Model);
                        var matches = sized.Where(candidate => target.Matches(candidate)).Take(2).ToArray();
                        if (matches.Length == 1)
                            row.ApplySize(matches[0]);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                           System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Container size refresh failed for session {Session}.", session.Name);
                    if (!visible.IsCancellationRequested && generation == _sizeRefreshGeneration)
                        _sizeFailureSessions.Add(session.Name);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_sizeRefreshCts, refresh))
            {
                _sizeRefreshCts = null;
                OnPropertyChanged(nameof(CombinedWarning));
            }
        }
    }

    /// <summary>Opens the Run Container dialog for a new container.</summary>
    [RelayCommand]
    private Task RunAsync() => ShowRunDialogAsync(null, null);

    /// <summary>
    /// Prompts for a pasted <c>docker run</c> command (a top-level toolbar action), then opens the Run
    /// dialog pre-filled with the parsed options. The two dialogs are shown sequentially so no nested
    /// <see cref="Microsoft.UI.Xaml.Controls.ContentDialog"/> is ever open.
    /// </summary>
    [RelayCommand]
    private async Task ImportDockerRunAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var import = new ImportDockerRunDialog();
        var result = await _dialogs.ShowDialogAsync(import);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || import.Options is null)
        {
            return;
        }

        await ShowRunDialogAsync(import.Options, import.Warnings);
    }

    private async Task ShowRunDialogAsync(RunContainerOptions? prefillOptions, IReadOnlyList<string>? importWarnings)
    {
        using var selection = _sessionContext.LockSelection();
        WslcSessionSelection? destination;
        try
        {
            destination = await _sessionSelection.ChooseAsync();
            if (destination is null)
                return;
            await _sessionSelection.VerifyAsync(destination);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
            return;
        }
        var client = _sessionSelection.CreateClient(destination);
        var dialog = new RunContainerDialog(
            client, _settings.Registries, _profiles,
            prefillOptions: prefillOptions, importWarnings: importWarnings)
        {
            Title = UiText.Get("Containers_RunInManagementSession", "Run container in {0}", destination.DisplayName),
        };
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary || dialog.Options is null)
        {
            return;
        }

        if (await ExecuteAsync($"Creating container from {dialog.Options.Image}…",
            async () =>
            {
                // `wslc run` auto-pulls if needed — refresh Azure auth just-in-time.
                await _sessionSelection.VerifyAsync(destination);
                await _authRefresher.EnsureFreshForReferenceAsync(dialog.Options.Image, sessionClient: client,
                    verifyTarget: token => _sessionSelection.VerifyAsync(destination, token));
                await _sessionSelection.VerifyAsync(destination);
                return await client.RunContainerAsync(dialog.Options);
            }))
            await _combined.RefreshAllAsync();
    }

    /// <summary>Starts the selected container.</summary>
    [RelayCommand]
    private async Task StartAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;
        await ExecuteAsync($"Starting {row.Name}…", () => ExecuteSessionLifecycleAsync(row, WslcContainerAction.Start));
    }

    /// <summary>Stops the selected container after confirmation when appropriate.</summary>
    [RelayCommand]
    private async Task StopAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        await ExecuteAsync($"Stopping {row.Name}…", () => ExecuteSessionLifecycleAsync(row, WslcContainerAction.Stop));
    }

    /// <summary>Restarts the selected container.</summary>
    [RelayCommand]
    private async Task RestartAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        await ExecuteAsync($"Restarting {row.Name}…", () => ExecuteSessionLifecycleAsync(row, WslcContainerAction.Restart));
    }

    /// <summary>Prompts for a network and connects the selected container to it.</summary>
    [RelayCommand]
    private async Task ConnectNetworkAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || !await EnsureCurrentRowsAsync([selected]))
            return;

        IReadOnlyList<NetworkInfo> networks;
        try
        {
            var client = await ClientForRowAsync(selected);
            networks = NetworkDisplayList.Create(await client.ListNetworksAsync());
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_fce244da09aa", "Failed to load networks"), ex.Message);
            return;
        }

        var dialog = new ConnectNetworkDialog(networks, DetailNetworks.Select(n => n.Network));
        if (await _dialogs.ShowDialogAsync(dialog) != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
            dialog.Attachment is null)
        {
            return;
        }

        var id = selected.Id;
        await ExecuteAsync($"Connecting {selected.Name} to {dialog.Attachment.Network}…",
            async () => await (await ClientForRowAsync(selected)).ConnectNetworkAsync(dialog.Attachment, id));
        if (ReferenceEquals(Selected, selected))
        {
            await LoadDetailsAsync(id);
        }
    }

    /// <summary>Disconnects the selected container from the chosen network attachment.</summary>
    [RelayCommand]
    private async Task DisconnectNetworkAsync(NetworkAttachment? network)
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || network is null || !await EnsureCurrentRowsAsync([selected]))
            return;

        if (DetailNetworks.Count <= 1)
        {
            var ok = await _dialogs.ShowConfirmAsync(
                UiText.Get("Resource_Text_d8fcde886f51", "Disconnect last network"),
                UiText.Get("Resource_Text_4d11e46048eb", "Disconnect \"{0}\" from its only network \"{1}\"? The container will have no network attachments.", selected.Name, network.Network),
                UiText.Get("Resource_Text_ed28e0686e12", "Disconnect"));
            if (!ok)
            {
                return;
            }
        }

        var id = selected.Id;
        await ExecuteAsync($"Disconnecting {selected.Name} from {network.Network}…",
            async () => await (await ClientForRowAsync(selected)).DisconnectNetworkAsync(network.Network, id));
        if (ReferenceEquals(Selected, selected))
        {
            await LoadDetailsAsync(id);
        }
    }

    /// <summary>Force-stops the selected container after confirmation.</summary>
    [RelayCommand]
    private async Task KillAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        await ExecuteAsync($"Killing {row.Name}…", () => ExecuteSessionLifecycleAsync(row, WslcContainerAction.Kill));
    }

    /// <summary>Removes the selected container after confirmation.</summary>
    [RelayCommand]
    private async Task RemoveAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_15404127f80d", "Remove container"),
            UiText.Get("Containers_RemoveInSession",
                "Remove container \"{0}\" ({1}) from session \"{2}\"? This cannot be undone.", row.Name, row.ShortId, row.SessionName),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        // Show exactly which volumes are at stake. A generic warning invites either careless
        // deletion or needless retention of data the user actually wanted gone.
        ContainerVolumeImpact impact;
        try
        {
            var client = await ClientForRowAsync(row);
            impact = await new ContainerVolumeInspector(client).InspectAsync(row.Id);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
            return;
        }
        var removeAnonymous = false;
        var namedTargets = new List<WslcResourceActionTarget>();
        if (!impact.IsComplete)
        {
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("Containers_StorageUnknownTitle", "Volume usage is incomplete"),
                UiText.Get("Containers_StorageUnknown",
                    "Volume usage could not be verified. The container can be removed, but its volumes will be kept."));
        }
        else if (impact.HasDeletableData && row.SessionSource is { } source)
        {
            var deletable = impact.Anonymous.Concat(impact.OrphanedNamed).ToArray();
            var lines = deletable.Select(v => $"• {Shorten(v.Name)} → {v.Destination}");
            var kept = impact.SharedNamed.Count > 0
                ? UiText.Get("Resource_Text_7f43dc519669", "\n\nKept (still used by other containers): ") +
                  string.Join(", ", impact.SharedNamed.Select(v => $"{v.Name} [{string.Join(", ", v.OtherContainers)}]"))
                : string.Empty;
            var caution = impact.SharedAnonymous.Count > 0
                ? UiText.Get("Resource_Text_06cf19039cfe", "\n\n⚠ Some of these are mounted by other containers and would lose data too.")
                : string.Empty;
            try
            {
                foreach (var volume in impact.OrphanedNamed)
                    namedTargets.Add(await _sessionResources.PrepareVolumeRemovalAsync(source.Session, volume.Name));
            }
            catch (Exception ex) when (IsSessionActionFailure(ex))
            {
                await ShowSessionActionErrorAsync(ex);
                return;
            }
            if (await _dialogs.ShowConfirmAsync(
                    UiText.Get("Resource_Text_d73c0bb0fa10", "Also delete this container's volumes?"),
                    UiText.Get("Resource_Text_8c3c6433325f", "Removing \"{0}\" leaves this storage behind. Its data would be permanently deleted:\n\n", row.Name) +
                    string.Join('\n', lines) + kept + caution +
                    UiText.Get("Resource_Text_7e892fbf5dd3", "\n\nIf you are unsure, keep them — you can delete volumes later from the Volumes page."),
                    primaryText: UiText.Get("Resource_Text_3121cab8f91c", "Delete these volumes"),
                    closeText: UiText.Get("Resource_Text_f82ef289fb04", "Keep volumes")))
            {
                removeAnonymous = impact.Anonymous.Count > 0;
                try
                {
                    var client = await ClientForRowAsync(row);
                    var current = await new ContainerVolumeInspector(client).InspectAsync(row.Id);
                    if (!current.IsComplete || current.Signature() != impact.Signature())
                        throw new InvalidOperationException(UiText.Get("Containers_StorageChanged",
                            "The container's volume usage changed. Review it again before removing anything."));
                }
                catch (Exception ex) when (IsSessionActionFailure(ex))
                {
                    await ShowSessionActionErrorAsync(ex);
                    return;
                }
            }
            else
                namedTargets.Clear();
        }

        if (!await ExecuteAsync($"Removing {row.Name}…",
            () => ExecuteSessionLifecycleAsync(row, WslcContainerAction.Remove, removeAnonymous)))
            return;

        // A cached "tools supported" proof outlives the runtime that earned it, which would keep the
        // assistant offered after its backing container is gone. Re-observe instead of trusting it.
        await InvalidateAiCapabilitiesAsync();

        // Named volumes are not covered by `remove --volumes`, so delete the approved ones after the
        // container is gone and report any that could not be removed instead of failing silently.
        if (namedTargets.Count > 0)
        {
            var failed = new List<string>();
            for (var index = 0; index < namedTargets.Count; index++)
            {
                var target = namedTargets[index];
                try
                {
                    var result = await _sessionResources.RemoveAsync(target);
                    if (!result.Success)
                        failed.Add($"{target.Name}: {result.ErrorText}");
                }
                catch (Exception ex) when (IsSessionActionFailure(ex))
                {
                    _logger.LogWarning(ex, "Could not remove volume {Volume} in session {Session}.", target.Name, target.Session.Name);
                    failed.Add($"{target.Name}: {ex.Message}");
                    if (ex is OperationCanceledException)
                    {
                        failed.Add(UiText.Get("ResourceSession_BulkInterrupted",
                            "The current action's outcome may be unknown; {0} later item(s) were not attempted.",
                            namedTargets.Count - index - 1));
                        break;
                    }
                }
            }
            await _combined.RefreshSessionAsync(namedTargets[0].Session);
            if (failed.Count > 0)
            {
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_606d1a1317c3", "Some volumes were kept"),
                    UiText.Get("Resource_Text_a7652bb6962c", "The container was removed, but these volumes could not be deleted: {0}. ", string.Join(", ", failed)) +
                    UiText.Get("Resource_Text_a28caef66f74", "You can retry from the Volumes page."));
            }
        }

        static string Shorten(string name) =>
            name.Length == 64 ? name[..12] + UiText.Get("Resource_Text_c05fe6c2ab87", "… (unnamed)") : name;

        // If we removed the currently-selected container, notify the detail page to navigate back.
        if (ReferenceEquals(Selected, row))
        {
            SelectionCleared?.Invoke();
        }

    }

    /// <summary>Exports a container to the chosen archive path.</summary>
    public async Task ExportContainerAsync(ContainerRowViewModel? row, string outputPath)
    {
        using var selection = _sessionContext.LockSelection();
        row ??= Selected;
        if (row is null || string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }
        if (!await EnsureCurrentRowsAsync([row]))
            return;

        await ExecuteAsync($"Exporting {row.Name} filesystem…",
            async () => await (await ClientForRowAsync(row)).ExportContainerAsync(row.Id, outputPath));
        if (StatusMessage == "Done")
        {
            StatusMessage = $"Exported {row.Name} to {Path.GetFileName(outputPath)}. To make an image from it, use Images → Import → Create image from exported files….";
        }
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            SelectedCount = 0;
        }
    }

    /// <summary>Starts every selected stopped container, then exits selection mode.</summary>
    /// <summary>Starts every selected stopped container.</summary>
    public async Task BulkStartAsync(IReadOnlyList<ContainerRowViewModel> rows)
    {
        using var selection = _sessionContext.LockSelection();
        var items = (rows ?? Array.Empty<ContainerRowViewModel>()).Where(r => r is not null && !r.IsRunning).ToList();
        if (items.Count == 0)
        {
            IsSelectionMode = false;
            return;
        }
        if (!await EnsureCurrentRowsAsync(items))
            return;

        await RunBulkAsync(UiText.Get("Resource_Text_259af9c750d4", "Starting {0} container(s)…", items.Count), items,
            r => ExecuteSessionLifecycleAsync(r, WslcContainerAction.Start));
    }

    /// <summary>Stops every selected running container (suppressing exit toasts/restarts), then exits selection mode.</summary>
    /// <summary>Stops every selected running container.</summary>
    public async Task BulkStopAsync(IReadOnlyList<ContainerRowViewModel> rows)
    {
        using var selection = _sessionContext.LockSelection();
        var items = (rows ?? Array.Empty<ContainerRowViewModel>()).Where(r => r is not null && r.IsRunning).ToList();
        if (items.Count == 0)
        {
            IsSelectionMode = false;
            return;
        }
        if (!await EnsureCurrentRowsAsync(items))
            return;

        await RunBulkAsync(UiText.Get("Resource_Text_558c85c42670", "Stopping {0} container(s)…", items.Count), items,
            r => ExecuteSessionLifecycleAsync(r, WslcContainerAction.Stop));
    }

    /// <summary>Removes every selected container after one confirmation, then exits selection mode.</summary>
    /// <summary>Removes selected containers after a single confirmation.</summary>
    public async Task BulkRemoveAsync(IReadOnlyList<ContainerRowViewModel> rows)
    {
        using var selection = _sessionContext.LockSelection();
        var items = (rows ?? Array.Empty<ContainerRowViewModel>()).Where(r => r is not null).ToList();
        if (items.Count == 0)
        {
            return;
        }
        if (!await EnsureCurrentRowsAsync(items))
            return;

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_18ff715ccce4", "Remove containers"),
            UiText.Get("Containers_CombinedRemoveConfirmation",
                "Remove {0} container(s) from their listed sessions? This cannot be undone. Some removals may fail independently.\n\n{1}",
                items.Count, BulkNames(items.Select(r => $"{r.SessionName}: {r.Name}"))),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        var removingSelected = Selected is not null && items.Any(r => ReferenceEquals(r, Selected));

        await RunBulkAsync(UiText.Get("Resource_Text_5e90eeae3d81", "Removing {0} container(s)…", items.Count), items,
            r => ExecuteSessionLifecycleAsync(r, WslcContainerAction.Remove), showConfirm: false);
        await InvalidateAiCapabilitiesAsync();

        if (removingSelected)
        {
            SelectionCleared?.Invoke();
        }
    }

    private async Task RunBulkAsync(string message, IReadOnlyList<ContainerRowViewModel> items, Func<ContainerRowViewModel, Task<CommandResult>> op, bool showConfirm = true)
    {
        IsBusy = true;
        StatusMessage = message;
        var failures = new List<string>();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var r = items[index];
                try
                {
                    var result = await op(r);
                    if (!result.Success)
                    {
                        failures.Add($"{r.SessionName}: {r.Name}: {result.ErrorText}");
                    }
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Bulk container action was interrupted for {Container} in {Session}.", r.Name, r.SessionName);
                    failures.Add(UiText.Get("ResourceSession_BulkInterrupted",
                        "The current action's outcome may be unknown; {0} later item(s) were not attempted.",
                        items.Count - index - 1));
                    break;
                }
                catch (Exception ex) when (IsSessionActionFailure(ex))
                {
                    _logger.LogWarning(ex, "Bulk container action failed for {Container} in {Session}.", r.Name, r.SessionName);
                    failures.Add($"{r.SessionName}: {r.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            IsSelectionMode = false;
            _monitor.RequestRefresh();
        }

        StatusMessage = failures.Count == 0 ? "Done" : $"{failures.Count} of {items.Count} failed";
        if (failures.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Resource_Text_c0f95512437f", "Some containers failed"),
                UiText.Get("Resource_Text_3a167b296e0d", "{0} of {1} operations failed:\n\n{2}", failures.Count, items.Count, BulkNames(failures)));
        }
    }

    /// <summary>
    /// Drops any cached AI capability proof and re-observes. Removing a container can destroy the
    /// runtime a provider points at, and a stale positive observation would keep offering the
    /// assistant for a backend that no longer exists.
    /// </summary>
    private async Task InvalidateAiCapabilitiesAsync()
    {
        try
        {
            _aiCapabilities.Invalidate();
            await _aiAvailability.RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advisory only: a failed re-observation must not surface as a removal failure.
            _logger.LogDebug(ex, "AI capability refresh after container removal was unavailable.");
        }
    }

    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? UiText.Get("Resource_Text_5b44479e9557", "{0}\n… and {1} more", shown, list.Count - max) : shown;
    }

    /// <summary>Shows a one-time log snapshot for the selected container.</summary>
    [RelayCommand]
    private async Task LogsAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        IsBusy = true;
        StatusMessage = $"Fetching logs for {row.Name}…";
        try
        {
            var client = await ClientForRowAsync(row);
            var result = await client.GetLogsAsync(row.Id);
            var text = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? (string.IsNullOrWhiteSpace(result.StandardError) ? UiText.Get("Resource_Text_800c9b197e97", "(no output)") : result.StandardError)
                : result.StandardOutput;
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_LogsTitle",
                "Logs · {0} · {1}", row.Name, row.SessionName), text);
            StatusMessage = "Ready";
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens an interactive shell in the selected running container.</summary>
    [RelayCommand]
    private async Task TerminalAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        try
        {
            var client = await ClientForRowAsync(row);
            await client.OpenTerminal(row.Id);
            StatusMessage = $"Opened terminal for {row.Name}";
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
    }

    /// <summary>Attaches to the selected container's console through <c>wslc</c>.</summary>
    [RelayCommand]
    private async Task AttachAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_e2dfda5ec309", "Attach to container"),
            UiText.Get("Resource_Text_770d22e09b5d", "Attach opens an interactive terminal connected to the container's main process. ") +
            UiText.Get("Resource_Text_666f53f2d084", "The wslc help does not document a detach key sequence; pressing Ctrl+C may stop the container's main process."),
            UiText.Get("Resource_Text_1afff0157ce3", "Attach"));
        if (!ok)
        {
            return;
        }

        try
        {
            var client = await ClientForRowAsync(row);
            await client.AttachContainer(row.Id);
            StatusMessage = $"Attached to {row.Name}";
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
    }

    /// <summary>Runs or displays the selected container's health check result.</summary>
    [RelayCommand]
    private async Task HealthCheckAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        AutomationSessionOwner owner;
        try { owner = OwnerForRow(row); }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
            return;
        }
        var target = new WslcSessionTarget(owner.SessionName);
        var existing = _settings.HealthChecks
            .FirstOrDefault(h => string.Equals(h.ContainerName, row.Name, StringComparison.Ordinal) &&
                _legacyOwners.OwnerFor(h)?.Matches(owner.ExecutablePath, target) == true);
        var hostPorts = row.Model.Ports
            .Where(p => p.HostPort > 0)
            .Select(p => p.HostPort)
            .Distinct()
            .ToList();

        var dialog = new HealthCheckDialog(row.Name, hostPorts, existing)
        {
            Title = UiText.Get("Containers_HealthInSession", "Health check in {0}", row.SessionName),
        };
        var result = await _dialogs.ShowDialogAsync(dialog);
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
        {
            return;
        }
        if (!await EnsureCurrentRowsAsync([row]))
            return;
        try { await ClientForRowAsync(row); }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
            return;
        }

        // Replace any existing policy for this container. Build a new list and swap the reference
        // atomically so the watchdog's background loop never observes a mid-mutation list.
        var updated = _settings.HealthChecks
            .Where(h => !string.Equals(h.ContainerName, row.Name, StringComparison.Ordinal) ||
                _legacyOwners.OwnerFor(h)?.Matches(owner.ExecutablePath, target) != true)
            .ToList();

        if (dialog.Result is { IsValid: true } config)
        {
            config.SessionOwner = owner;
            updated.Add(config);
            StatusMessage = config.Enabled
                ? $"Health check configured for {row.Name}"
                : $"Health check disabled for {row.Name}";
        }
        else
        {
            StatusMessage = $"Health check removed for {row.Name}";
        }

        _settings.HealthChecks = updated;
        _settings.Save();
        if (row.Model.NativeHealth.OwnsCommandProbe)
            StatusMessage += ". Existing engine health is unchanged; native health changes require explicit container recreation.";
        RefreshHealth();
    }

    /// <summary>Starts streaming logs for the selected container.</summary>
    [RelayCommand]
    private async Task FollowLogsAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        try
        {
            var client = await ClientForRowAsync(row);
            await client.FollowLogs(row.Id, timestamps: LogTimestamps);
            StatusMessage = $"Streaming logs for {row.Name}";
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
    }

    /// <summary>Shows raw inspect output for the selected container.</summary>
    [RelayCommand]
    private async Task InspectAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        IsBusy = true;
        try
        {
            var client = await ClientForRowAsync(row);
            var result = await client.InspectContainerAsync(row.Id);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_InspectTitle", "Inspect {0} · {1}", row.Name, row.SessionName),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Captures a running container's settings (via <c>wslc inspect</c>, diffed against its image) as
    /// a reusable run profile. Recoverable mounts are included; storage limitations and hostname
    /// are reported before saving.
    /// </summary>
    /// <summary>Saves the selected container's run settings as a reusable run profile.</summary>
    [RelayCommand]
    private async Task SaveAsProfileAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;

        IsBusy = true;
        try
        {
            var client = await ClientForRowAsync(row);
            var containerResult = await client.InspectContainerAsync(row.Id);
            if (!containerResult.Success || string.IsNullOrWhiteSpace(containerResult.StandardOutput))
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_d2078b7339a5", "Save as run profile"),
                    UiText.Get("Resource_Text_93c23d7b9d3d", "Couldn't inspect {0}: {1}", row.Name, containerResult.ErrorText));
                return;
            }

            string? imageJson = null;
            await ClientForRowAsync(row);
            try
            {
                var imageRef = string.IsNullOrWhiteSpace(row.Model.Image) ? null : row.Model.Image;
                if (imageRef is not null)
                {
                    var imageResult = await client.InspectImageAsync(imageRef);
                    if (imageResult.Success)
                    {
                        imageJson = imageResult.StandardOutput;
                    }
                }
            }
            catch (Exception ex) when (IsSessionActionFailure(ex))
            {
                _logger.LogDebug(ex, "Could not inspect image settings for {Container} in {Session}.", row.Name, row.SessionName);
            }

            var options = ContainerConfigImporter.FromInspect(containerResult.StandardOutput, out var warnings, imageJson);
            if (options is null)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_d2078b7339a5", "Save as run profile"),
                    UiText.Get("Resource_Text_dfb7ec51e410", "Couldn't read a usable configuration from {0}.", row.Name));
                return;
            }

            var notCaptured = new[] { "hostname" };
            var suggested = string.IsNullOrWhiteSpace(options.Name) ? row.Name : options.Name!;

            var dialog = new SaveRunProfileDialog(suggested, options, notCaptured, warnings);
            var result = await _dialogs.ShowDialogAsync(dialog);
            if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary ||
                string.IsNullOrWhiteSpace(dialog.ProfileName))
            {
                return;
            }

            await ClientForRowAsync(row);
            _profiles.Save(new RunProfile { Name = dialog.ProfileName, Options = options });
            StatusMessage = $"Saved run profile \"{dialog.ProfileName}\" from {row.Name}";
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the selected container's primary published port in the browser.</summary>
    [RelayCommand]
    private async Task OpenPortAsync(ContainerRowViewModel? row)
    {
        using var selection = _sessionContext.LockSelection();
        if (row is null || !await EnsureCurrentRowsAsync([row]))
            return;
        var port = row.PrimaryHttpPort;
        if (port is null)
            return;

        try
        {
            if (row.SessionSource is not { } source)
                throw new InvalidOperationException(UiText.Get("CombinedSession_NotContainer", "The selected row is not a container."));
            var endpoint = CombinedSessionRow.FromEndpoint(new WslcCombinedInventory.Endpoint(source.Session, row.Model, port));
            var uri = endpoint.GetEndpointUri() ?? throw new InvalidOperationException(UiText.Get("CombinedSession_InvalidEndpoint",
                "The endpoint does not have a usable TCP address."));
            await _sessionActions.VerifyEndpointAsync(WslcContainerActionTarget.Capture(source.Session, row.Model), port);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Could not open the container endpoint.");
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"), ex.Message);
        }
    }

    /// <summary>Expands or collapses the AI diagnosis panel.</summary>
    [RelayCommand]
    private void ToggleAiPanelCollapsed() => AiPanelCollapsed = !AiPanelCollapsed;

    /// <summary>Clears the current AI diagnosis preview and result.</summary>
    [RelayCommand]
    private void DismissAiDiagnosis() => ResetAiState();

    /// <summary>Clears the AI diagnosis feedback banner.</summary>
    [RelayCommand]
    private void DismissDiagnosisFeedback() => DiagnosisFeedback = AiFeedback.None;

    /// <summary>Copies AI diagnosis technical details to the clipboard.</summary>
    [RelayCommand]
    private void CopyDiagnosisFeedbackDetails()
    {
        if (!DiagnosisFeedback.HasTechnicalDetails)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText($"{DiagnosisFeedback.Title}\n{DiagnosisFeedback.Message}\n\n{DiagnosisFeedback.TechnicalDetails}");
        Clipboard.SetContent(package);
    }

    /// <summary>Collects and redacts evidence for the selected container before sending it to AI.</summary>
    [RelayCommand]
    private async Task PrepareAiDiagnosisAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var selected = Selected;
        if (selected is null || !await EnsureCurrentRowsAsync([selected]))
            return;

        IsAiBusy = true;
        DiagnosisFeedback = AiFeedback.Informational("Preparing diagnosis", "Collecting and redacting evidence…");
        AiHasDiagnosis = false;
        try
        {
            var client = await ClientForRowAsync(selected);
            var preview = await _aiDiagnostics.BuildPreviewAsync(selected.Model, sessionClient: client,
                includeActivity: IsManagedRow(selected), sessionName: selected.SessionName);
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected))
                return;
            await ClientForRowAsync(selected);
            _pendingAiRequest = preview.Request;
            AiPreviewPayload = preview.Payload;
            AiHasPreview = true;
            DiagnosisFeedback = AiFeedback.Informational("Preview ready", "Review the redacted payload, then click Send diagnosis.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AI diagnosis preview failed: {Detail}", AiTextSanitizer.Sanitize(ex.Message));
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
                DiagnosisFeedback = AiErrorClassifier.Classify(ex, DiagnosisContext("Build diagnosis preview"));
        }
        finally
        {
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
                IsAiBusy = false;
        }
    }

    /// <summary>Sends the prepared redacted evidence to the configured AI diagnosis service.</summary>
    [RelayCommand]
    private async Task SendAiDiagnosisAsync()
    {
        if (_pendingAiRequest is null)
        {
            await PrepareAiDiagnosisAsync();
        }

        var selected = Selected;
        var request = _pendingAiRequest;
        if (selected is null || request is null || !await EnsureCurrentRowsAsync([selected]))
            return;

        IsAiBusy = true;
        DiagnosisFeedback = AiFeedback.Informational("Sending diagnosis", "Sending redacted evidence to the selected provider…");
        try
        {
            await ClientForRowAsync(selected);
            var diagnosis = await _aiDiagnostics.DiagnoseAsync(request);
            if (!ReferenceEquals(Selected, selected) || !IsCurrentRow(selected))
                return;
            AiSummary = diagnosis.Summary;
            AiLikelyCause = diagnosis.LikelyCause;
            AiSuggestedFixDescription = diagnosis.SuggestedFix.Description;
            AiConfidence = $"Confidence: {diagnosis.Confidence:P0}";

            AiEvidenceCitations.Clear();
            foreach (var item in diagnosis.EvidenceCited)
            {
                AiEvidenceCitations.Add(item);
            }

            AiSuggestedCommands.Clear();
            foreach (var item in diagnosis.SuggestedFix.Commands)
            {
                AiSuggestedCommands.Add(item);
            }

            AiSuggestedFileEdits.Clear();
            foreach (var item in diagnosis.SuggestedFix.FileEdits)
            {
                AiSuggestedFileEdits.Add(item);
            }

            AiHasDiagnosis = true;
            DiagnosisFeedback = AiFeedback.Success("Diagnosis complete", "Suggested fixes are review-only.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AI diagnosis failed: {Detail}", AiTextSanitizer.Sanitize(ex.Message));
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
                DiagnosisFeedback = AiErrorClassifier.Classify(ex, DiagnosisContext("Send diagnosis"));
        }
        finally
        {
            if (ReferenceEquals(Selected, selected) && IsCurrentRow(selected))
                IsAiBusy = false;
        }
    }

    /// <summary>Copies the AI suggested fix, commands, and edits to the clipboard.</summary>
    [RelayCommand]
    private void CopyAiSuggestedFix()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(AiSuggestedFixDescription))
        {
            parts.Add(AiSuggestedFixDescription);
        }

        if (AiSuggestedCommands.Count > 0)
        {
            parts.Add(UiText.Get("Resource_Text_d2865cc8490e", "Commands:\n") + string.Join('\n', AiSuggestedCommands));
        }

        if (AiSuggestedFileEdits.Count > 0)
        {
            parts.Add(UiText.Get("Resource_Text_c97652eb6016", "File edits:\n") + string.Join('\n', AiSuggestedFileEdits));
        }

        var package = new DataPackage();
        package.SetText(string.Join("\n\n", parts));
        Clipboard.SetContent(package);
        DiagnosisFeedback = AiFeedback.Success("Copied", "Copied suggested fix to the clipboard.");
    }

    /// <summary>Confirms and removes unused containers/images through the engine prune command.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var target = await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger, startDefault: false);
        if (target is null) return;
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_658d1f8b6e57", "Prune containers"),
            UiText.Get("Containers_PruneInSession", "Remove all stopped containers in {0}?", target.DisplayName),
            UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        if (await ExecuteAsync("Pruning stopped containers…", async () =>
            {
                await target.VerifyAsync();
                return await target.Client.PruneContainersAsync();
            }))
            await _combined.RefreshAllAsync();
    }

    private async Task<bool> ExecuteAsync(string message, Func<Task<CommandResult>> action)
    {
        IsBusy = true;
        StatusMessage = message;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"), result.ErrorText);
                StatusMessage = "Operation failed";
                return false;
            }
            else
            {
                StatusMessage = "Done";
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Container operation failed.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"),
                ex is OperationCanceledException
                    ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                        "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                    : ex.Message);
            StatusMessage = "Operation failed";
            return false;
        }
        finally
        {
            IsBusy = false;
            _monitor.RequestRefresh();
        }
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        using (Grid.DeferRefresh())
            foreach (var item in Containers) item.RefreshLocalizedText();
        foreach (var item in Groups) item.RefreshLocalizedText();
        foreach (var item in DetailFiles) item.RefreshLocalizedText();
    }
}
