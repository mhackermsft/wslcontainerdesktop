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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Backs the Images page, where users pull, build, tag, push, save, restore and remove container images through the WSL container engine.</summary>
public partial class ImagesViewModel : ObservableObject
{
    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusMessageDisplay => _combined is null || IsBusy && !_loadingCombined
        ? UiText.Translate(StatusMessage)
        : _combined.IsBusy || _combined.HasRefreshFailure || !_combined.HasLoadedSelection ? _combined.StatusText
        : UiText.Get("Images_CombinedSummary", "{0} images across {1} active sessions.",
            Images.Count, _combined.Sessions.Count(session => session.IsAvailable && session.IsSelected));

    /// <summary>Text projected for the active UI language.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BuildPolicyMessageDisplay => UiText.Translate(BuildPolicyMessage);

    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly RegistryAuthRefresher _authRefresher;
    private readonly INotificationService _notifications;
    private readonly IRunProfileStore _profiles;
    private readonly IActivityLog _activity;
    private readonly IImageUpdateService _updates;
    private readonly IRegistryCatalogService _catalog;
    private readonly IWslPolicyService _policy;
    private readonly IRegistryCredentialStore _credentials;
    private readonly IWslcSettingsFileService _wslcSettings;
    private readonly WslcSessionContext _sessionContext;
    private readonly CombinedSessionViewModel? _combined;
    private readonly WslcSessionResourceActions? _sessionActions;
    private readonly Func<WslcSessionInfo, string, bool, IWslcService>? _sessionClient;
    private readonly IWslcSessionSelectionService? _sessionSelection;
    private readonly ILogger<ImagesViewModel> _logger;
    private readonly Dictionary<ImageInfo, CombinedSessionRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly List<(string Session, string Detail)> _detailErrors = [];
    private bool _loadingCombined;
    private WslcSessionContext.SelectionSnapshot? _inventorySelection;
    private long _refreshGeneration;

    /// <summary>Whether busy for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private bool _isBusy;

    /// <summary>Bindable state for status message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessageDisplay))]
    private string _statusMessage = "Ready";

    /// <summary>Value for selected shown or edited by the view.</summary>
    [ObservableProperty]
    private ImageInfo? _selected;

    /// <summary>Whether selection mode for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private bool _isSelectionMode;

    /// <summary>Bindable state for selected count used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    private int _selectedCount;

    /// <summary>Whether the user can build from the view.</summary>
    [ObservableProperty]
    private bool _canBuild = true;

    /// <summary>Bindable state for build policy message used by the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildPolicyMessageDisplay))]
    private string _buildPolicyMessage = "Build an image from a Dockerfile";

    /// <summary>Header text for the bulk-action bar, e.g. "3 selected".</summary>
    public string SelectionSummary => UiText.Get("Resource_Text_e5084bffcc17", "{0} selected", SelectedCount);

    /// <summary>Value for images shown or edited by the view.</summary>
    public ObservableCollection<ImageInfo> Images { get; } = new();

    /// <summary>Search and sort state for the image list.</summary>
    public ResourceGridView<ImageInfo> Grid { get; }

    /// <summary>Creates the Images view model and stores its injected services.</summary>
#if !WSLCD_TEST_NO_DISPATCHER
    public ImagesViewModel(IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings,
        RegistryAuthRefresher authRefresher, INotificationService notifications, IRunProfileStore profiles,
        IActivityLog activity, IImageUpdateService updates, IRegistryCatalogService catalog, IWslPolicyService policy,
        IRegistryCredentialStore credentials, IWslcSettingsFileService wslcSettings, WslcSessionContext sessionContext,
        CombinedSessionViewModel combined, WslcSessionResourceActions sessionActions, WslcSessionClientFactory clients,
        ILogger<ImagesViewModel> logger, IWslcSessionSelectionService sessionSelection)
        : this(wslc, monitor, dialogs, settings, authRefresher, notifications, profiles, activity, updates, catalog,
            policy, credentials, wslcSettings, sessionContext, combined, sessionActions, clients.Create, logger, sessionSelection)
    {
    }
#endif

    internal ImagesViewModel(IWslcService wslc, StatusMonitor monitor, DialogService dialogs, ISettingsService settings,
        RegistryAuthRefresher authRefresher, INotificationService notifications, IRunProfileStore profiles,
        IActivityLog activity, IImageUpdateService updates, IRegistryCatalogService catalog, IWslPolicyService policy,
        IRegistryCredentialStore credentials, IWslcSettingsFileService wslcSettings, WslcSessionContext sessionContext,
        CombinedSessionViewModel? combined = null, WslcSessionResourceActions? sessionActions = null,
        Func<WslcSessionInfo, string, bool, IWslcService>? sessionClient = null, ILogger<ImagesViewModel>? logger = null,
        IWslcSessionSelectionService? sessionSelection = null)
    {
        _wslc = wslc;
        _monitor = monitor;
        _dialogs = dialogs;
        _settings = settings;
        _authRefresher = authRefresher;
        _notifications = notifications;
        _profiles = profiles;
        _activity = activity;
        _updates = updates;
        _catalog = catalog;
        _policy = policy;
        _credentials = credentials;
        _wslcSettings = wslcSettings;
        _sessionContext = sessionContext;
        _combined = combined;
        _sessionActions = sessionActions;
        _sessionClient = sessionClient;
        _sessionSelection = sessionSelection;
        _logger = logger ?? NullLogger<ImagesViewModel>.Instance;
        Grid = new(Images, image =>
            [image.SessionName, image.Repository, image.Tag, image.Reference, image.Id, image.DigestDisplay,
             image.UsedByCaption, FormatHelpers.HumanSize(image.Size), image.CreatedUtc.ToString(),
             FormatHelpers.RelativeTime(image.CreatedUtc)],
            new("session", "ResourceGrid_Session", "Session", image => image.SessionName),
            new("repository", "ResourceGrid_Repository", "Repository", image => image.Repository),
            new("tag", "ResourceGrid_Tag", "Tag", image => image.Tag),
            new("id", "ResourceGrid_ImageId", "Image ID", image => image.Id),
            new("size", "ResourceGrid_Size", "Size", image => image.Size),
            new("created", "ResourceGrid_Created", "Created", image => image.CreatedUtc));
        if (_combined is not null)
            _combined.PropertyChanged += OnCombinedChanged;
    }

