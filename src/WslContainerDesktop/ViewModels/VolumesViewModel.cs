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
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>View model for the Volumes page, listing <c>wslc</c> volumes and running create, inspect, remove, prune, and bulk-remove commands.</summary>
public partial class VolumesViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => _combined is null ? UiText.Translate(StatusMessage)
        : _combined.IsBusy || _combined.HasRefreshFailure || !_combined.HasLoadedSelection ? _combined.StatusText
        : UiText.Get("Volumes_CombinedSummary", "{0} volumes across {1} active sessions.",
            Volumes.Count, _combined.Sessions.Count(session => session.IsAvailable && session.IsSelected));

    private readonly IWslcService _wslc;
    private readonly DialogService _dialogs;
    private readonly ILogger<VolumesViewModel> _logger;
    private readonly WslcSessionContext _sessionContext;
    private readonly CombinedSessionViewModel? _combined;
    private readonly WslcSessionResourceActions? _sessionActions;
    private readonly Func<WslcSessionInfo, string, IWslcService>? _sessionClient;
    private readonly IWslcSessionSelectionService? _sessionSelection;
    private readonly Dictionary<VolumeInfo, CombinedSessionRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly List<(string Session, string Detail)> _detailErrors = [];
    private bool _detailsTimedOut;
    private CancellationTokenSource? _refreshCancellation;
    private WslcSessionContext.SelectionSnapshot? _inventorySelection;

    /// <summary>Generated busy flag used to disable volume commands while an operation is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status text shown at the top of the Volumes page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    /// <summary>Currently selected volume row, or <c>null</c> when nothing is selected.</summary>
    [ObservableProperty]
    private VolumeInfo? _selected;

    /// <summary>True while the page is showing bulk-selection actions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    /// <summary>Number of rows currently selected for a bulk operation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => UiText.Get("Resource_Text_e5084bffcc17", "{0} selected", SelectedCount);

    /// <summary>Volume rows displayed by the page.</summary>
    public ObservableCollection<VolumeInfo> Volumes { get; } = new();

    /// <summary>Search and sort state for the volume list.</summary>
    public ResourceGridView<VolumeInfo> Grid { get; }

    /// <summary>Creates the volume page model with the services that talk to <c>wslc</c> and show dialogs.</summary>
#if !WSLCD_TEST_NO_DISPATCHER
    public VolumesViewModel(IWslcService wslc, DialogService dialogs, ILogger<VolumesViewModel> logger,
        WslcSessionContext sessionContext, CombinedSessionViewModel combined, WslcSessionResourceActions sessionActions,
        WslcSessionClientFactory clients, IWslcSessionSelectionService sessionSelection)
        : this(wslc, dialogs, logger, sessionContext, combined, sessionActions,
            (session, path) => clients.Create(session, path, managed: false), sessionSelection)
    {
    }
