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
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.ViewModels;

/// <summary>Separate settings and lifecycle commands; constructing this VM performs no I/O.</summary>
public partial class FoundryLocalSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IFoundryLocalRuntimeService _runtime;
    private readonly IAiCapabilityService _capabilities;
    private readonly ILogger _logger;
    private readonly FoundryLocalSetupService _setup;
    private readonly FoundryLocalInitialSetupService? _initialSetup;
    private FoundryLocalConnectionPlan? _connectionPlan;
    private FoundryLocalInventory? _lastInventory;
    private string? _lastInventoryDisplay;
    private int _configurationRevision;
    private bool _confirmingConnection;
    private bool _applyingReadyConfiguration;
    private CancellationTokenSource? _runtimeSetupCancellation;

    /// <summary>Endpoint URL for the standalone Foundry Local server.</summary>
    [ObservableProperty] private string _endpoint;
    /// <summary>Exact model id to use when this provider is selected.</summary>
    [ObservableProperty] private string _model;
    /// <summary>Discovery and catalog details shown to help users verify the runtime manually.</summary>
    [ObservableProperty] private string _inventoryText = UiText.Get("Common_Text0150", "Not refreshed. Enter the actual runtime URL and model ID. No default port or model is assumed.");
    /// <summary>Status for metadata, load and unload operations against the runtime.</summary>
    [ObservableProperty] private string _status = UiText.Get("Common_Text0151", "No runtime operation requested.");
    /// <summary>Status for install, staging and discovery setup workflows.</summary>
    [ObservableProperty] private string _setupStatus = UiText.Get("Common_Text0152", "Discovery is read-only and runs only when requested.");
    /// <summary>True when discovery found an endpoint the user can explicitly accept.</summary>
    [ObservableProperty] private bool _canUseDiscoveredEndpoint;
    /// <summary>True while the runtime installation workflow is active.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallRuntime))]
    [NotifyPropertyChangedFor(nameof(CanStageModelFiles))]
    [NotifyPropertyChangedFor(nameof(CanPrepareInitialModel))]
    [NotifyPropertyChangedFor(nameof(IsPreparingAnything))]
    private bool _isInstallingRuntime;
    /// <summary>Whether preparing model files for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallRuntime))]
    [NotifyPropertyChangedFor(nameof(CanStageModelFiles))]
    [NotifyPropertyChangedFor(nameof(CanPrepareInitialModel))]
    [NotifyPropertyChangedFor(nameof(IsPreparingAnything))]
    private bool _isPreparingModelFiles;
    /// <summary>Whether preparing initial model for view binding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallRuntime))]
    [NotifyPropertyChangedFor(nameof(CanStageModelFiles))]
    [NotifyPropertyChangedFor(nameof(CanPrepareInitialModel))]
    [NotifyPropertyChangedFor(nameof(IsPreparingAnything))]
    private bool _isPreparingInitialModel;

    /// <summary>Bindable state for acquisition guidance used by the view.</summary>
    public string AcquisitionGuidance => UiText.TranslateLines(FoundryLocalStandaloneRuntimeService.AcquisitionGuidance);
    /// <summary>Bindable state for memory policy used by the view.</summary>
    public string MemoryPolicy => UiText.TranslateLines(FoundryLocalStandaloneRuntimeService.MemoryPolicy);
    /// <summary>Bindable state for installation guidance used by the view.</summary>
    public string InstallationGuidance => UiText.TranslateLines(_setup.AvailabilityGuidance);
    /// <summary>Whether preparing anything for view binding.</summary>
    public bool IsPreparingAnything => IsInstallingRuntime || IsPreparingModelFiles || IsPreparingInitialModel;
    /// <summary>Whether the user can install runtime from the view.</summary>
    public bool CanInstallRuntime => _setup.CanInstall && !IsPreparingAnything;
    /// <summary>Whether the user can stage model files from the view.</summary>
    public bool CanStageModelFiles => _setup.CanStageModelFiles && !IsPreparingAnything;
    /// <summary>Whether the user can prepare initial model from the view.</summary>
    public bool CanPrepareInitialModel => _initialSetup is not null && !IsPreparingAnything;
    /// <summary>Bindable state for setup cache location used by the view.</summary>
    public string SetupCacheLocation => UiText.Get("Common_Text0153", "Setup cache: ") + (_setup.CacheLocation == "Unavailable" ? UiText.Get("Common_SetupUnavailable", "Unavailable") : _setup.CacheLocation);
    /// <summary>Bindable state for model staging location used by the view.</summary>
    public string ModelStagingLocation => UiText.Get("Common_Text0154", "Model-file staging (not the Foundry runtime cache): ") + (_setup.ModelCacheLocation == "Unavailable" ? UiText.Get("Common_SetupUnavailable", "Unavailable") : _setup.ModelCacheLocation);

    /// <summary>Whether the standalone runtime is installed on this PC, for setup-versus-remove affordances.</summary>
    public bool IsRuntimeInstalled => _setup.IsRuntimeInstalled;

    /// <summary>Refreshes installed state for the view model.</summary>
    public void RefreshInstalledState() => OnPropertyChanged(nameof(IsRuntimeInstalled));

    /// <summary>Removes only the Foundry Local package after explicit confirmation.</summary>
    public async Task<string> UninstallRuntimeAsync(CancellationToken ct = default)
    {
        IsInstallingRuntime = true;
        try
        {
            var result = await _setup.UninstallRuntimeAsync(ct);
            SetupStatus = result.Guidance;
            return result.Guidance;
        }
        finally
        {
            IsInstallingRuntime = false;
            RefreshInstalledState();
        }
    }

    /// <summary>Creates the FoundryLocalSettings view model and stores its injected services.</summary>
    public FoundryLocalSettingsViewModel(ISettingsService settings,
        IFoundryLocalRuntimeService runtime, IAiCapabilityService capabilities,
        ILogger<FoundryLocalSettingsViewModel> logger, FoundryLocalSetupService? setup = null,
        FoundryLocalInitialSetupService? initialSetup = null)
    {
        _settings = settings;
        _runtime = runtime;
        _capabilities = capabilities;
        _logger = AiTextSanitizer.WrapLogger(logger);
        _setup = setup ?? new(new FoundryLocalCli(), runtime);
        _initialSetup = initialSetup;
        _endpoint = settings.AiFoundryLocalEndpoint;
        _model = settings.AiFoundryLocalModel;
        UiText.LanguageChanged += (_, _) => RefreshLocalizedText();
    }

    /// <summary>Handles endpoint changed changes and updates related view-model state.</summary>
    partial void OnEndpointChanged(string value)
    {
        _settings.AiFoundryLocalEndpoint = value;
        if (!_applyingReadyConfiguration) ConfigurationChanged();
    }

    /// <summary>Handles model changed changes and updates related view-model state.</summary>
    partial void OnModelChanged(string value)
    {
        _settings.AiFoundryLocalModel = value;
        if (!_applyingReadyConfiguration) ConfigurationChanged();
    }

    /// <summary>Refreshes only displayed text, preserving the endpoint, model, and active workflows.</summary>
    private void RefreshLocalizedText()
    {
        InventoryText = _lastInventory is not null && InventoryText == _lastInventoryDisplay
            ? _lastInventoryDisplay = FormatInventory(_lastInventory) : UiText.TranslateLines(InventoryText);
        Status = UiText.TranslateLines(Status);
        SetupStatus = UiText.TranslateLines(SetupStatus);
        foreach (var property in new[] { nameof(AcquisitionGuidance), nameof(MemoryPolicy),
            nameof(InstallationGuidance), nameof(SetupCacheLocation), nameof(ModelStagingLocation) })
            OnPropertyChanged(property);
    }

    partial void OnInventoryTextChanged(string value)
    {
        var translated = UiText.TranslateLines(value);
        if (translated != value) InventoryText = translated;
    }

    partial void OnStatusChanged(string value)
    {
        var translated = UiText.TranslateLines(value);
        if (translated != value) Status = translated;
    }

    partial void OnSetupStatusChanged(string value)
    {
        var translated = UiText.TranslateLines(value);
        if (translated != value) SetupStatus = translated;
    }

    /// <summary>Helper for the configuration changed workflow in this view model.</summary>
    private void ConfigurationChanged()
    {
        InvalidateDiscovery();
        _lastInventory = null;
        _lastInventoryDisplay = null;
        RunOperationCommand.Cancel();
        _capabilities.Invalidate();
        _settings.Save();
        InventoryText = UiText.Get("Common_Text0155", "Configuration changed. Refresh metadata; cached observations were cleared.");
        Status = UiText.Get("Common_Text0156", "No operation requested for this configuration.");
    }

    /// <summary>Handles provider changed changes and updates related view-model state.</summary>
    public void OnProviderChanged()
    {
        InvalidateDiscovery();
        _lastInventory = null;
        _lastInventoryDisplay = null;
        RunOperationCommand.Cancel();
        _capabilities.Invalidate();
        InventoryText = UiText.Get("Common_Text0157", "Provider changed. Refresh metadata after selecting Foundry Local.");
        Status = UiText.Get("Common_Text0158", "Pending requests cancelled. Refresh observed state; completed actions are not rolled back.");
    }

    /// <summary>Helper for the invalidate discovery workflow in this view model.</summary>
    private void InvalidateDiscovery()
    {
        _configurationRevision++;
        _runtimeSetupCancellation?.Cancel();
        DiscoverCommand.Cancel();
        _connectionPlan = null;
        CanUseDiscoveredEndpoint = false;
        SetupStatus = IsPreparingModelFiles
            ? UiText.Get("Common_Text0159", "Configuration changed; model-file preparation cancelled. Completed and partial staging data are retained.")
            : IsInstallingRuntime
            ? UiText.Get("Common_Text0160", "Configuration changed; runtime setup cancelled. Windows deployment may still complete. Inspect installed packages before retrying.")
            : UiText.Get("Common_Text0161", "Configuration changed. Discover again before connecting; no runtime operation requested for these settings.");
    }

    /// <summary>Provides the install runtime operation to views or collaborating view models.</summary>
    public Task InstallRuntimeAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunSetupAsync(false, confirm);
    /// <summary>Provides the stage model files operation to views or collaborating view models.</summary>
    public Task StageModelFilesAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunSetupAsync(true, confirm);
    /// <summary>Provides the prepare initial model operation to views or collaborating view models.</summary>
    public Task PrepareInitialModelAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunInitialAsync(false, confirm);
    /// <summary>Provides the stop server operation to views or collaborating view models.</summary>
    public Task StopServerAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunInitialAsync(true, confirm);

    /// <summary>Helper for the run initial workflow in this view model.</summary>
    private async Task RunInitialAsync(bool stop, Func<string, CancellationToken, Task<bool>> confirm)
    {
        if (!CanPrepareInitialModel || _initialSetup is null || DiscoverCommand.IsRunning
            || RunOperationCommand.IsRunning || _confirmingConnection) return;
        var original = AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
        var revision = _configurationRevision;
        bool Current() => revision == _configurationRevision && IsCurrent(original)
            && (stop || _settings.AiFeaturesEnabled);
        if (!Current()) return;
        using var cancellation = new CancellationTokenSource();
        _runtimeSetupCancellation = cancellation;
        IsPreparingInitialModel = true;
        _capabilities.Invalidate();
        var inFlight = true;
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (inFlight && Current() && !cancellation.IsCancellationRequested) SetupStatus = text;
            });
            var result = stop
                ? await _initialSetup.StopAsync(original, confirm, Current, cancellation.Token)
                : await _initialSetup.PrepareAsync(original, confirm, Current, progress, cancellation.Token);
            inFlight = false;
            if (!Current()) return;
            if (result.Success && result.Configuration is { } ready)
            {
                _settings.AiFoundryLocalEndpoint = ready.Endpoint;
                _settings.AiFoundryLocalModel = ready.Model;
                _applyingReadyConfiguration = true;
                try
                {
                    Endpoint = ready.Endpoint;
                    Model = ready.Model;
                }
                finally { _applyingReadyConfiguration = false; }
                InvalidateDiscovery();
                _settings.Save();
                InventoryText = UiText.Get("Common_Text0162", "Initial CPU model is loaded; run independent capability checks before assistant use.");
            }
            SetupStatus = result.Guidance;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Foundry initial-model operation cancelled.");
            if (Current()) SetupStatus = UiText.Get("Common_Text0163", "Cancelled. Runtime/model state may have changed; files and packages retained.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry initial-model operation failed.");
            if (Current()) SetupStatus = UiText.Get("Common_Text0164", "Preparation failed: ") + AiTextSanitizer.Sanitize(ex.Message)
                + UiText.Get("Common_Text0165", " No automatic rollback or retry.");
        }
        finally
        {
            inFlight = false;
            _runtimeSetupCancellation = null;
            IsPreparingInitialModel = false;
            _capabilities.Invalidate();
        }
    }

    /// <summary>Helper for the run setup workflow in this view model.</summary>
    private async Task RunSetupAsync(bool modelFiles, Func<string, CancellationToken, Task<bool>> confirm)
    {
        if (!(modelFiles ? CanStageModelFiles : CanInstallRuntime) || DiscoverCommand.IsRunning || _confirmingConnection) return;
        var original = AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
        var revision = _configurationRevision;
        if (!IsCurrent(original)) return;
        using var cancellation = new CancellationTokenSource();
        _runtimeSetupCancellation = cancellation;
        IsInstallingRuntime = !modelFiles;
        IsPreparingModelFiles = modelFiles;
        _connectionPlan = null;
        CanUseDiscoveredEndpoint = false;
        var inFlight = true;
        bool Current() => revision == _configurationRevision && IsCurrent(original);
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (inFlight && Current() && !cancellation.IsCancellationRequested)
                    SetupStatus = text;
            });
            SetupStatus = modelFiles
                ? UiText.Get("Common_Text0166", "Preparing pinned model-file download; no network until you confirm…")
                : UiText.Get("Common_Text0167", "Preparing runtime-only installation; no downloads or registration until you confirm…");
            string guidance;
            if (modelFiles)
            {
                var result = await _setup.StageModelFilesAsync(original, confirm, Current, progress, cancellation.Token);
                guidance = result.Guidance;
            }
            else
            {
                var result = await _setup.InstallRuntimeAsync(original, confirm, Current, progress, cancellation.Token);
                guidance = result.Guidance;
            }
            inFlight = false;
            if (Current()) SetupStatus = guidance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Foundry preparation failure.");
            if (Current())
                SetupStatus = UiText.Get("Common_Text0168", "Unexpected preparation failure. No automatic retry or cleanup. Inspect retained setup cache and any requested Windows registration before retrying.");
        }
        finally
        {
            inFlight = false;
            _runtimeSetupCancellation = null;
            IsInstallingRuntime = false;
            IsPreparingModelFiles = false;
        }
    }

    /// <summary>Command handler for cancel runtime setup actions triggered from the view.</summary>
    [RelayCommand]
    private void CancelRuntimeSetup() => _runtimeSetupCancellation?.Cancel();

    /// <summary>Command handler for discover actions triggered from the view.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DiscoverAsync(CancellationToken ct)
    {
        if (IsPreparingAnything) return;
        var original = AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
        var revision = _configurationRevision;
        var reading = true;
        _connectionPlan = null;
        CanUseDiscoveredEndpoint = false;
        try
        {
            if (!IsCurrent(original)) return;
            SetupStatus = UiText.Get("Common_Text0169", "Discovering the existing standalone CLI/server; no installation or start requested…");
            var progress = new Progress<string>(text =>
            {
                if (reading && revision == _configurationRevision && IsCurrent(original) && !ct.IsCancellationRequested)
                    SetupStatus = text;
            });
            var plan = await _setup.DiscoverAsync(original, progress, ct);
            reading = false;
            ct.ThrowIfCancellationRequested();
            if (revision != _configurationRevision || !IsCurrent(original)) return;
            _connectionPlan = plan;
            CanUseDiscoveredEndpoint = plan.Inventory.Selected is not null;
            SetupStatus = plan.Confirmation + (CanUseDiscoveredEndpoint ? UiText.Get("Common_Text0170", "\nReview and confirm to change the endpoint setting.")
                : UiText.Get("Common_Text0171", "\nEnter an exact model ID advertised by this server, then discover again. No model was selected automatically."));
            InventoryText = AiTextSanitizer.Sanitize(UiText.Get("Common_Text0172", "Discovered catalog IDs (up to 100): ") +
                string.Join(", ", plan.Inventory.Catalog.Take(100).Select(model => model.Id)));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Foundry Local discovery cancelled.");
            if (revision == _configurationRevision && IsCurrent(original))
                SetupStatus = UiText.Get("Common_Text0173", "Discovery cancelled or timed out. Settings and external runtime were not changed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry Local discovery failed without fallback.");
            if (revision == _configurationRevision && IsCurrent(original))
                SetupStatus = UiText.Get("Common_Text0174", "Discovery failed: ") + AiTextSanitizer.Sanitize(ex.Message) +
                    UiText.Get("Common_Text0175", "\nSettings and runtime were not changed. Enter the actual endpoint manually or inspect the standalone installation.");
        }
        finally
        {
            reading = false;
        }
    }

    /// <summary>Provides the use discovered endpoint operation to views or collaborating view models.</summary>
    public async Task UseDiscoveredEndpointAsync(Func<string, Task<bool>> confirm)
    {
        if (_confirmingConnection || _connectionPlan is not { } plan || !CanUseDiscoveredEndpoint) return;
        var revision = _configurationRevision;
        _confirmingConnection = true;
        try
        {
            FoundryLocalRuntimeService.Validate(plan.Inventory.Configuration);
            if (!IsCurrent(plan.Original) || !await confirm(plan.Confirmation)) return;
            // The dialog may outlive edits, provider switches (including away and back), or a
            // new discovery. Approval applies only to the immutable observation it displayed.
            if (revision != _configurationRevision || !IsCurrent(plan.Original)
                || !ReferenceEquals(plan, _connectionPlan)) return;
            Endpoint = plan.Discovery.Endpoint;
            _connectionPlan = null;
            CanUseDiscoveredEndpoint = false;
            SetupStatus = UiText.Get("Common_Text0176", "Confirmed endpoint saved. Model selection preserved; no runtime or model was changed. Refresh metadata and test capabilities before inference.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry Local connection confirmation failed.");
            if (revision == _configurationRevision && IsCurrent(plan.Original))
                SetupStatus = UiText.Get("Common_Text0177", "Connection was not confirmed. Inspect settings and discover again.");
        }
        finally
        {
            _confirmingConnection = false;
        }
    }

    /// <summary>Command handler for run operation actions triggered from the view.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunOperationAsync(string operation, CancellationToken ct)
    {
        var configuration = AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(configuration))
                throw new OperationCanceledException("Foundry Local is no longer the selected provider.");
            Status = operation == "refresh" ? UiText.Get("Common_Text0178", "Reading metadata only…") : UiText.Get("Common_Text0179", "Requesting model memory change…");
            if (operation is "load" or "unload")
            {
                var result = operation == "load"
                    ? await _runtime.LoadAsync(configuration, ct)
                    : await _runtime.UnloadAsync(configuration, ct);
                if (!IsCurrent(configuration)) return;
                Status = result.Guidance;
                if (operation == "load" || !result.IsConfirmed) return;
            }
            else if (operation != "refresh") throw new ArgumentException("Unknown Foundry Local operation.");
            var inventory = await _runtime.ReadInventoryAsync(configuration, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(configuration)) return;
            _lastInventory = inventory;
            InventoryText = _lastInventoryDisplay = FormatInventory(inventory);
            if (operation == "refresh") Status = UiText.Get("Common_Text0187", "Metadata refreshed. No load, download or inference was requested.");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Foundry Local operation cancelled or invalidated.");
            if (IsCurrent(configuration))
                Status = UiText.Get("Common_Text0188", "Request cancelled or timed out. Memory/model state is unknown until refreshed; completed actions are not rolled back.");
        }
        catch (Exception ex) when (ex is HttpRequestException or AiProviderException or JsonException
            or InvalidDataException or ArgumentException or TimeoutException)
        {
            _logger.LogDebug(ex, "Expected Foundry Local operation failure.");
            ShowFailure(ex, operation, configuration, unexpected: false);
        }
        catch (Exception ex)
        {
            // This async UI-command boundary must not crash WinUI. Unexpected failures are
            // logged at Error through the redacting wrapper and visibly classified, not hidden.
            _logger.LogError(ex, "Unexpected Foundry Local operation failure.");
            ShowFailure(ex, operation, configuration, unexpected: true);
        }
    }

    /// <summary>Reformats observed metadata without another network request or runtime operation.</summary>
    private static string FormatInventory(FoundryLocalInventory inventory)
    {
        var rows = inventory.Catalog.Take(100).Select(m =>
            UiText.Get("Common_Text0180", "{0} | cached: {1} | loaded: {2}\n", m.Id, (inventory.CacheStateKnown ? UiText.Translate(inventory.Cached.Contains(m.Id).ToString()) : UiText.Get("Common_Text0191", "unknown")), (inventory.LoadStateKnown ? UiText.Translate(inventory.Loaded.Contains(m.Id).ToString()) : UiText.Get("Common_Text0191", "unknown"))) +
            UiText.Get("Common_Text0181", "  version: {0}; size MB: {1}; license: {2}\n", Known(m.Version), m.FileSizeMb?.ToString() ?? UiText.Get("Common_Text0191", "unknown"), Known(m.License)) +
            UiText.Get("Common_Text0182", "  license information: {0}; task: {1}; format: {2}\n", Known(m.LicenseDescription), Known(m.Task), Known(m.ModelType)) +
            UiText.Get("Common_Text0183", "  hardware target: {0}; EP: {1} (advertised, not validated); tools advertised: {2}", Known(m.DeviceType), Known(m.ExecutionProvider), m.SupportsToolCalling is { } supportsTools ? UiText.Translate(supportsTools.ToString()) : UiText.Get("Common_Text0191", "unknown")));
        return AiTextSanitizer.Sanitize(
            UiText.Get("Common_Text0184", "Catalog: {0}; cached: {1}; loaded: {2}.\n", inventory.Catalog.Count, (inventory.CacheStateKnown ? inventory.Cached.Count.ToString() : UiText.Get("Common_Text0191", "unknown")), (inventory.LoadStateKnown ? inventory.Loaded.Count.ToString() : UiText.Get("Common_Text0191", "unknown"))) +
            UiText.Get("Common_Text0185", "Cached IDs: {0}\nLoaded IDs: {1}\n", (inventory.CacheStateKnown ? string.Join(", ", inventory.Cached.Take(100)) : UiText.Get("Common_Text0191", "unknown")), (inventory.LoadStateKnown ? string.Join(", ", inventory.Loaded.Take(100)) : UiText.Get("Common_Text0191", "unknown"))) +
            UiText.Get("Common_Text0186", "Showing up to 100 catalog entries. Registered external entries are not eligible for local inference.\n") +
            string.Join("\n", rows), AiTextSanitizer.DiagnosticLimit);
    }

    /// <summary>Helper for the show failure workflow in this view model.</summary>
    private void ShowFailure(Exception error, string operation, AiChatConfiguration configuration, bool unexpected)
    {
        if (!IsCurrent(configuration)) return; // Never publish stale feedback into another provider's UI.
        var feedback = AiErrorClassifier.Classify(error, AiErrorContext.For(AiProviderKind.FoundryLocal,
            operation, configuration.Endpoint, configuration.Model));
        Status = AiTextSanitizer.Sanitize(
            (unexpected ? UiText.Get("Common_Text0189", "Unexpected Foundry Local error. ") : "") + feedback.Title + ": " + feedback.Message
            + UiText.Get("Common_Text0190", "\nNo fallback or acquisition was attempted. Refresh state before retrying."));
    }

    /// <summary>Helper for the is current workflow in this view model.</summary>
    private bool IsCurrent(AiChatConfiguration configuration) =>
        _settings.AiProvider == AiProviderKind.FoundryLocal
        && configuration == AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
    /// <summary>Helper for the known workflow in this view model.</summary>
    private static string Known(string value) => string.IsNullOrWhiteSpace(value) ? UiText.Get("Common_Text0191", "unknown") : value;
}