    /// <summary>Saved run profiles that target the given image, for the one-click run submenu.</summary>
    public IReadOnlyList<RunProfile> ProfilesForImage(string image) => _profiles.GetForImage(image);

    /// <summary>Command handler for refresh actions triggered from the view.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_combined is not null)
        {
            await RefreshCombinedAsync();
            return;
        }
        await RefreshLegacyAsync();
    }

    /// <summary>Warnings about incomplete session inventories or image usage.</summary>
    public string CombinedWarning => string.Join(Environment.NewLine,
        new[] { _combined?.WarningText ?? string.Empty }.Concat(_detailErrors.Select(error =>
            UiText.Get("Images_SessionDetailsUnavailable",
                "{0}: image usage may be incomplete: {1}", error.Session, error.Detail)))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    /// <summary>Session used by actions that are not tied to an existing row.</summary>
    public string ManagementDestination => UiText.Get("Images_ManagementDestination",
        "Choose a session when pulling, building, importing or pruning. Existing images stay in their own session.");

    private void OnCombinedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CombinedSessionViewModel.ImageRows))
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
        var old = new Dictionary<(WslcSessionInfo, ImageInfo), Queue<ImageInfo>>();
        foreach (var pair in _rows)
        {
            var key = (pair.Value.Session, pair.Value.Image!);
            if (!old.TryGetValue(key, out var matches))
                old[key] = matches = new Queue<ImageInfo>();
            matches.Enqueue(pair.Key);
        }
        var rows = _combined.ImageRows.Where(row => row.Image is not null).ToArray();
        _rows.Clear();
        foreach (var row in rows)
        {
            var source = row.Image!;
            var image = old.TryGetValue((row.Session, source), out var matches) && matches.Count > 0
                ? matches.Dequeue() : CopyImage(source);
            image.SessionName = row.SessionName;
            image.SessionActionsAvailable = row.CanRemoveImage;
            _rows[image] = row;
        }
        var images = _rows.Keys.OrderBy(image => image.SessionName, StringComparer.Ordinal)
            .ThenBy(image => image.Repository).ThenBy(image => image.Tag).ToArray();
        if (!Images.SequenceEqual(images, ReferenceEqualityComparer.Instance))
        {
            using (Grid.DeferRefresh())
                CollectionSync.ReplaceAll(Images, images);
        }
        Grid.Refresh();
    }

    private static ImageInfo CopyImage(ImageInfo source) => new()
    {
        Id = source.Id,
        Repository = source.Repository,
        Tag = source.Tag,
        Created = source.Created,
        CreatedAt = source.CreatedAt,
        Size = source.Size,
        Digest = source.Digest,
        UsedBy = source.UsedBy,
        UpdateState = source.UpdateState,
    };

    /// <summary>Reuses recent shared inventory on navigation.</summary>
    public Task RefreshOnNavigationAsync() =>
        _combined is null ? RefreshAsync() : RefreshCombinedAsync(reuseRecent: true);

    private async Task RefreshCombinedAsync(bool reuseRecent = false)
    {
        if (_combined is null)
            throw new InvalidOperationException("The session inventory is not configured.");
        var generation = ++_refreshGeneration;
        RefreshPolicyState();
        _loadingCombined = true;
        IsBusy = true;
        _detailErrors.Clear();
        OnPropertyChanged(nameof(CombinedWarning));
        try
        {
            if (reuseRecent) await _combined.RefreshForNavigationAsync();
            else await _combined.RefreshAllAsync();
            if (generation != _refreshGeneration || _combined.HasRefreshFailure)
                return;
            SyncCombinedRows();
            var snapshot = _combined.Inventory;
            foreach (var group in _rows.GroupBy(pair => pair.Value.Session))
            {
                var session = group.Key;
                var images = group.Select(pair => pair.Key).ToArray();
                foreach (var image in images)
                {
                    image.UsedBy = string.Empty;
                    image.UsageUnknown = true;
                }
                if (snapshot.Failures.Any(failure => failure.Session == session &&
                    failure.Kind is WslcCombinedInventory.FailureKind.Containers or WslcCombinedInventory.FailureKind.Session))
                    continue;
                try
                {
                    ImageUsageResolver.Apply(images, snapshot.Containers.Where(row => row.Session == session)
                        .Select(row => row.Value).ToArray());
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    _logger.LogWarning(ex, "Could not resolve image usage in session {Session}.", session.Name);
                    _detailErrors.Add((session.Name, ex.Message));
                }
            }
            OnPropertyChanged(nameof(CombinedWarning));
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                _loadingCombined = false;
                IsBusy = false;
                OnPropertyChanged(nameof(StatusMessageDisplay));
            }
        }
    }

    private async Task RefreshLegacyAsync()
    {
        using var selection = _sessionContext.LockSelection();
        _inventorySelection = null;
        var snapshot = _sessionContext.CaptureSnapshot();
        var generation = ++_refreshGeneration;
        RefreshPolicyState();
        IsBusy = true;
        if (Images.Count == 0)
        {
            StatusMessage = "Loading images…";
        }
        try
        {
            // Both calls are independent engine round trips, so run them together. The usage lookup
            // never faults, so a failed image listing doesn't leave it unobserved.
            var imagesTask = _wslc.ListImagesAsync();
            var containersTask = ListContainersForUsageAsync();
            var images = await imagesTask;
            foreach (var image in images) image.UsageUnknown = true;

            // Which container holds each image. A dangling row otherwise reads "<none> <none>",
            // which says an image is untagged but not why it is still on disk or why removing it
            // fails — and the answer is almost always a container still referencing it.
            if (await containersTask is { } containers)
            {
                try
                {
                    ImageUsageResolver.Apply(images, containers);
                }
                catch (Exception)
                {
                    // Same reason as ListContainersForUsageAsync: usage is optional annotation.
                }
            }

            if (generation != _refreshGeneration)
                return;
            using (Grid.DeferRefresh())
                CollectionSync.ReplaceAll(Images, images.OrderBy(i => i.Repository).ThenBy(i => i.Tag).ToList());
            StatusMessage = $"{Images.Count} image{(Images.Count == 1 ? "" : "s")}";
            _inventorySelection = snapshot;
        }
        catch (Exception ex)
        {
            if (generation != _refreshGeneration)
                return;
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_68d903613f94", "Failed to load images"), ex.Message);
            StatusMessage = "Error";
        }
        finally
        {
            if (generation == _refreshGeneration)
                IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<ContainerInfo>?> ListContainersForUsageAsync()
    {
        try
        {
            return await _wslc.ListContainersAsync(all: true);
        }
        catch (Exception)
        {
            // Deliberately silent: usage is an annotation on the listing, not the listing
            // itself, and the rows are still correct and actionable without it.
            return null;
        }
    }

    /// <summary>Command handler for pull actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PullAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (_combined is not null && target is null)
            return;
        var client = target?.Client ?? _wslc;
        var dialog = new PullImageDialog(_settings.Registries, _catalog);
        if (target is not null)
            dialog.SetSession(target.Selection);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var reference = dialog.Reference.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        if (dialog.AllTags)
        {
            reference = WslcService.StripTag(reference);
        }

        IsBusy = true;
        StatusMessage = dialog.AllTags
            ? $"Pulling all tags of {reference}… (this can take a while)"
            : $"Pulling {reference}… (this can take a while)";
        try
        {
            // Refresh an Azure-backed registry's token just-in-time so the pull authenticates.
            if (target is not null) await target.VerifyAsync();
            await _authRefresher.EnsureFreshForReferenceAsync(reference, sessionClient: client,
                verifyTarget: target is null ? null : token => target.VerifyAsync(token));
            if (target is not null) await target.VerifyAsync();

            var result = await client.PullImageAsync(reference, allTags: dialog.AllTags);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_8ee0e62563d4", "Pull failed"), result.ErrorText);
                StatusMessage = "Pull failed";
                _notifications.NotifyImagePull(reference, success: false, result.ErrorText);
                _activity.RecordImagePull(reference, success: false, result.ErrorText, ActivityOwner(target));
            }
            else
            {
                StatusMessage = dialog.AllTags ? $"Pulled all tags for {reference}" : $"Pulled {reference}";
                _notifications.NotifyImagePull(reference, success: true);
                _activity.RecordImagePull(reference, success: true, owner: ActivityOwner(target));
                await RefreshAsync();
            }
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Image pull could not complete for the chosen session.");
            await ShowSessionActionErrorAsync(ex);
            StatusMessage = "Pull failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Checks every tagged image against its upstream registry digest and updates each row's
    /// <see cref="ImageInfo.UpdateState"/> so an "update available" badge can show. Runs the checks
    /// concurrently; images with no tag or no registry digest are skipped.
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        List<ImageInfo> candidates;
        WslcSessionContext.SelectionSnapshot snapshot;
        (ImageInfo Image, IReadOnlyList<string>? Digests)[] observations;
        using (var selection = _sessionContext.LockSelection())
        {
            candidates = Images
                .Where(i => !string.IsNullOrEmpty(i.Tag) && i.Tag != "<none>")
                .ToList();
            if (candidates.Count == 0)
                return;
            if (!await EnsureCurrentInventoryAsync(candidates))
                return;
            snapshot = _sessionContext.CaptureSnapshot();
            StatusMessage = "Checking for image updates…";
            foreach (var image in candidates)
                image.UpdateState = ImageUpdateState.Checking;

            using var engineGate = new SemaphoreSlim(4);
            observations = await Task.WhenAll(candidates.Select(async image =>
            {
                await engineGate.WaitAsync();
                try
                {
                    var (client, _) = await ClientForImageAsync(image);
                    return (image, (IReadOnlyList<string>?)await client.GetImageRepoDigestsAsync(image.Id));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
                                           System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not read image digest for {Image} in session {Session}.",
                        image.Reference, image.SessionName);
                    return (image, (IReadOnlyList<string>?)null);
                }
                finally { engineGate.Release(); }
            }));
        }

        using var registryGate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(observations.Select(async observation =>
        {
            if (observation.Digests is null)
                return (observation.Image, ImageUpdateState.CheckFailed);
            await registryGate.WaitAsync();
            try
            {
                return (observation.Image, await _updates.CheckAsync(observation.Image.Reference, observation.Digests));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Could not check image {Image} in session {Session}.",
                    observation.Image.Reference, observation.Image.SessionName);
                return (observation.Image, ImageUpdateState.CheckFailed);
            }
            finally { registryGate.Release(); }
        }));

        _sessionContext.TryApply(snapshot, () =>
        {
            if (_combined is null && _inventorySelection != snapshot || !candidates.All(Images.Contains))
                return;
            foreach (var (image, state) in results)
                image.UpdateState = state;
            var available = candidates.Count(i => i.UpdateState == ImageUpdateState.UpdateAvailable);
            StatusMessage = available == 0
                ? "All images are up to date."
                : $"{available} image{(available == 1 ? "" : "s")} can be updated.";
        });
    }

    /// <summary>Pulls the latest image for a row that has an update available, then re-checks it.</summary>
    [RelayCommand]
    private async Task PullUpdateAsync(ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        image ??= Selected;
        if (image is null || string.IsNullOrWhiteSpace(image.Reference))
        {
            return;
        }
        if (!await EnsureCurrentInventoryAsync([image]))
            return;

        var reference = image.Reference;
        IsBusy = true;
        StatusMessage = $"Pulling {reference}…";
        try
        {
            var (client, row) = await ClientForImageAsync(image);
            await _authRefresher.EnsureFreshForReferenceAsync(reference, sessionClient: client,
                verifyTarget: _ => VerifySessionAsync(row));
            await ClientForImageAsync(image);
            await VerifySessionAsync(row);

            var result = await client.PullImageAsync(reference);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_8ee0e62563d4", "Pull failed"), result.ErrorText);
                StatusMessage = "Pull failed";
                _notifications.NotifyImagePull(reference, success: false, result.ErrorText);
                _activity.RecordImagePull(reference, success: false, result.ErrorText, ActivityOwner(row));
                return;
            }

            StatusMessage = $"Updated {reference}";
            _notifications.NotifyImagePull(reference, success: true);
            _activity.RecordImagePull(reference, success: true, owner: ActivityOwner(row));
            await RefreshAsync();

            // Re-check the freshly pulled tag so the badge clears.
            var updated = Images.FirstOrDefault(i =>
                string.Equals(i.Reference, reference, StringComparison.Ordinal) &&
                string.Equals(i.SessionName, image.SessionName, StringComparison.Ordinal));
            if (updated is not null)
            {
                updated.UpdateState = ImageUpdateState.Checking;
                try
                {
                    var (updatedClient, _) = await ClientForImageAsync(updated);
                    var digests = await updatedClient.GetImageRepoDigestsAsync(updated.Id);
                    updated.UpdateState = await _updates.CheckAsync(updated.Reference, digests);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Could not recheck image {Image} in session {Session}.",
                        updated.Reference, updated.SessionName);
                    updated.UpdateState = ImageUpdateState.CheckFailed;
                }
            }
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

    /// <summary>Command handler for run actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RunAsync(ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        image ??= Selected;
        if (image is null)
        {
            return;
        }
        if (!await EnsureCurrentInventoryAsync([image]))
            return;

        try
        {
            var (client, row) = await ClientForImageAsync(image);
            var dialog = new RunContainerDialog(client, _settings.Registries, _profiles, image.Reference);
            if (row is not null)
                dialog.Title = UiText.Get("Images_RunInSession", "Run image in {0}", row.SessionName);
            if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Options is null)
                return;
            await ClientForImageAsync(image);
            await ExecuteRunAsync(dialog.Options, client, row, image);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
    }

    /// <summary>Command handler for run profile actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RunProfileAsync(RunProfile? profile)
    {
        using var selection = _sessionContext.LockSelection();
        if (profile is null || string.IsNullOrWhiteSpace(profile.Options.Image))
        {
            return;
        }

        await ExecuteRunAsync(profile.Options);
    }

    /// <summary>Runs a saved profile in the owning session of the selected image.</summary>
    public async Task RunProfileForImageAsync(RunProfile? profile, ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        if (profile is null || image is null || string.IsNullOrWhiteSpace(profile.Options.Image) ||
            !await EnsureCurrentInventoryAsync([image]))
            return;
        if (!string.Equals(profile.Options.Image, image.Reference, StringComparison.Ordinal))
        {
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
                UiText.Get("Images_ProfileChanged", "The saved profile no longer targets this image. Refresh before trying again."));
            return;
        }
        try
        {
            var (client, row) = await ClientForImageAsync(image);
            await ExecuteRunAsync(profile.Options, client, row, image);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
    }

    /// <summary>Launches a container from resolved run options and refreshes the monitor.</summary>
    private async Task ExecuteRunAsync(RunContainerOptions options, IWslcService? sessionClient = null,
        CombinedSessionRow? row = null, ImageInfo? image = null)
    {
        IsBusy = true;
        StatusMessage = $"Running {options.Image}…";
        try
        {
            // `wslc run` auto-pulls if the image is absent, so refresh Azure auth first.
            var client = sessionClient ?? _wslc;
            await _authRefresher.EnsureFreshForReferenceAsync(options.Image, sessionClient: client,
                verifyTarget: _ => VerifySessionAsync(row));
            if (image is not null)
                await ClientForImageAsync(image);
            await VerifySessionAsync(row);

            var result = await client.RunContainerAsync(options);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_9a731f3d6dec", "Run failed"), result.ErrorText);
                StatusMessage = "Run failed";
            }
            else
            {
                StatusMessage = "Container started";
                _monitor.RequestRefresh();
            }
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

    /// <summary>Command handler for remove actions triggered from the view.</summary>
    [RelayCommand]
    private async Task RemoveAsync(ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        image ??= Selected;
        if (image is null)
        {
            return;
        }
        if (!await EnsureCurrentInventoryAsync([image]))
            return;

        if (_combined is not null)
        {
            await RemoveCombinedAsync(image);
            return;
        }

        var ok = await _dialogs.ShowConfirmAsync(
            UiText.Get("Resource_Text_5f94b03c2e6a", "Remove image"),
            UiText.Get("Resource_Text_92bffa1208b8", "Remove image \"{0}\" ({1})?", image.Reference, image.ShortId),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Removing {image.Reference}…";
        try
        {
            var result = await _wslc.RemoveImageAsync(image.Id);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_debe01de259b", "Remove failed"), result.ErrorText);
                StatusMessage = "Remove failed";
            }
            else
            {
                await RefreshAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Handles is selection mode changed changes and updates related view-model state.</summary>
    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
        {
            SelectedCount = 0;
        }
    }

    /// <summary>Removes every selected image after one confirmation, then exits selection mode.</summary>
    public async Task BulkRemoveAsync(IReadOnlyList<ImageInfo> images)
    {
        using var selection = _sessionContext.LockSelection();
        var items = images?.Where(i => i is not null).ToList() ?? new List<ImageInfo>();
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
            UiText.Get("Resource_Text_aea8cc2d34a5", "Remove images"),
            UiText.Get("Resource_Text_eb37f372523b", "Remove {0} image(s)?\n\n{1}", items.Count, BulkNames(items.Select(i => i.Reference))),
            UiText.Get("Resource_Text_e963907dac5c", "Remove"));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Removing {items.Count} image(s)…";
        var failures = new List<string>();
        try
        {
            foreach (var image in items)
            {
                var result = await _wslc.RemoveImageAsync(image.Id);
                if (!result.Success)
                {
                    failures.Add(image.Reference);
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
                UiText.Get("Resource_Text_7800478a75f7", "Some images were not removed"),
                UiText.Get("Resource_Text_ca3ab35903c6", "{0} of {1} could not be removed (they may be in use by a container):\n\n{2}", failures.Count, items.Count, BulkNames(failures)));
        }
        else
        {
            StatusMessage = $"Removed {items.Count} image(s)";
        }
    }

    /// <summary>Helper for the bulk names workflow in this view model.</summary>
    private static string BulkNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        const int max = 12;
        var shown = string.Join("\n", list.Take(max).Select(n => "• " + n));
        return list.Count > max ? UiText.Get("Resource_Text_5b44479e9557", "{0}\n… and {1} more", shown, list.Count - max) : shown;
    }

    /// <summary>Command handler for tag actions triggered from the view.</summary>
    [RelayCommand]
    private async Task TagAsync(ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        image ??= Selected;
        if (image is null)
        {
            return;
        }
        if (!await EnsureCurrentInventoryAsync([image]))
            return;

        var dialog = new SimpleInputDialog(
            _combined is null ? UiText.Get("Resource_Text_95562ee52bdb", "Tag image")
                : UiText.Get("Images_TagInSession", "Tag image in {0}", image.SessionName),
            UiText.Get("Resource_Text_0a6181aadd01", "New tag"),
            UiText.Get("Resource_Text_3ad374153994", "e.g. myrepo/myimage:v1"))
        {
            Value = image.Reference,
        };
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var target = dialog.Value.Trim();
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (client, row) = await ClientForImageAsync(image);
            await VerifySessionAsync(row);
            var result = await client.TagImageAsync(image.Id, target);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_b5d06eb9a6ed", "Tag failed"), result.ErrorText);
            }
            else
            {
                await RefreshAsync();
            }
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

    /// <summary>Command handler for push actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PushAsync(ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        image ??= Selected;
        if (image is null)
        {
            return;
        }
        if (!await EnsureCurrentInventoryAsync([image]))
            return;

        IWslcService client;
        CombinedSessionRow? row;
        try
        {
            (client, row) = await ClientForImageAsync(image);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
            return;
        }
        var dialog = new PushImageDialog(_settings.Registries, image.Reference,
            registry => CheckPushSignInAsync(registry, client, row));
        if (row is not null)
            dialog.Title = UiText.Get("Images_PushFromSession", "Push image from {0}", row.SessionName);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            if (dialog.OpenRegistriesRequested)
            {
                App.Current.MainWindow?.NavigateTo("registries");
            }

            return;
        }

        var reference = dialog.Reference.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        IsBusy = true;
        var allTags = dialog.AllTags;
        var pushReference = allTags ? WslcService.StripTag(reference) : reference;
        StatusMessage = allTags
            ? $"Pushing all tags for {pushReference}… (this can take a while)"
            : $"Pushing {reference}… (this can take a while)";

        // Snapshot the names this image already carries so cleanup only removes an alias we
        // actually added — never a tag the user already had. Comparing the engine's own
        // normalized references catches collisions that raw string equality would miss
        // (e.g. Docker Hub where "nginx:1.0" resolves to "docker.io/library/nginx:1.0").
        var transientAliases = new List<string>();
        var unverifiedTemporaryTag = false;
        try
        {
            await ClientForImageAsync(image);
            await _authRefresher.EnsureFreshForReferenceAsync(pushReference, sessionClient: client,
                verifyTarget: _ => VerifySessionAsync(row));
            await VerifySessionAsync(row);
            var before = await client.ListImagesAsync();
            var normalizedReference = PushDestination.NormalizeLocalReference(reference);
            var existing = before.Where(candidate =>
                string.Equals(PushDestination.NormalizeLocalReference(candidate.Reference), normalizedReference,
                    StringComparison.Ordinal)).ToArray();
            if (existing.Length > 1 || existing.Any(candidate =>
                    !string.Equals(candidate.Id, image.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(UiText.Get("Images_PushTagConflict",
                    "The destination tag already belongs to a different local image. Choose a different tag."));
            await VerifySessionAsync(row);

            // A registry destination is encoded in the image name, and wslc can only push a
            // reference that exists locally. Add the fully-qualified name as an alias for the
            // push; it copies no data — just another pointer to the same image content.
            if (existing.Length == 0)
            {
                var tagResult = await client.TagImageAsync(image.Id, reference);
                if (!tagResult.Success)
                {
                    await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0ef1f1bf6b99", "Push failed"),
                        UiText.Get("Resource_Text_b9f7af9f92c3", "Couldn't tag the image as {0}.\n\n{1}", reference, tagResult.ErrorText));
                    StatusMessage = "Push failed";
                    return;
                }

                unverifiedTemporaryTag = true;
                await VerifySessionAsync(row);
                var after = await client.ListImagesAsync();
                transientAliases = after.Where(candidate =>
                        string.Equals(candidate.Id, image.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(PushDestination.NormalizeLocalReference(candidate.Reference), normalizedReference,
                            StringComparison.Ordinal))
                    .Select(candidate => candidate.Reference).Distinct(StringComparer.Ordinal).ToList();
                if (transientAliases.Count != 1)
                {
                    transientAliases.Clear();
                    throw new InvalidOperationException(UiText.Get("Images_AliasChanged",
                        "The temporary tag now points to a different image."));
                }
                unverifiedTemporaryTag = false;
            }

            await VerifySessionAsync(row);
            await VerifyImageReferenceAsync(client, reference, image.Id);
            await VerifySessionAsync(row);
            var result = await client.PushImageAsync(reference, allTags: allTags);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0ef1f1bf6b99", "Push failed"), result.ErrorText);
                StatusMessage = "Push failed";
            }
            else
            {
                StatusMessage = allTags ? $"Pushed all tags for {pushReference}" : $"Pushed {reference}";
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_3c60d06bc185", "Push complete"),
                    string.IsNullOrWhiteSpace(result.StandardOutput)
                        ? (allTags ? UiText.Get("Resource_Text_d443bd63ddee", "Pushed all tags for {0}.", pushReference) : UiText.Get("Resource_Text_6bbef811d085", "Pushed {0}.", reference))
                        : result.StandardOutput.Trim());
            }

        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
        finally
        {
            if (unverifiedTemporaryTag)
            {
                await _dialogs.ShowLocalizedMessageAsync(
                    UiText.Get("Images_AliasCleanupFailedTitle", "Temporary image tag remains"),
                    UiText.Get("Images_AliasUnverified",
                        "A temporary tag for \"{0}\" may remain in {1}. Review it before retrying.",
                        reference, row?.SessionName ?? CurrentSessionLabel()));
            }
            // Remove only the aliases the tag step introduced, so the local image keeps the
            // exact names it had before the push. Untagging a still-multi-referenced image
            // never deletes its content or the user's original tags.
            foreach (var alias in transientAliases)
            {
                try
                {
                    await VerifySessionAsync(row);
                    var current = await client.ListImagesAsync();
                    var matches = current.Where(candidate => string.Equals(candidate.Reference, alias, StringComparison.Ordinal))
                        .ToArray();
                    if (matches.Length != 1 || !string.Equals(matches[0].Id, image.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException(UiText.Get("Images_AliasChanged",
                            "The temporary tag now points to a different image."));
                    var cleanup = await client.RemoveImageAsync(alias, force: true);
                    if (!cleanup.Success)
                        throw new InvalidOperationException(cleanup.ErrorText);
                }
                catch (Exception ex) when (IsSessionActionFailure(ex))
                {
                    _logger.LogWarning(ex, "Could not remove temporary image alias {Alias}.", alias);
                    await _dialogs.ShowLocalizedMessageAsync(
                        UiText.Get("Images_AliasCleanupFailedTitle", "Temporary image tag remains"),
                        UiText.Get("Images_AliasCleanupFailed",
                            "The temporary tag \"{0}\" could not be removed from {1}. Review it before retrying. {2}",
                            alias, row?.SessionName ?? CurrentSessionLabel(), ex.Message));
                    break;
                }
            }

            IsBusy = false;
        }
    }

    /// <summary>Provides the save images operation to views or collaborating view models.</summary>
    public async Task<bool> CanSaveTogetherAsync(IReadOnlyList<ImageInfo> images)
    {
        if (images.Count == 0 || !await EnsureCurrentInventoryAsync(images))
            return false;
        if (_combined is not null && images.Select(image => _rows[image].Session).Distinct().Skip(1).Any())
        {
            await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_0a4444676244", "Save failed"),
                UiText.Get("Images_SaveOneSession", "Select images from one session at a time to save them in an archive."));
            return false;
        }
        return true;
    }

    /// <summary>Provides the save images operation to views or collaborating view models.</summary>
    public async Task SaveImagesAsync(IReadOnlyList<ImageInfo> images, string outputPath)
    {
        using var selection = _sessionContext.LockSelection();
        var items = images.Where(i => i is not null).ToList();
        if (items.Count == 0)
        {
            return;
        }
        if (!await CanSaveTogetherAsync(items))
            return;

        IsBusy = true;
        StatusMessage = $"Saving {items.Count} image{(items.Count == 1 ? "" : "s")}…";
        try
        {
            // Tagged images save by reference so the archive keeps their names; untagged and
            // intermediate images ("<none>") can only be addressed by ID.
            IWslcService? client = null;
            CombinedSessionRow? owner = null;
            foreach (var image in items)
            {
                var (current, row) = await ClientForImageAsync(image);
                if (owner is not null && row?.Session != owner.Session)
                {
                    await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_0a4444676244", "Save failed"),
                        UiText.Get("Images_SaveOneSession",
                            "Select images from one session at a time to save them in an archive."));
                    return;
                }
                client ??= current;
                owner ??= row;
            }
            var targets = items
                .Select(i => i.Repository is "" or "<none>" || i.Tag is "" or "<none>" ? i.Id : i.Reference)
                .ToList();
            if (!await EnsureCurrentInventoryAsync(items))
                return;
            await VerifySessionAsync(owner);
            var result = await (client ?? _wslc).SaveImagesAsync(targets, outputPath);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_0a4444676244", "Save failed"), result.ErrorText);
                StatusMessage = "Save failed";
                return;
            }

            StatusMessage = $"Saved {items.Count} image{(items.Count == 1 ? "" : "s")} to {Path.GetFileName(outputPath)}. To bring {(items.Count == 1 ? "it" : "them")} back, use Import → Restore saved images….";
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

    /// <summary>Refreshes load image state for the view model.</summary>
    public async Task LoadImageAsync(string inputPath)
    {
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (_combined is not null && target is null)
            return;
        IsBusy = true;
        StatusMessage = "Restoring saved images…";
        try
        {
            if (target is not null) await target.VerifyAsync();
            var result = await (target?.Client ?? _wslc).LoadImageAsync(inputPath);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_7c7a8c1ac2c8", "Couldn't restore images"), result.ErrorText);
                StatusMessage = "Load failed";
                return;
            }

            StatusMessage = "Saved images restored";
            await RefreshAsync();
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Image restore could not complete for the chosen session.");
            await ShowSessionActionErrorAsync(ex);
            StatusMessage = "Load failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Provides the import image operation to views or collaborating view models.</summary>
    public async Task ImportImageAsync(string inputPath)
    {
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (_combined is not null && target is null)
            return;
        var dialog = new SimpleInputDialog(
            target is null ? UiText.Get("Resource_Text_043660976a99", "Create image from exported files")
                : UiText.Get("Images_ImportIntoSession", "Create image in {0}", target.Selection.DisplayName),
            UiText.Get("Resource_Text_47e73c701cb3", "Name for the new image (optional; without one it is listed as <none>)"),
            UiText.Get("Resource_Text_73b3566a1003", "e.g. my-snapshot:latest"))
        {
            Value = string.Empty,
        };
        if (target is not null)
            dialog.SetSession(target.Selection, "Images_ImportIntoSession", "Create image in {0}",
                "Resource_Text_47e73c701cb3", "Name for the new image (optional; without one it is listed as <none>)",
                "Resource_Text_73b3566a1003", "e.g. my-snapshot:latest");
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var image = dialog.Value.Trim();
        IsBusy = true;
        StatusMessage = string.IsNullOrEmpty(image) ? "Creating image from exported files…" : $"Creating {image}…";
        try
        {
            if (target is not null) await target.VerifyAsync();
            var result = await (target?.Client ?? _wslc).ImportImageAsync(inputPath, string.IsNullOrEmpty(image) ? null : image);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_6d490981d8f3", "Couldn't create the image"), result.ErrorText);
                StatusMessage = "Import failed";
                return;
            }

            StatusMessage = "Image created from exported files";
            await RefreshAsync();
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Image import could not complete for the chosen session.");
            await ShowSessionActionErrorAsync(ex);
            StatusMessage = "Import failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Command handler for inspect actions triggered from the view.</summary>
    [RelayCommand]
    private async Task InspectAsync(ImageInfo? image)
    {
        using var selection = _sessionContext.LockSelection();
        image ??= Selected;
        if (image is null)
        {
            return;
        }
        if (!await EnsureCurrentInventoryAsync([image]))
            return;

        IsBusy = true;
        try
        {
            CommandResult result;
            if (_combined is not null)
            {
                if (_sessionActions is null || !_rows.TryGetValue(image, out var row))
                    throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                        "The session, engine, or displayed resources changed. Refresh before taking this action."));
                result = await _sessionActions.InspectAsync(row.Session, WslcResourceActionTarget.ResourceKind.Image, image.Id);
            }
            else
                result = await _wslc.InspectImageAsync(image.Id);
            var title = _combined is null ? UiText.Get("Resource_Text_f306c4f01fc3", "Inspect · {0}", image.Reference)
                : UiText.Get("CombinedSession_InspectTitle", "Inspect {0} · {1}", image.Reference, image.SessionName);
            await _dialogs.ShowLocalizedMessageAsync(title,
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

    /// <summary>Command handler for build actions triggered from the view.</summary>
    [RelayCommand]
    private async Task BuildAsync()
    {
        using var selection = _sessionContext.LockSelection();
        RefreshPolicyState();
        if (!CanBuild)
        {
            await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_be5db383a1cb", "Build blocked by policy"), BuildPolicyMessage);
            return;
        }

        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger);
        if (_combined is not null && target is null)
            return;
        var dialog = new BuildImageDialog(_settings.Registries);
        if (target is not null)
            dialog.SetSession(target.Selection);
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Building {dialog.ImageTag}…";
        try
        {
            if (target is not null) await target.VerifyAsync();
            var result = await (target?.Client ?? _wslc).BuildImageAsync(dialog.ContextPath, dialog.ImageTag, dialog.Dockerfile);
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_2f8e461f4b9f", "Build failed"), result.ErrorText);
                StatusMessage = "Build failed";
                _notifications.NotifyImageBuild(dialog.ImageTag, success: false, result.ErrorText);
                _activity.RecordImageBuild(dialog.ImageTag, success: false, result.ErrorText, ActivityOwner(target));
            }
            else
            {
                _notifications.NotifyImageBuild(dialog.ImageTag, success: true);
                _activity.RecordImageBuild(dialog.ImageTag, success: true, owner: ActivityOwner(target));
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_9e1ba089f927", "Build complete"), result.StandardOutput);
                await RefreshAsync();
            }
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Image build could not complete for the chosen session.");
            await ShowSessionActionErrorAsync(ex);
            StatusMessage = "Build failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sign-in check for the Push dialog. Docker Hub is read from the engine's stored credential
    /// (an anonymous probe can't tell the difference); other registries are probed, and Azure
    /// registries are silently re-authenticated first so an expired token doesn't read as signed out.
    /// </summary>
    private async Task<(RegistryLoginState State, string? User)> CheckPushSignInAsync(RegistryEntry registry,
        IWslcService sessionClient, CombinedSessionRow? row)
    {
        if (registry.IsDefault)
        {
            if (_credentials.IsLoggedIn(registry.LoginServer, out var user))
                return (RegistryLoginState.LoggedIn, user);

            // Only the "wincred" store (wslc's default) can be read here. With "file", a missing
            // Windows credential proves nothing, so report "couldn't confirm" rather than block the push.
            return await UsesWindowsCredentialStoreAsync()
                ? (RegistryLoginState.Anonymous, null)
                : (RegistryLoginState.Unknown, null);
        }

        await VerifySessionAsync(row);
        var state = await sessionClient.ProbeRegistryLoginAsync(registry.Host, "wslcd-login-probe");
        if (state != RegistryLoginState.LoggedIn && registry.IsAzure &&
            await _authRefresher.RefreshAsync(registry, sessionClient: sessionClient,
                verifyTarget: _ => VerifySessionAsync(row)))
        {
            state = RegistryLoginState.LoggedIn;
        }

        if (row is null || WslcSessionActionGuard.IsManaged(_sessionContext, row.Session,
                WslcSessionActionGuard.ResolveDefaultSessionName))
            registry.LoginState = state;
        return (state, registry.Username);
    }

    /// <summary>
    /// True when wslc keeps registry logins in Windows Credential Manager, which is the only store
    /// the app can read. An unreadable settings file counts as the default (wincred).
    /// </summary>
    private async Task<bool> UsesWindowsCredentialStoreAsync()
    {
        try
        {
            var store = (await _wslcSettings.ReadAsync()).CredentialStore;
            return string.IsNullOrWhiteSpace(store) || store.Trim().Equals("wincred", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // The default is wincred, so an unreadable settings file keeps the normal sign-in check.
            System.Diagnostics.Debug.WriteLine($"Could not read the wslc credential store setting: {ex.Message}");
            return true;
        }
    }

    /// <summary>Refreshes policy state for the view model.</summary>
    private void RefreshPolicyState()
    {
        var message = WslRegistryPolicyGuard.ValidateBuild(_policy.GetPolicy());
        CanBuild = message is null;
        BuildPolicyMessage = message ?? "Build an image from a Dockerfile";
    }

    /// <summary>Command handler for prune actions triggered from the view.</summary>
    [RelayCommand]
    private async Task PruneAsync()
    {
        using var selection = _sessionContext.LockSelection();
        var target = _combined is null ? null : await SessionOperationTarget.ChooseAsync(_sessionSelection, _dialogs, _logger, startDefault: false);
        if (_combined is not null && target is null)
            return;
        var ok = await _dialogs.ShowLocalizedConfirmAsync(() => (
            UiText.Get("Resource_Text_58d3b07833b2", "Prune images"),
            target is null ? UiText.Get("Resource_Text_c2370bb291a5", "Remove all dangling (unused) images?")
                : UiText.Get("Images_PruneInSession", "Remove all dangling (unused) images in {0}?", target.DisplayName),
            UiText.Get("Resource_Text_2f24ebaee0aa", "Prune"),
            UiText.Get("Workload_Text_Cancel_19766e", "Cancel")));
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (target is not null) await target.VerifyAsync();
            var result = await (target?.Client ?? _wslc).PruneImagesAsync();
            if (!result.Success)
            {
                await _dialogs.ShowMessageAsync(UiText.Get("Resource_Text_4133934aa616", "Prune failed"), result.ErrorText);
            }
            else
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            _logger.LogWarning(ex, "Image prune could not complete for the chosen session.");
            await ShowSessionActionErrorAsync(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureCurrentInventoryAsync(IReadOnlyList<ImageInfo> items)
    {
        if (_combined is not null)
        {
            if (_combined.IsBusy)
            {
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
                    UiText.Get("CombinedSession_RefreshBusy", "Another session refresh is already running."));
                return false;
            }
            if (items.All(item => Images.Contains(item) && _rows.TryGetValue(item, out var row) &&
                _combined.IsCurrentRow(row)))
                return true;
        }
        else
        {
            if (_inventorySelection is { } snapshot && snapshot == _sessionContext.CaptureSnapshot() &&
                items.All(Images.Contains))
                return true;
        }

        await _dialogs.ShowLocalizedMessageAsync(
            UiText.Get("SessionInventory_RefreshRequiredTitle", "Refresh the inventory"),
            UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        return false;
    }

    private async Task RemoveCombinedAsync(ImageInfo image)
    {
        if (_combined is null || _sessionActions is null || !_rows.TryGetValue(image, out var row))
            throw new InvalidOperationException("The selected image has no session target.");
        IsBusy = true;
        try
        {
            var target = WslcResourceActionTarget.Capture(row.Session, image);
            if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("CombinedSession_RemoveResourceTitle", "Remove resource"),
                    UiText.Get("CombinedSession_RemoveImageConfirmation",
                        "Remove image \"{0}\" from session \"{1}\"? Other tags for the same image may also be affected.",
                        target.Name, target.Session.Name),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;
            if (!await EnsureCurrentInventoryAsync([image]))
                return;
            _combined.InvalidateSession(row.Session);
            var result = await _sessionActions.RemoveAsync(target);
            if (!result.Success)
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_debe01de259b", "Remove failed"), result.ErrorText);
            await _combined.RefreshSessionAsync(row.Session);
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
        finally { IsBusy = false; }
    }

    private async Task BulkRemoveCombinedAsync(IReadOnlyList<ImageInfo> images)
    {
        if (_combined is null || _sessionActions is null)
            throw new InvalidOperationException("The session actions are not configured.");
        IsBusy = true;
        try
        {
            var targets = new List<WslcResourceActionTarget>();
            foreach (var image in images)
            {
                if (!_rows.TryGetValue(image, out var row))
                    throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                        "The session, engine, or displayed resources changed. Refresh before taking this action."));
                targets.Add(WslcResourceActionTarget.Capture(row.Session, image));
            }
            if (!await _dialogs.ShowLocalizedConfirmAsync(UiText.Get("Resource_Text_aea8cc2d34a5", "Remove images"),
                    UiText.Get("Images_CombinedRemoveConfirmation",
                        "Remove {0} image(s) from their listed sessions? Some removals may fail independently.\n\n{1}",
                        targets.Count, BulkNames(targets.Select(target => $"{target.Session.Name}: {target.Name}"))),
                    UiText.Get("Resource_Text_e963907dac5c", "Remove"), UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
                return;
            if (!await EnsureCurrentInventoryAsync(images))
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
                catch (Exception ex) when (IsSessionActionFailure(ex))
                {
                    _logger.LogWarning(ex, "Could not remove image {Image} in session {Session}.", target.Name, target.Session.Name);
                    failures.Add($"{target.Session.Name}: {target.Name}: {ex.Message}");
                    if (ex is OperationCanceledException)
                    {
                        failures.Add(UiText.Get("ResourceSession_BulkInterrupted",
                            "The current action's outcome may be unknown; {0} later item(s) were not attempted.",
                            targets.Count - index - 1));
                        break;
                    }
                }
            }
            IsSelectionMode = false;
            await RefreshCombinedAsync();
            if (failures.Count > 0)
                await _dialogs.ShowLocalizedMessageAsync(UiText.Get("Resource_Text_7800478a75f7", "Some images were not removed"),
                    string.Join(Environment.NewLine, failures));
        }
        catch (Exception ex) when (IsSessionActionFailure(ex))
        {
            await ShowSessionActionErrorAsync(ex);
        }
        finally { IsBusy = false; }
    }

    private static bool IsSessionActionFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or ArgumentException;

    private string CurrentSessionLabel() => _sessionContext.Capture().Name ??
        UiText.Get("WslEngine_Session_Default", "Default CLI session");

    private Task ShowSessionActionErrorAsync(Exception ex)
    {
        _logger.LogWarning(ex, "A session-qualified image action failed.");
        return _dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"),
            ex is OperationCanceledException
                ? UiText.Get("CombinedSession_ActionOutcomeUnknown",
                    "The action was cancelled or timed out. Its outcome may be unknown; refresh before retrying.")
                : ex.Message);
    }

    private static AutomationSessionOwner? ActivityOwner(SessionOperationTarget? target) =>
        target is null ? null : AutomationSessionOwner.Capture(target.Selection.ExecutablePath, target.Selection.Target);

    private AutomationSessionOwner? ActivityOwner(CombinedSessionRow? row)
    {
        if (row is null || _combined is null || string.IsNullOrWhiteSpace(_combined.CurrentExecutablePath))
            return null;
        var target = row.Session.Name.StartsWith("wslc-cli-", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Session.Name, WslcSessionActionGuard.ResolveDefaultSessionName(), StringComparison.Ordinal)
            ? WslcSessionTarget.Default : new WslcSessionTarget(row.Session.Name);
        return AutomationSessionOwner.Capture(_combined.CurrentExecutablePath, target);
    }

    private async Task<(IWslcService Client, CombinedSessionRow? Row)> ClientForImageAsync(ImageInfo image)
    {
        if (_combined is null)
            return (_wslc, null);
        if (_sessionClient is null || !_rows.TryGetValue(image, out var row) || !_combined.IsCurrentRow(row))
            throw new InvalidOperationException(UiText.Get("SessionInventory_RefreshRequired",
                "The session, engine, or displayed resources changed. Refresh before taking this action."));
        var executable = _combined.CurrentExecutablePath;
        var managed = WslcSessionActionGuard.IsManaged(_sessionContext, row.Session,
            WslcSessionActionGuard.ResolveDefaultSessionName);
        var client = _sessionClient(row.Session, executable, managed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
            row.Session, executable, timeout.Token);
        var images = await client.ListImagesAsync(timeout.Token);
        if (images.Count(current => string.Equals(current.Id, image.Id, StringComparison.Ordinal) &&
            string.Equals(current.Reference, image.Reference, StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException(UiText.Get("CombinedSession_ResourceChanged",
                "The resource changed or is no longer available; refresh before taking action."));
        await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
            row.Session, executable, timeout.Token);
        return (client, row);
    }

    private async Task VerifySessionAsync(CombinedSessionRow? row)
    {
        if (row is not null && _combined is not null)
        {
            var executable = _combined.CurrentExecutablePath;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await WslcSessionActionGuard.VerifyAsync(_wslc, () => _combined.CurrentExecutablePath,
                row.Session, executable, timeout.Token);
        }
    }

    private static async Task VerifyImageReferenceAsync(IWslcService client, string reference, string expectedId)
    {
        var result = await client.InspectImageAsync(reference);
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorText);
        var records = WslcJsonParser.ParseList<System.Text.Json.JsonElement>(result.StandardOutput);
        var observed = records.Count == 1
            ? ContainerInfoJsonConverter.ReadString(records[0], "Id") : string.Empty;
        static string Normalize(string id) => id.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? id[7..] : id;
        if (ContainerIdentity.ResolveId([Normalize(expectedId)], Normalize(observed)) is null)
            throw new InvalidOperationException(UiText.Get("CombinedSession_ResourceChanged",
                "The resource changed or is no longer available; refresh before taking action."));
    }

    /// <summary>Refreshes display projections after a UI language change.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(string.Empty);
        using (Grid.DeferRefresh())
            foreach (var item in Images) item.RefreshLocalizedText();
    }
}