#endif

    internal VolumesViewModel(IWslcService wslc, DialogService dialogs, ILogger<VolumesViewModel> logger,
        WslcSessionContext sessionContext, CombinedSessionViewModel? combined = null,
        WslcSessionResourceActions? sessionActions = null,
        Func<WslcSessionInfo, string, IWslcService>? sessionClient = null,
        IWslcSessionSelectionService? sessionSelection = null)
    {
        _wslc = wslc;
        _dialogs = dialogs;
        _logger = logger;
        _sessionContext = sessionContext;
        _combined = combined;
        _sessionActions = sessionActions;
        _sessionClient = sessionClient;
        _sessionSelection = sessionSelection;
        Grid = new(Volumes, volume =>
            [volume.SessionName, volume.Name, volume.DisplayName, volume.TypeLabel, volume.UsedByDisplay,
             volume.Mountpoint, volume.CreatedAt?.ToString(),
             volume.CreatedAt is { } created ? FormatHelpers.RelativeTime(created) : null],
            new("session", "ResourceGrid_Session", "Session", volume => volume.SessionName),
            new("name", "ResourceGrid_Name", "Name", volume => volume.Name),
            new("type", "ResourceGrid_Type", "Type", volume => volume.TypeLabel),
            new("usedby", "ResourceGrid_UsedBy", "Used by", volume => volume.UsedByDisplay),
            new("created", "ResourceGrid_Created", "Created", volume => volume.CreatedAt));
        if (_combined is not null)
            _combined.PropertyChanged += OnCombinedChanged;
    }

    /// <summary>Reloads volumes, enriches them with inspect metadata, and resolves which containers use them.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_combined is not null)
        {
            await RefreshCombinedAsync(ct);
            return;
        }
        using var selection = _sessionContext.LockSelection();
        _inventorySelection = null;
        var snapshot = _sessionContext.CaptureSnapshot();
        _refreshCancellation?.Cancel();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refreshCancellation = refresh;
        refresh.CancelAfter(TimeSpan.FromSeconds(45));
        ct = refresh.Token;
        IsBusy = true;
        if (Volumes.Count == 0)
        {
            StatusMessage = "Loading volumes…";
        }

        try
        {
            var volumes = (await _wslc.ListVolumesAsync(ct)).ToList();

            // Enrich each volume with created-time + anonymous flag from inspect, a few at a time,
            // while the container listing for usage runs alongside. Awaiting both together means a
            // failure in either still fails the refresh and neither is left unobserved.
            using var gate = new SemaphoreSlim(4);
            var containersTask = _wslc.ListContainersAsync(all: true, ct: ct);
            var inspectsTask = Task.WhenAll(volumes.Select(async v =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var inspect = await _wslc.InspectVolumeAsync(v.Name, ct);
                    if (inspect.Success)
                    {
                        v.EnrichFromInspect(inspect.StandardOutput);
                    }
                    else
                    {
                        _logger.LogWarning("Could not inspect volume {Volume}: {Error}", v.Name, inspect.ErrorText);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            await Task.WhenAll(inspectsTask, containersTask);
            var containers = await containersTask;
            var warnings = await VolumeUsageResolver.ResolveAsync(volumes, containers,
                (id, token) => _wslc.InspectContainerAsync(id, token), ct);
            foreach (var warning in warnings)
            {
                _logger.LogWarning("Volume usage: {Warning}", warning);
            }

            ct.ThrowIfCancellationRequested();
            using (Grid.DeferRefresh())
                CollectionSync.ReplaceAll(Volumes, volumes
                    .OrderBy(v => v.IsAnonymous)
                    .ThenByDescending(v => v.CreatedAt ?? DateTimeOffset.MinValue)
                    .ToList());

            StatusMessage = $"{Volumes.Count} volume{(Volumes.Count == 1 ? "" : "s")}";
            if (warnings.Count > 0)
            {
                StatusMessage += " - usage incomplete; unknown does not mean unused";
            }
            _inventorySelection = snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                StatusMessage = "Volume refresh cancelled or timed out; displayed data may be stale";
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_587588037564", "Failed to load volumes"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                _refreshCancellation = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>Cancels the current refresh when the page is leaving or being replaced.</summary>
    public void CancelRefresh() => _refreshCancellation?.Cancel();

    /// <summary>Warnings about incomplete session inventories or volume details.</summary>
    public string CombinedWarning => string.Join(Environment.NewLine,
        new[]
        {
            _combined?.WarningText ?? string.Empty,
            _detailsTimedOut ? UiText.Get("SessionInventory_RefreshCancelled",
                "Inventory refresh cancelled or timed out; displayed data may be stale.") : string.Empty,
        }.Concat(_detailErrors.Select(error =>
            UiText.Get("Volumes_SessionDetailsUnavailable",
                "{0}: volume details may be incomplete: {1}", error.Session, error.Detail)))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    /// <summary>Session used by actions that are not tied to an existing row.</summary>
    public string ManagementDestination => UiText.Get("ResourceSessionDestination",
        "Choose a session when creating or pruning. Existing resources stay in their own session.");

    private void OnCombinedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.VolumeRows))
            SyncCombinedRows();
        if (args.PropertyName is nameof(CombinedSessionViewModel.WarningText) or nameof(CombinedSessionViewModel.StatusText))
        {
            OnPropertyChanged(nameof(CombinedWarning));
            OnPropertyChanged(nameof(StatusMessageDisplay));
        }
    }

    private void SyncCombinedRows()
    {
        if (_combined is null)
            return;
        var old = new Dictionary<(WslcSessionInfo, VolumeInfo), Queue<VolumeInfo>>();
        foreach (var pair in _rows)
        {
            var key = (pair.Value.Session, pair.Value.Volume!);
            if (!old.TryGetValue(key, out var matches))
                old[key] = matches = new Queue<VolumeInfo>();
            matches.Enqueue(pair.Key);
        }
        var rows = _combined.VolumeRows.Where(row => row.Volume is not null).ToArray();
        _rows.Clear();
        foreach (var row in rows)
        {
            var source = row.Volume!;
            var volume = old.TryGetValue((row.Session, source), out var matches) && matches.Count > 0
                ? matches.Dequeue() : CopyVolume(source);
            volume.SessionName = row.SessionName;
            volume.SessionActionsAvailable = row.CanRemoveVolume;
            _rows[volume] = row;
        }
        var volumes = _rows.Keys.OrderBy(volume => volume.SessionName, StringComparer.Ordinal)
            .ThenBy(volume => volume.IsAnonymous)
            .ThenByDescending(volume => volume.CreatedAt ?? DateTimeOffset.MinValue).ToArray();
        if (!Volumes.SequenceEqual(volumes, ReferenceEqualityComparer.Instance))
        {
            using (Grid.DeferRefresh())
                CollectionSync.ReplaceAll(Volumes, volumes);
        }
        Grid.Refresh();
    }

    private static VolumeInfo CopyVolume(VolumeInfo source) => new()
    {
        Name = source.Name, Driver = source.Driver, Mountpoint = source.Mountpoint,
        CreatedAt = source.CreatedAt, IsAnonymous = source.IsAnonymous,
        ContainerUsers = source.ContainerUsers.ToArray(), UsageState = source.UsageState,
    };

    /// <summary>Reuses recent shared inventory on navigation.</summary>
    public Task RefreshOnNavigationAsync() =>
        _combined is null ? RefreshAsync() : RefreshCombinedAsync(CancellationToken.None, reuseRecent: true);

    private async Task RefreshCombinedAsync(CancellationToken ct, bool reuseRecent = false)
    {
        if (_combined is null || _sessionClient is null)
            throw new InvalidOperationException("The session inventory is not configured.");
        _refreshCancellation?.Cancel();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refreshCancellation = refresh;
        refresh.CancelAfter(TimeSpan.FromSeconds(45));
        IsBusy = true;
        _detailErrors.Clear();
        _detailsTimedOut = false;
        OnPropertyChanged(nameof(CombinedWarning));
        try
        {
            if (reuseRecent) await _combined.RefreshForNavigationAsync(refresh.Token);
            else await _combined.RefreshAllAsync(refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            if (_combined.HasRefreshFailure)
                return;
            SyncCombinedRows();
            var executable = _combined.CurrentExecutablePath;
            var snapshot = _combined.Inventory;
            var groups = _rows.ToArray().GroupBy(pair => pair.Value.Session).ToArray();
            using var gate = new SemaphoreSlim(4);
            foreach (var group in groups)
            {
                var session = group.Key;
                var captured = group.ToArray();
                var detail = captured.Select(pair =>
                {
                    var copy = CopyVolume(pair.Key);
                    copy.ContainerUsers = [];
                    copy.UsageState = VolumeUsageState.Unknown;
                    return copy;
                }).ToArray();
                foreach (var (volume, _) in captured)
                {
                    volume.ContainerUsers = [];
                    volume.UsageState = VolumeUsageState.Unknown;
                    volume.RefreshLocalizedText();
                }
                try
                {
                    var client = _sessionClient(session, executable);
                    await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                        session, executable, refresh.Token);
                    if (!ReferenceEquals(_refreshCancellation, refresh))
                        return;
                    var containers = snapshot.Containers.Where(row => row.Session == session)
                        .Select(row => row.Value).ToArray();
                    var complete = !snapshot.Failures.Any(failure => failure.Session == session &&
                        failure.Kind is WslcCombinedInventory.FailureKind.Containers or WslcCombinedInventory.FailureKind.Session);
                    await Task.WhenAll(detail.Select(async volume =>
                    {
                        await gate.WaitAsync(refresh.Token);
                        try
                        {
                            var result = await client.InspectVolumeAsync(volume.Name, refresh.Token);
                            if (!result.Success)
                                throw new InvalidOperationException(result.ErrorText);
                            volume.EnrichFromInspect(result.StandardOutput);
                        }
                        finally { gate.Release(); }
                    }));
                    if (complete)
                    {
                        var warnings = await VolumeUsageResolver.ResolveAsync(detail, containers,
                            (id, token) => client.InspectContainerAsync(id, token), refresh.Token);
                        foreach (var warning in warnings)
                            _logger.LogWarning("Volume usage in session {Session}: {Warning}", session.Name, warning);
                    }
                    await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                        session, executable, refresh.Token);
                    for (var index = 0; index < captured.Length; index++)
                    {
                        var (volume, originalRow) = captured[index];
                        if (!_rows.TryGetValue(volume, out var currentRow) || currentRow.Session != originalRow.Session ||
                            !ReferenceEquals(currentRow.Volume, originalRow.Volume))
                            continue;
                        var updated = detail[index];
                        volume.CreatedAt = updated.CreatedAt;
                        volume.IsAnonymous = updated.IsAnonymous;
                        volume.ContainerUsers = updated.ContainerUsers;
                        volume.UsageState = updated.UsageState;
                        volume.RefreshLocalizedText();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                           System.ComponentModel.Win32Exception or TimeoutException)
                {
                    _logger.LogWarning(ex, "Could not read volume details for session {Session}.", session.Name);
                    if (ReferenceEquals(_refreshCancellation, refresh))
                        _detailErrors.Add((session.Name, ex.Message));
                }
                catch (OperationCanceledException ex) when (!refresh.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Volume details were unexpectedly cancelled for session {Session}.", session.Name);
                    if (ReferenceEquals(_refreshCancellation, refresh))
                        _detailErrors.Add((session.Name, UiText.Get("CombinedSession_UnexpectedCancellation",
                            "The engine cancelled the request unexpectedly.")));
                }
            }
            OnPropertyChanged(nameof(CombinedWarning));
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                _detailsTimedOut = true;
                OnPropertyChanged(nameof(CombinedWarning));
                StatusMessage = UiText.Get("SessionInventory_RefreshCancelled",
                    "Inventory refresh cancelled or timed out; displayed data may be stale.");
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, refresh))
            {
                _refreshCancellation = null;
                IsBusy = false;
            }
        }
    }

    /// <summary>Prompts for a volume name and creates it with <c>wslc</c>.</summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (_combined is not null && target is null) return;
        var title = target is null ? UiText.Get("Resource_Text_1c2351331a73", "Create volume")
            : UiText.Get("Volumes_CreateForSession", "Create volume in {0}", target.Selection.DisplayName);
        var dialog = new SimpleInputDialog(title, UiText.Get("Resource_Text_9665af7c3b74", "Volume name"), UiText.Get("Resource_Text_389e0c4950f6", "e.g. my-data"));
        if (target is not null)
            dialog.SetSession(target.Selection, "Volumes_CreateForSession", "Create volume in {0}",
                "Resource_Text_9665af7c3b74", "Volume name", "Resource_Text_389e0c4950f6", "e.g. my-data");
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var name = dialog.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            if (target is not null) await target.VerifyAsync();
            return await (target?.Client ?? _wslc).CreateVolumeAsync(name);
        });
    }

    /// <summary>Confirms and removes one selected volume.</summary>
    [RelayCommand]
    private async Task RemoveAsync(VolumeInfo? volume)
    {
        using var selection = _sessionContext.LockSelection();
        volume ??= Selected;
        if (volume is null)
        {
            return;
        }

        if (!await EnsureCurrentInventoryAsync([volume]))
            return;

        if (_combined is not null)
        {
            await RemoveCombinedAsync(volume);
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_7fa7f416b8c2", "Remove volume"),
            UiText.Get("Resource_Text_9cd3f346fe82", "Remove volume \"{0}\"? Data in the volume will be lost.", volume.Name),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(() => _wslc.RemoveVolumeAsync(volume.Name), volume.Name);
    }

    /// <summary>Shows raw inspect output for the selected volume.</summary>
    [RelayCommand]
    private async Task InspectAsync(VolumeInfo? volume)
    {
        using var selection = _sessionContext.LockSelection();
        volume ??= Selected;
        if (volume is null)
        {
            return;
        }

        if (!await EnsureCurrentInventoryAsync([volume]))
            return;

        if (_combined is not null)
        {
            await InspectCombinedAsync(volume);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _wslc.InspectVolumeAsync(volume.Name);
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_f306c4f01fc3", "Inspect · {0}", volume.Name),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Confirms and asks the engine to remove every volume it considers unused.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger, startDefault: false);
        if (_combined is not null && target is null) return;
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_cb5d30ca0eaa", "Prune volumes"),
            target is null ? UiText.Get("Resource_Text_075341e9d7a5", "Remove all unused volumes?")
                : UiText.Get("Volumes_PruneForSession", "Remove all unused volumes in {0}?", target.DisplayName),
            UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        await ExecuteAsync(async () =>
        {
            if (target is not null) await target.VerifyAsync();
            return await (target?.Client ?? _wslc).PruneVolumesAsync();
        });
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            SelectedCount = 0;
        }
    }

    /// <summary>Removes every selected volume after a single confirmation, then exits selection mode.</summary>
    public async Task BulkRemoveAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        using var selection = _sessionContext.LockSelection();
        var items = volumes?.Where(v => v is not null).ToList() ?? new List<VolumeInfo>();
        if (items.Count == 0)
        {
            return;
        }

        if (!await EnsureCurrentInventoryAsync(items))
            return;

        if (_combined is not null)
        {
            await BulkRemoveCombinedAsync(items);
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_97e7c38762f2", "Remove volumes"),
            UiText.Get("Resource_Text_895d5f585893", "Remove {0} volume(s)? Data in these volumes will be lost.\n\n{1}", items.Count, BulkNames(items.Select(v => v.Name))),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        var failures = new List<string>();
        try
        {
            foreach (var v in items)
            {
                var result = await _wslc.RemoveVolumeAsync(v.Name);
                if (!result.Success)
                {
                    failures.Add(v.Name);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        IsSelectionMode = false;
        await RefreshAsync();

        if (failures.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                UiText.Get("Resource_Text_7e216826243b", "Some volumes were not removed"),
                UiText.Get("Resource_Text_2c7e82a6c879", "{0} of {1} could not be removed (they may still be attached to a container):\n\n{2}", failures.Count, items.Count, BulkNames(failures)));
        }
    }

    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? UiText.Get("Resource_Text_5b44479e9557", "{0}\n… and {1} more", shown, list.Count - max) : shown;
    }

    private async Task<bool> EnsureCurrentInventoryAsync(IReadOnlyList<VolumeInfo> items)
    {
        if (_combined is not null)
        {
            if (_combined.IsBusy)
            {
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
                    UiText.Get("CombinedSession_RefreshBusy", "Another session refresh is already running."));
                return false;
            }
            if (items.All(item => Volumes.Contains(item) && _rows.TryGetValue(item, out var row) &&
                _combined.IsCurrentRow(row)))
                return true;
        }
        else
        {
            if (_inventorySelection is { } snapshot && snapshot == _sessionContext.CaptureSnapshot() &&
                items.All(Volumes.Contains))
                return true;
        }

        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
            UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        return false;
    }

    private async Task InspectCombinedAsync(VolumeInfo volume)
    {
        if (_sessionActions is null || !_rows.TryGetValue(volume, out var row))
            throw new InvalidOperationException("The selected volume has no session target.");
        IsBusy = true;
        try
        {
            var result = await _sessionActions.InspectAsync(row.Session,
                WslcResourceActionTarget.ResourceKind.Volume, volume.Name);
            await _dialogs.ShowLocalizedMessageAsync(
                UiText.Get("CombinedSession_InspectTitle", "Inspect {0} · {1}", volume.Name, row.SessionName),
                result.Success ? result.StandardOutput : result.ErrorText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not inspect volume {Volume} in session {Session}.", volume.Name, row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_587588037564", "Failed to load volumes"), ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Volume inspection was cancelled for session {Session}.", row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_UnexpectedCancellation", "The engine cancelled the request unexpectedly."));
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveCombinedAsync(VolumeInfo volume)
    {
        if (_sessionActions is null || _combined is null || !_rows.TryGetValue(volume, out var row))
            throw new InvalidOperationException("The selected volume has no session target.");
        IsBusy = true;
        try
        {
            var target = await _sessionActions.PrepareVolumeRemovalAsync(row.Session, volume.Name);
            var created = target.CreatedAt ?? throw new InvalidOperationException(UiText.Get("CombinedSession_VolumeIdentityUnknown",
                "The volume's creation identity could not be verified; no removal was attempted."));
            if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("CombinedSession_RemoveResourceTitle", "Remove resource"),
                    UiText.Get("CombinedSession_RemoveVolumeConfirmation",
                        "Remove volume \"{0}\" from session \"{1}\"? All data in the volume will be lost.\n\nThe volume currently has creation time {2}; it may differ from the loaded snapshot.",
                        target.Name, target.Session.Name, created.ToLocalTime().ToString("g")),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;
            if (!await EnsureCurrentInventoryAsync([volume]))
                return;
            _combined.InvalidateSession(row.Session);
            var result = await _sessionActions.RemoveAsync(target);
            if (!result.Success)
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_69047d7b411a", "Can't remove volume"), result.ErrorText);
            await _combined.RefreshSessionAsync(row.Session);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not remove volume {Volume} in session {Session}.", volume.Name, row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_69047d7b411a", "Can't remove volume"), ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Volume removal was interrupted for session {Session}.", row.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying."));
        }
        finally { IsBusy = false; }
    }

    private async Task BulkRemoveCombinedAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        if (_sessionActions is null || _combined is null)
            throw new InvalidOperationException("The session actions are not configured.");
        IsBusy = true;
        try
        {
            var rows = new List<CombinedSessionRow>();
            foreach (var volume in volumes)
            {
                if (!_rows.TryGetValue(volume, out var row))
                    throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                        "The session, engine, or displayed resources changed. Refresh before taking this action."));
                rows.Add(row);
            }
            var targets = new List<WslcResourceActionTarget>();
            foreach (var row in rows)
            {
                var volume = row.Volume ?? throw new InvalidOperationException("The selected volume has no identity.");
                targets.Add(await _sessionActions.PrepareVolumeRemovalAsync(row.Session, volume.Name));
            }
            if (!await _dialogs.ShowLocalizedConfirmAsync(
                    UiText.Get("Resource_Text_97e7c38762f2", "Remove volumes"),
                    UiText.Get("Volumes_CombinedRemoveConfirmation",
                        "Remove {0} volume(s) from their listed sessions? Data will be lost. Some removals may fail independently.\n\n{1}",
                        targets.Count, BulkNames(targets.Select(target =>
                            $"{target.Session.Name}: {target.Name} ({target.CreatedAt?.ToLocalTime():g})"))),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;
            if (!await EnsureCurrentInventoryAsync(volumes))
                return;
            var failures = new List<string>();
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                try
                {
                    var result = await _sessionActions.RemoveAsync(target);
                    if (!result.Success)
                        failures.Add($"{target.Session.Name}: {target.Name}: {result.ErrorText}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                           System.ComponentModel.Win32Exception or TimeoutException)
                {
                    _logger.LogWarning(ex, "Could not remove volume {Volume} in session {Session}.", target.Name, target.Session.Name);
                    failures.Add($"{target.Session.Name}: {target.Name}: {ex.Message}");
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Volume removal was interrupted for session {Session}.", target.Session.Name);
                    failures.Add(UiText.Get("ResourceSession_BulkInterrupted",
                        "The current action's outcome may be unknown; {0} later item(s) were not attempted.",
                        targets.Count - index - 1));
                    break;
                }
            }
            IsSelectionMode = false;
            await RefreshCombinedAsync(CancellationToken.None);
            if (failures.Count > 0)
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_7e216826243b", "Some volumes were not removed"),
                    string.Join(Environment.NewLine, failures));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                   System.ComponentModel.Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not prepare session-qualified volume removal.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_7e216826243b", "Some volumes were not removed"), ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Bulk volume removal was interrupted.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionInterrupted", "Resource action interrupted"),
                UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying."));
        }
        finally { IsBusy = false; }
    }

    private async Task ExecuteAsync(Func<Task<CommandResult>> action, string? volumeName = null)
    {
        IsBusy = true;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                var message = result.ErrorText;

                // Friendlier message for the common "volume is attached to a container" case.
                if (message.Contains("ERROR_SHARING_VIOLATION", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("is in use", StringComparison.OrdinalIgnoreCase))
                {
                    var name = string.IsNullOrEmpty(volumeName) ? UiText.Get("Resource_Text_815f075b8069", "This volume") : $"\"{volumeName}\"";
                    message =
                        UiText.Get("Resource_Text_01d84a089f65", "{0} is still attached to a container, so it can't be removed.\n\n", name) +
                        UiText.Get("Resource_Text_0d553567c19c", "Stop and remove the container that uses it first, then try again.\n\n") +
                        UiText.Get("Resource_Text_ea8c28b99ef3", "The Used by column includes stopped containers when inspect metadata is available. ") +
                        UiText.Get("Resource_Text_148f26a65911", "Unknown or partial usage is not proof that a volume is unused.");
                }

                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_69047d7b411a", "Can't remove volume"), message);
            }
            else
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex) when (SessionOperationTarget.IsSessionFailure(ex))
        {
            _logger.LogWarning(ex, "Volume operation failed for the chosen session.");
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_c4e6ed956953", "Operation failed"),
                ex is OperationCanceledException
                    ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                        "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                    : ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        using (Grid.DeferRefresh())
            foreach (var item in Volumes) item.RefreshLocalizedText();
    }
}
