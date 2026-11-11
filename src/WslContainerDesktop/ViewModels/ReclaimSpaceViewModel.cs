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
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>
/// Backs the "Reclaim space" page: a holistic disk-usage &amp; cleanup center that
/// summarizes how much space images, containers, and volumes consume and offers
/// one-click, confirmed pruning that reuses the existing <c>Prune*</c> service methods.
/// </summary>
/// <summary>View model for the Reclaim Space page, summarizing images, stopped containers, and volumes that may free disk space.</summary>
public partial class ReclaimSpaceViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay
    {
        get
        {
            var message = _status switch
            {
                ReclaimStatus.Calculating => UiText.Get("Reclaim_StatusCalculating", "Calculating disk usage…"),
                ReclaimStatus.Reclaimable or ReclaimStatus.Nothing when _imageUsageIncomplete || _containerStateIncomplete =>
                    UiText.Get("Reclaim_StatusUsageIncomplete",
                        "Some image usage or container cleanup eligibility is unknown; reclaimable totals are unavailable."),
                ReclaimStatus.Reclaimable or ReclaimStatus.Nothing when _containerSizeIncomplete =>
                    UiText.Get("Reclaim_StatusSizeIncomplete",
                        "Some stopped containers have no writable-layer size; reclaimable totals are unavailable."),
                ReclaimStatus.Reclaimable => UiText.Get("Reclaim_StatusReclaimable",
                    "Up to {0} reclaimable from dangling images and stopped containers", TotalReclaimableSize),
                ReclaimStatus.Nothing when _sessionFailures.Count > 0 =>
                    UiText.Get("Reclaim_StatusNoKnown", "No reclaimable space found in the sessions scanned."),
                ReclaimStatus.Nothing => UiText.Get("Reclaim_StatusNothing", "Nothing obvious to reclaim"),
                ReclaimStatus.Cancelled => UiText.Get("Reclaim_StatusCancelled",
                    "Disk usage refresh cancelled or timed out; displayed data may be stale"),
                ReclaimStatus.Error => UiText.Get("Reclaim_StatusError", "Error"),
                ReclaimStatus.PruneFailed => UiText.Get("Reclaim_StatusPruneFailed", "Prune failed"),
                _ => UiText.Get("Workload_Text_Ready_5fa7aa", "Ready"),
            };
            return _volumeUsageIncomplete && (_status is ReclaimStatus.Reclaimable or ReclaimStatus.Nothing)
                ? UiText.Get("Reclaim_StatusIncomplete",
                    "{0} · volume usage is incomplete; unknown volumes are not counted as unused", message)
                : message;
        }
    }

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StorageLocationDisplay => _storagePath is null
        ? UiText.Get("Common_Text0003", "Unknown")
        : _usesDefaultStorage ? UiText.Get("Reclaim_DefaultStorage", "Default ({0})", _storagePath) : _storagePath;

    /// <summary>How many of the largest images to surface in the "largest images" list.</summary>
    private const int TopImageCount = 5;

    private readonly IWslcService _wslc;
    private readonly IWslcSettingsFileService _wslcSettings;
    private readonly DialogService _dialogs;
    private readonly ILogger<ReclaimSpaceViewModel> _logger;
    private readonly WslcSessionContext _sessionContext;
    private readonly IWslcSessionSelectionService? _sessionSelection;
    private readonly WslcSessionInventoryReader? _sessionReader;
    private WslcSessionContext.SelectionSnapshot? _inventorySelection;
    private SessionOperationTarget? _inventoryTarget;
    private bool _showingAllSessions;
    private bool _discoveryComplete;
    private int _observedSessionCount;
    private int _totalSessionCount;
    private sealed record ScanFailure(string? Name, Exception Error, bool Discovery = false);
    private sealed class ScanTimeoutException : TimeoutException { }
    private IReadOnlyList<ScanFailure> _sessionFailures = [];

    private long _imagesTotalBytes;
    private long _imagesReclaimableBytes;

    /// <summary>Generated busy flag used while scanning or pruning disk usage.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshChosenCommand), nameof(PruneImagesCommand), nameof(PruneContainersCommand),
        nameof(PruneVolumesCommand), nameof(PruneAllCommand))]
    private bool _isBusy;

    /// <summary>Whether the page has a valid inventory to display.</summary>
    [ObservableProperty]
    private bool _hasInventory;

    private bool CanReclaim() => !IsBusy;
    private bool CanCleanup() => !IsBusy && (!_showingAllSessions || !_discoveryComplete || _totalSessionCount > 0);

    private void RefreshCleanupAvailability()
    {
        PruneImagesCommand.NotifyCanExecuteChanged();
        PruneContainersCommand.NotifyCanExecuteChanged();
        PruneVolumesCommand.NotifyCanExecuteChanged();
        PruneAllCommand.NotifyCanExecuteChanged();
    }

    private enum ReclaimStatus { Ready, Calculating, Reclaimable, Nothing, Cancelled, Error, PruneFailed }
    private ReclaimStatus _status;
    private bool _volumeUsageIncomplete;
    private bool _containerSizeIncomplete;
    private bool _containerStateIncomplete;
    private bool _imageUsageIncomplete;
    private readonly SemaphoreSlim _pageScanGate = new(1, 1);
    private CancellationToken _pageLifetime;

    private void SetStatus(ReclaimStatus status, bool volumeUsageIncomplete = false)
    {
        _status = status;
        _volumeUsageIncomplete = volumeUsageIncomplete;
        OnPropertyChanged(nameof(StatusMessageDisplay));
    }

    /// <summary>The session whose disk usage is displayed.</summary>
    [ObservableProperty]
    private string _inventorySession = string.Empty;

    /// <summary>Localized label for the displayed inventory.</summary>
    public string InventorySessionDisplay => _showingAllSessions
        ? !_discoveryComplete ? string.Empty : _totalSessionCount == 0
            ? UiText.Get("Reclaim_NoActiveSessions", "No active sessions. Start a session to see its disk usage.")
            : UiText.Get("Reclaim_AllSessions", "Active sessions scanned: {0} of {1}",
                _observedSessionCount, _totalSessionCount)
        : _inventorySelection is null ? string.Empty :
        UiText.Get("Reclaim_InventorySession", "Disk usage for: {0}", string.IsNullOrEmpty(InventorySession)
            ? UiText.Get("WslEngine_Session_Default", "Default CLI session") : InventorySession);

    /// <summary>Failures excluded from the displayed totals.</summary>
    public string InventoryWarning => _sessionFailures.Count == 0 ? string.Empty :
        UiText.Get("Reclaim_PartialSessions",
            "Disk usage is incomplete. These sessions could not be scanned: {0}",
            string.Join("; ", _sessionFailures.Select(failure =>
                $"{(failure.Discovery ? UiText.Get("Reclaim_ScanUnavailable", "Disk usage scan") :
                    failure.Name ?? UiText.Get("WslEngine_Session_Default", "Default CLI session"))}: " +
                (failure.Error is ScanTimeoutException
                    ? UiText.Get("Reclaim_ScanTimedOut", "Disk usage scan timed out.")
                    : WslcSessionInventoryErrors.Describe(failure.Error)))));

    partial void OnInventorySessionChanged(string value) => OnPropertyChanged(nameof(InventorySessionDisplay));

    // ---- Images ----
    /// <summary>Total images reported by <c>wslc</c>.</summary>
    [ObservableProperty]
    private int _imageCount;

    /// <summary>Dangling images that are not currently held by a container and may be pruned.</summary>
    [ObservableProperty]
    private int _danglingImageCount;

    /// <summary>
    /// Dangling images a container still holds. They are listed, because they are real untagged
    /// images taking real space, but they are not counted as reclaimable: nothing can remove them
    /// until whatever is using them goes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRetainedDanglingImages))]
    [NotifyPropertyChangedFor(nameof(RetainedDanglingNote))]
    private int _retainedDanglingImageCount;

    /// <summary>True when some dangling images are still referenced by containers.</summary>
    public bool HasRetainedDanglingImages => RetainedDanglingImageCount > 0;

    /// <summary>Explanation shown when dangling images cannot yet be reclaimed.</summary>
    public string RetainedDanglingNote => RetainedDanglingImageCount == 1
        ? UiText.Get("Resource_Text_314c9caa1b7f", "1 dangling image is still used by a container and cannot be removed yet.")
        : UiText.Get("Resource_Text_0ce59716077c", "{0} dangling images are still used by containers and cannot be removed yet.", RetainedDanglingImageCount);

    /// <summary>Human-readable total image size.</summary>
    [ObservableProperty]
    private string _imagesTotalSize = "0 B";

    /// <summary>Human-readable upper bound for reclaimable dangling image space.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImagesReclaimableSizeDisplay))]
    private string _imagesReclaimableSize = "0 B";
    public string ImagesReclaimableSizeDisplay => _imageUsageIncomplete
        ? UiText.Get("Common_Text0003", "Unknown") : ImagesReclaimableSize;

    // ---- Containers ----
    /// <summary>Total containers counted during the scan.</summary>
    [ObservableProperty]
    private int _containerCount;

    /// <summary>Stopped containers whose writable layers may be removed.</summary>
    [ObservableProperty]
    private int _stoppedContainerCount;

    /// <summary>Human-readable writable-layer space held by stopped containers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoppedContainersReclaimableSizeDisplay))]
    private string _stoppedContainersReclaimableSize = "0 B";
    public string StoppedContainersReclaimableSizeDisplay => _containerSizeIncomplete || _containerStateIncomplete
        ? UiText.Get("Common_Text0003", "Unknown") : StoppedContainersReclaimableSize;

    // ---- Volumes ----
    /// <summary>Total volumes counted during the scan.</summary>
    [ObservableProperty]
    private int _volumeCount;

    /// <summary>Volumes resolved as unused by the volume usage resolver.</summary>
    [ObservableProperty]
    private int _unusedVolumeCount;

    // ---- Totals ----
    /// <summary>Human-readable total of reclaimable image and stopped-container space.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalReclaimableSizeDisplay))]
    private string _totalReclaimableSize = "0 B";
    public string TotalReclaimableSizeDisplay => _containerSizeIncomplete || _containerStateIncomplete || _imageUsageIncomplete
        ? UiText.Get("Common_Text0003", "Unknown") : TotalReclaimableSize;

    private bool _usesDefaultStorage;
    private string? _storagePath;

    /// <summary>The largest images by on-disk size (top <see cref="TopImageCount"/>).</summary>
    public ObservableCollection<ImageInfo> LargestImages { get; } = new();

    /// <summary>Dangling (untagged) images, which a plain image prune removes.</summary>
    public ObservableCollection<ImageInfo> DanglingImages { get; } = new();

    /// <summary>Volumes unused in a complete inspect snapshot; the engine decides prune eligibility.</summary>
    public ObservableCollection<VolumeInfo> UnusedVolumes { get; } = new();

    /// <summary>Creates the reclaim-space model with <c>wslc</c>, settings, dialog, and logging services.</summary>
    public ReclaimSpaceViewModel(IWslcService wslc, IWslcSettingsFileService wslcSettings, DialogService dialogs,
        ILogger<ReclaimSpaceViewModel> logger, WslcSessionContext sessionContext,
        IWslcSessionSelectionService? sessionSelection = null, WslcSessionInventoryReader? sessionReader = null)
    {
        _wslc = wslc;
        _wslcSettings = wslcSettings;
        _dialogs = dialogs;
        _logger = logger;
        _sessionContext = sessionContext;
        _sessionSelection = sessionSelection;
        _sessionReader = sessionReader;
    }

    /// <summary>An image is "dangling" when it has no repository or tag (i.e. <c>&lt;none&gt;</c>).</summary>
    internal static bool IsDangling(ImageInfo image) =>
        IsNone(image.Repository) || IsNone(image.Tag);

    private static bool IsNone(string value) =>
        string.IsNullOrEmpty(value) || value.Equals("<none>", StringComparison.OrdinalIgnoreCase);

    private sealed record UsageSnapshot(
        IReadOnlyList<ImageInfo> Images, IReadOnlyList<ContainerInfo> Containers,
        IReadOnlyList<VolumeInfo> Volumes, bool VolumeUsageIncomplete);

    private async Task<UsageSnapshot> ReadSnapshotAsync(
        IWslcService client, string sessionName, CancellationToken ct)
    {
        var images = await client.ListImagesAsync(ct);
        var containers = await client.ListContainersAsync(all: true, ct: ct, includeSize: true);
        var volumes = (await client.ListVolumesAsync(ct)).ToList();
        foreach (var volume in volumes)
        {
            volume.SessionName = sessionName;
            var inspect = await client.InspectVolumeAsync(volume.Name, ct);
            if (inspect.Success)
                volume.EnrichFromInspect(inspect.StandardOutput);
            else
                _logger.LogWarning("Could not inspect volume {Volume} in {Session}: {Error}",
                    volume.Name, sessionName, inspect.ErrorText);
        }
        var warnings = await VolumeUsageResolver.ResolveAsync(volumes, containers,
            (id, token) => client.InspectContainerAsync(id, token), ct);
        foreach (var warning in warnings)
            _logger.LogWarning("Volume usage in {Session}: {Warning}", sessionName, warning);
        ImageUsageResolver.Apply(images, containers);
        foreach (var image in images)
            image.SessionName = sessionName;
        return new(images, containers, volumes, warnings.Count > 0);
    }

    private void ApplySnapshots(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var images = snapshots.SelectMany(snapshot => snapshot.Images).ToList();
        var containers = snapshots.SelectMany(snapshot => snapshot.Containers).ToList();
        var volumes = snapshots.SelectMany(snapshot => snapshot.Volumes).ToList();
        var dangling = images.Where(IsDangling).ToList();
        var reclaimable = dangling.Where(image => !image.IsInUse && !image.UsageUnknown).ToList();
        _imageUsageIncomplete = dangling.Any(image => image.UsageUnknown);
        _imagesTotalBytes = images.Sum(image => image.Size);
        _imagesReclaimableBytes = reclaimable.Sum(image => image.Size);
        ImageCount = images.Count;
        DanglingImageCount = reclaimable.Count;
        RetainedDanglingImageCount = dangling.Count(image => image.IsInUse);
        ImagesTotalSize = FormatHelpers.HumanSize(_imagesTotalBytes);
        ImagesReclaimableSize = FormatHelpers.HumanSize(_imagesReclaimableBytes);
        OnPropertyChanged(nameof(ImagesReclaimableSizeDisplay));
        LargestImages.Clear();
        foreach (var image in images.OrderByDescending(image => image.Size).Take(TopImageCount))
            LargestImages.Add(image);
        DanglingImages.Clear();
        foreach (var image in dangling.OrderByDescending(image => image.Size))
            DanglingImages.Add(image);
        ContainerCount = containers.Count;
        var stopped = containers.Where(container => container.State == ContainerState.Stopped).ToList();
        _containerStateIncomplete = containers.Any(container => container.State is ContainerState.Unknown or ContainerState.Created);
        StoppedContainerCount = stopped.Count;
        _containerSizeIncomplete = stopped.Any(container => container.SizeRwBytes is null);
        var stoppedBytes = stopped.Sum(container => container.SizeRwBytes ?? 0);
        StoppedContainersReclaimableSize = FormatHelpers.HumanSize(stoppedBytes);
        OnPropertyChanged(nameof(StoppedContainersReclaimableSizeDisplay));
        VolumeCount = volumes.Count;
        var unused = volumes.Where(volume => volume.UsageState == VolumeUsageState.Unused).ToList();
        UnusedVolumeCount = unused.Count;
        UnusedVolumes.Clear();
        foreach (var volume in unused)
            UnusedVolumes.Add(volume);
        TotalReclaimableSize = FormatHelpers.HumanSize(_imagesReclaimableBytes + stoppedBytes);
        OnPropertyChanged(nameof(TotalReclaimableSizeDisplay));
        SetStatus(_imagesReclaimableBytes + stoppedBytes > 0 ? ReclaimStatus.Reclaimable : ReclaimStatus.Nothing,
            snapshots.Any(snapshot => snapshot.VolumeUsageIncomplete));
        HasInventory = true;
    }

    private async Task ScanAllAsync(CancellationToken ct = default)
    {
        if (_sessionReader is null || _sessionSelection is null)
            throw new InvalidOperationException("All-session disk usage is unavailable.");
        using var selection = _sessionContext.LockSelection();
        _inventorySelection = null;
        _inventoryTarget = null;
        _showingAllSessions = true;
        _discoveryComplete = false;
        _sessionFailures = [];
        _observedSessionCount = _totalSessionCount = 0;
        HasInventory = false;
        SetStatus(ReclaimStatus.Calculating);
        OnPropertyChanged(nameof(InventorySessionDisplay));
        OnPropertyChanged(nameof(InventoryWarning));
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batch.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var executable = _sessionReader.CurrentExecutablePath;
            var settingsFile = await _wslcSettings.ReadAsync(batch.Token).WaitAsync(batch.Token);
            _usesDefaultStorage = settingsFile.UsesDefaultStorage;
            _storagePath = _usesDefaultStorage ? settingsFile.DefaultStoragePath : settingsFile.EffectiveStoragePath;
            OnPropertyChanged(nameof(StorageLocationDisplay));
            var sessions = await _sessionReader.DiscoverAsync(batch.Token);
            var choices = WslcSessionChoices.Create(sessions, executable,
                    WslcSessionActionGuard.ResolveDefaultSessionName(),
                    UiText.Get("WslEngine_Session_Default", "Default CLI session"))
                .Where(choice => choice.Generation is not null).ToArray();
            _totalSessionCount = choices.Length;
            _discoveryComplete = true;
            using var gate = new SemaphoreSlim(2);
            var results = await Task.WhenAll(choices.Select(async choice =>
            {
                try
                {
                    await gate.WaitAsync(batch.Token);
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested)
                {
                    return (Choice: choice, Snapshot: (UsageSnapshot?)null,
                        Error: (Exception?)new ScanTimeoutException());
                }
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(batch.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    try
                    {
                        await _sessionSelection.VerifyAsync(choice, timeout.Token).WaitAsync(timeout.Token);
                        var data = await ReadSnapshotAsync(_sessionSelection.CreateClient(choice),
                            choice.Generation!.Name, timeout.Token).WaitAsync(timeout.Token);
                        await _sessionSelection.VerifyAsync(choice, timeout.Token).WaitAsync(timeout.Token);
                        return (Choice: choice, Snapshot: (UsageSnapshot?)data, Error: (Exception?)null);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        return (Choice: choice, Snapshot: (UsageSnapshot?)null,
                            Error: (Exception?)new ScanTimeoutException());
                    }
                    catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
                    {
                        _logger.LogWarning(ex, "Could not read disk usage in session {Session}.", choice.DisplayName);
                        return (Choice: choice, Snapshot: (UsageSnapshot?)null, Error: (Exception?)ex);
                    }
                }
                finally { gate.Release(); }
            }));
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(executable, _sessionReader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
            for (var index = 0; index < results.Length; index++)
            {
                var result = results[index];
                if (result.Snapshot is null)
                    continue;
                try
                {
                    await _sessionSelection.VerifyAsync(result.Choice, batch.Token).WaitAsync(batch.Token);
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested)
                {
                    results[index] = (result.Choice, null, new ScanTimeoutException());
                }
                catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
                {
                    _logger.LogWarning(ex, "Disk usage session {Session} changed before display.", result.Choice.DisplayName);
                    results[index] = (result.Choice, null, ex);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(executable, _sessionReader.CurrentExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new WslcSessionInventoryErrors.InventoryException(WslcSessionInventoryErrors.Kind.ExecutableChanged);
            _sessionFailures = results.Where(result => result.Error is not null)
                .Select(result => new ScanFailure(result.Choice.Target.Name, result.Error!)).ToArray();
            var snapshots = results.Where(result => result.Snapshot is not null)
                .Select(result => result.Snapshot!).ToArray();
            _observedSessionCount = snapshots.Length;
            if (snapshots.Length > 0)
                ApplySnapshots(snapshots);
            else if (choices.Length == 0)
                SetStatus(ReclaimStatus.Ready);
            else
                SetStatus(ReclaimStatus.Error);
        }
        catch (OperationCanceledException) when (batch.IsCancellationRequested)
        {
            SetStatus(ReclaimStatus.Cancelled);
        }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Could not discover or scan active sessions for disk usage.");
            _sessionFailures = [new ScanFailure(null, ex, Discovery: true)];
            SetStatus(ReclaimStatus.Error);
        }
        finally
        {
            OnPropertyChanged(nameof(InventorySessionDisplay));
            OnPropertyChanged(nameof(InventoryWarning));
            RefreshCleanupAvailability();
        }
    }

    /// <summary>Scans images, containers, and volumes to estimate safely reclaimable space.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => ScanAsync(_wslc, null, ct);

    [RelayCommand(CanExecute = nameof(CanReclaim))]
    private Task RefreshChosenAsync() => RefreshForPageAsync(_pageLifetime);

    /// <summary>Sets the lifetime used by page-initiated refreshes.</summary>
    public void SetPageLifetime(CancellationToken lifetime) => _pageLifetime = lifetime;

    /// <summary>Refreshes on page entry, waiting for a cancelled prior scan to finish.</summary>
    public async Task RefreshForPageAsync(CancellationToken ct)
    {
        try
        {
            await _pageScanGate.WaitAsync(ct);
            try
            {
                if (IsBusy) return;
                IsBusy = true;
                try
                {
                    if (_sessionReader is not null && _sessionSelection is not null)
                    {
                        await ScanAllAsync(ct);
                        return;
                    }
                    if (_sessionSelection is null)
                    {
                        await ScanAsync(_wslc, null, ct, ownsBusy: true);
                        return;
                    }
                    var target = await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger, ct, startDefault: false);
                    if (target is not null) await ScanAsync(target.Client, target, ct, ownsBusy: true);
                }
                finally { IsBusy = false; }
            }
            finally { _pageScanGate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ScanAsync(IWslcService client, SessionOperationTarget? target,
        CancellationToken ct = default, bool ownsBusy = false)
    {
        if (IsBusy && !ownsBusy)
            return;
        using var selection = _sessionContext.LockSelection();
        _inventorySelection = null;
        _inventoryTarget = null;
        _showingAllSessions = false;
        _sessionFailures = [];
        HasInventory = false;
        OnPropertyChanged(nameof(InventorySessionDisplay));
        OnPropertyChanged(nameof(InventoryWarning));
        var snapshot = _sessionContext.CaptureSnapshot();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        refresh.CancelAfter(TimeSpan.FromSeconds(45));
        ct = refresh.Token;
        if (!ownsBusy) IsBusy = true;
        SetStatus(ReclaimStatus.Calculating);
        try
        {
            if (target is not null) await target.VerifyAsync(ct);
            var settingsFile = await _wslcSettings.ReadAsync(ct);
            _usesDefaultStorage = settingsFile.UsesDefaultStorage;
            _storagePath = _usesDefaultStorage ? settingsFile.DefaultStoragePath : settingsFile.EffectiveStoragePath;
            OnPropertyChanged(nameof(StorageLocationDisplay));
            var data = await ReadSnapshotAsync(client, target?.Selection.Generation?.Name ?? snapshot.Target.Name ?? string.Empty, ct);
            ct.ThrowIfCancellationRequested();
            if (target is not null) await target.VerifyAsync(ct);
            ApplySnapshots([data]);
            _inventorySelection = snapshot;
            _inventoryTarget = target;
            InventorySession = (target is null ? snapshot.Target.Name : target.Selection.Target.Name) ?? string.Empty;
            OnPropertyChanged(nameof(InventorySessionDisplay));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            HasInventory = false;
            SetStatus(ReclaimStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not calculate disk usage for the chosen session.");
            HasInventory = false;
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_272b9b240c75", "Failed to calculate disk usage"), ex.Message);
            SetStatus(ReclaimStatus.Error);
        }
        finally
        {
            if (!ownsBusy) IsBusy = false;
        }
    }

    /// <summary>Removes dangling images that are not currently used by containers.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private Task PruneImagesAsync() => RunPruneAsync(async () =>
    {
        if (DanglingImageCount == 0 && !_imageUsageIncomplete)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_6257dda9c3b7", "Remove dangling images"), UiText.Get("Resource_Text_fb107b55608b", "There are no dangling images to remove."));
            return;
        }

        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_6257dda9c3b7", "Remove dangling images"),
            WithSession(_imageUsageIncomplete
                ? UiText.Get("Reclaim_ConfirmUnknownImageUsage",
                    "Some image usage is unknown. The engine will decide which dangling images can be removed.")
                : UiText.Get("Resource_Text_bb93366308e4", "Remove {0}, reclaiming up to {1}?", Count(DanglingImageCount, "dangling image"), ImagesReclaimableSize)),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = await SumImageBytesAsync(InventoryClient);
            var result = await InventoryClient.PruneImagesAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
                return;
            }

            var freed = Math.Max(0, before - await SumImageBytesAsync(InventoryClient));
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_e8b433d94d09", "Images pruned"), UiText.Get("Resource_Text_447d795e6433", "Reclaimed {0}.", FormatHelpers.HumanSize(freed)));
        });
    });

    /// <summary>Removes stopped containers after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private Task PruneContainersAsync() => RunPruneAsync(async () =>
    {
        if (StoppedContainerCount == 0 && !_containerStateIncomplete)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_5affb4df88db", "Remove stopped containers"), UiText.Get("Resource_Text_0cb1ba8ea3a2", "There are no stopped containers to remove."));
            return;
        }

        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_5affb4df88db", "Remove stopped containers"),
            WithSession(_containerStateIncomplete
                ? UiText.Get("Reclaim_ConfirmUnknownContainerState",
                    "Some container states or cleanup eligibility are unknown. The engine will decide which containers can be removed.")
                : _containerSizeIncomplete
                ? UiText.Get("Reclaim_ConfirmUnknownContainerSize",
                    "Remove all stopped containers ({0} candidate(s))? Some writable-layer sizes are unknown.", StoppedContainerCount)
                : UiText.Get("Resource_Text_df08e49b2d70", "Remove all stopped containers ({0} candidate(s)), reclaiming about {1} from writable layers?", StoppedContainerCount, StoppedContainersReclaimableSize)),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = (await InventoryClient.ListContainersAsync(all: true, includeSize: true)).ToList();
            var beforeBytes = before.Where(c => c.State == ContainerState.Stopped).Sum(c => c.SizeRwBytes ?? 0);
            var result = await InventoryClient.PruneContainersAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
                return;
            }

            var after = await InventoryClient.ListContainersAsync(all: true, includeSize: true);
            var removed = Math.Max(0, before.Count - after.Count);
            var afterBytes = after.Where(c => c.State == ContainerState.Stopped).Sum(c => c.SizeRwBytes ?? 0);
            var sizesKnown = before.Concat(after).All(c => c.State is not (ContainerState.Unknown or ContainerState.Created)) &&
                before.Where(c => c.State == ContainerState.Stopped).All(c => c.SizeRwBytes is not null) &&
                after.Where(c => c.State == ContainerState.Stopped).All(c => c.SizeRwBytes is not null);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_7b1133fe472d", "Containers pruned"),
                sizesKnown
                    ? UiText.Get("Resource_Text_c1b7111021d0", "Removed {0}, reclaiming about {1}.", Count(removed, "container"), FormatHelpers.HumanSize(Math.Max(0, beforeBytes - afterBytes)))
                    : UiText.Get("Reclaim_RemovedUnknownSize", "Removed {0}. Writable-layer space reclaimed is unknown.", Count(removed, "container")));
        });
    });

    /// <summary>Asks the engine to remove volumes it considers unused.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private Task PruneVolumesAsync() => RunPruneAsync(async () =>
    {
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_1b82a339cce8", "Remove unused volumes"),
            WithSession(UiText.Get("Resource_Text_7a4d99920018", "Remove all unused volumes? Any data they hold will be lost.\n\n") +
            UiText.Get("Resource_Text_fbee6d8e80d2", "Usage is a snapshot, including stopped containers when metadata is available. ") +
            UiText.Get("Resource_Text_2eccf3bfe77f", "Unknown usage is not proof of eligibility; the engine determines which volumes can be pruned.")),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var before = (await InventoryClient.ListVolumesAsync()).Count;
            var result = await InventoryClient.PruneVolumesAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
                return;
            }

            var removed = Math.Max(0, before - (await InventoryClient.ListVolumesAsync()).Count);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_1bdc17c5e2f9", "Volumes pruned"), UiText.Get("Resource_Text_5e34cda5a434", "Removed {0}.", Count(removed, "volume")));
        });
    });

    /// <summary>Runs the combined image, container, and volume cleanup flow.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private Task PruneAllAsync() => RunPruneAsync(async () =>
    {
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_4f75bf557ab2", "Reclaim space"),
            WithSession(UiText.Get("Resource_Text_51f0128d112d", "Remove all dangling images, stopped containers, and unused volumes?\n\n") +
            UiText.Get("Resource_Text_1a709936ef89", "Data in the affected volumes will be lost.")),
            UiText.Get("Resource_Text_6600d6e1e67c", "Reclaim"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        await ExecutePruneAsync(async () =>
        {
            var imageBytesBefore = await SumImageBytesAsync(InventoryClient);
            var containersBefore = (await InventoryClient.ListContainersAsync(all: true, includeSize: true)).ToList();
            var containerBytesBefore = containersBefore.Where(c => c.State == ContainerState.Stopped).Sum(c => c.SizeRwBytes ?? 0);
            var volumesBefore = (await InventoryClient.ListVolumesAsync()).Count;

            // Containers first: removing a container releases any anonymous volumes it held,
            // so the subsequent volume prune can reclaim them too.
            var containerResult = await InventoryClient.PruneContainersAsync();
            var volumeResult = await InventoryClient.PruneVolumesAsync();
            var imageResult = await InventoryClient.PruneImagesAsync();

            var errors = new List<string>();
            AppendError(errors, UiText.Get("Resource_Text_642b5cd982d4", "containers"), containerResult);
            AppendError(errors, UiText.Get("Resource_Text_e44dfa6f6650", "volumes"), volumeResult);
            AppendError(errors, UiText.Get("Resource_Text_19f49d852660", "images"), imageResult);

            var freedBytes = Math.Max(0, imageBytesBefore - await SumImageBytesAsync(InventoryClient));
            var containersAfter = await InventoryClient.ListContainersAsync(all: true, includeSize: true);
            var containersRemoved = Math.Max(0, containersBefore.Count - containersAfter.Count);
            var containerFreedBytes = Math.Max(0, containerBytesBefore - containersAfter.Where(c => c.State == ContainerState.Stopped).Sum(c => c.SizeRwBytes ?? 0));
            var volumesRemoved = Math.Max(0, volumesBefore - (await InventoryClient.ListVolumesAsync()).Count);

            var containerSizesKnown = containersBefore.Concat(containersAfter).All(c => c.State is not (ContainerState.Unknown or ContainerState.Created)) &&
                containersBefore.Where(c => c.State == ContainerState.Stopped).All(c => c.SizeRwBytes is not null) &&
                containersAfter.Where(c => c.State == ContainerState.Stopped).All(c => c.SizeRwBytes is not null);
            var summary =
                UiText.Get("Resource_Text_2e0629ec10b6", "Reclaimed {0} from images.\n", FormatHelpers.HumanSize(freedBytes)) +
                (containerSizesKnown
                    ? UiText.Get("Resource_Text_2789780f8eaa", "Removed {0} (about {1} writable layer data) and {2}.", Count(containersRemoved, "container"), FormatHelpers.HumanSize(containerFreedBytes), Count(volumesRemoved, "volume"))
                    : UiText.Get("Reclaim_AllUnknownContainerSize",
                        "Removed {0} and {1}. Writable-layer space reclaimed is unknown.",
                        Count(containersRemoved, "container"), Count(volumesRemoved, "volume")));
            if (errors.Count > 0)
            {
                summary += UiText.Get("Resource_Text_21208bca20bf", "\n\nSome steps reported errors:\n") + string.Join("\n", errors);
            }

            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4b16ccc50cc2", "Reclaim complete"), summary);
        });
    });

    private IWslcService InventoryClient => _inventoryTarget?.Client ?? _wslc;

    private string WithSession(string message) => _inventoryTarget is null ? message :
        UiText.Get("Reclaim_ConfirmSession", "Session: {0}\n\n{1}", _inventoryTarget.DisplayName, message);

    private static async Task<long> SumImageBytesAsync(IWslcService client) =>
        (await client.ListImagesAsync()).Sum(i => i.Size);

    private async Task<bool> PrepareInventoryAsync()
    {
        if (_sessionSelection is not null)
        {
            var choice = await SessionOperationTarget.ChooseActiveAsync(_sessionSelection, _dialogs, _logger);
            if (choice is null) return false;
            try
            {
                var target = new SessionOperationTarget(choice, _sessionSelection.CreateClient(choice), _sessionSelection);
                await ScanAsync(target.Client, target, ownsBusy: true);
            }
            catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
            {
                _logger.LogWarning(ex, "Could not prepare disk cleanup for the chosen session.");
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Reclaim_SnapshotStaleTitle", "Refresh disk usage"),
                    WslcSessionInventoryErrors.Describe(ex));
                return false;
            }
        }
        return await EnsureCurrentInventoryAsync();
    }

    private async Task<bool> EnsureCurrentInventoryAsync()
    {
        if (_inventorySelection is { } snapshot && snapshot == _sessionContext.CaptureSnapshot())
        {
            try
            {
                if (_inventoryTarget is not null) await _inventoryTarget.VerifyAsync();
                return true;
            }
            catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
            {
                _logger.LogWarning(ex, "The selected session changed before disk cleanup.");
            }
        }

        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("Reclaim_SnapshotStaleTitle", "Refresh disk usage"),
            UiText.Get("Reclaim_SnapshotStale",
                "The session or engine changed, or the disk usage scan did not finish. Refresh before reclaiming space."));
        return false;
    }

    private async Task ExecutePruneAsync(Func<Task> action)
    {
        if (!await EnsureCurrentInventoryAsync()) return;
        var completed = await ContainerInventoryOperation.RunAsync(action, async error =>
        {
            SetStatus(ReclaimStatus.PruneFailed);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"),
                UiText.Get("Resource_Text_890767d3faf6", "Reclaim could not complete. Refresh the inventory before retrying; some cleanup may already have completed.\n\n{0}", error.Message));
        });
        if (!completed)
        {
            _inventorySelection = null;
            OnPropertyChanged(nameof(InventorySessionDisplay));
            return;
        }

        var target = _inventoryTarget;
        await ScanAsync(target?.Client ?? _wslc, target, ownsBusy: true);
    }

    private async Task RunPruneAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            using var selection = _sessionContext.LockSelection();
            if (await PrepareInventoryAsync()) await action();
        }
        finally
        {
            try
            {
                if (_sessionReader is not null && _sessionSelection is not null && !_showingAllSessions &&
                    !_pageLifetime.IsCancellationRequested)
                    await ScanAllAsync(_pageLifetime);
            }
            finally { IsBusy = false; }
        }
    }

    private static void AppendError(List<string> errors, string label, CommandResult result)
    {
        if (!result.Success)
        {
            errors.Add($"• {label}: {result.ErrorText}");
        }
    }

    private static string Count(int value, string noun) => noun switch
    {
        "dangling image" => value == 1
            ? UiText.Get("Reclaim_DanglingCountOne", "{0} dangling image", value)
            : UiText.Get("Reclaim_DanglingCountMany", "{0} dangling images", value),
        "image" => value == 1
            ? UiText.Get("Resource_Text_6d483483caef", "{0} image", value)
            : UiText.Get("Resource_Text_540058a14492", "{0} images", value),
        "container" => value == 1
            ? UiText.Get("Resource_Text_4554f5b256b4", "{0} container", value)
            : UiText.Get("Resource_Text_d89984bc3e37", "{0} containers", value),
        "volume" => value == 1
            ? UiText.Get("Resource_Text_7e34d9a960e3", "{0} volume", value)
            : UiText.Get("Resource_Text_765e0ff4d3a2", "{0} volumes", value),
        _ => $"{value} {noun}",
    };

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        foreach (var item in LargestImages) item.RefreshLocalizedText();
        foreach (var item in DanglingImages) item.RefreshLocalizedText();
        foreach (var item in UnusedVolumes) item.RefreshLocalizedText();
    }
}
