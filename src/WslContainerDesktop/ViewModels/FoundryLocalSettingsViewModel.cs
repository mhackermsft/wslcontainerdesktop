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
    private int _configurationRevision;
    private bool _confirmingConnection;
    private bool _applyingReadyConfiguration;
    private CancellationTokenSource? _runtimeSetupCancellation;

    [ObservableProperty] private string _endpoint;
    [ObservableProperty] private string _model;
    [ObservableProperty] private string _inventoryText = "Not refreshed. Enter the actual runtime URL and model ID. No default port or model is assumed.";
    [ObservableProperty] private string _status = "No runtime operation requested.";
    [ObservableProperty] private string _setupStatus = "Discovery is read-only and runs only when requested.";
    [ObservableProperty] private bool _canUseDiscoveredEndpoint;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallRuntime))]
    [NotifyPropertyChangedFor(nameof(CanStageModelFiles))]
    [NotifyPropertyChangedFor(nameof(CanPrepareInitialModel))]
    [NotifyPropertyChangedFor(nameof(IsPreparingAnything))]
    private bool _isInstallingRuntime;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallRuntime))]
    [NotifyPropertyChangedFor(nameof(CanStageModelFiles))]
    [NotifyPropertyChangedFor(nameof(CanPrepareInitialModel))]
    [NotifyPropertyChangedFor(nameof(IsPreparingAnything))]
    private bool _isPreparingModelFiles;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallRuntime))]
    [NotifyPropertyChangedFor(nameof(CanStageModelFiles))]
    [NotifyPropertyChangedFor(nameof(CanPrepareInitialModel))]
    [NotifyPropertyChangedFor(nameof(IsPreparingAnything))]
    private bool _isPreparingInitialModel;

    public string AcquisitionGuidance => FoundryLocalStandaloneRuntimeService.AcquisitionGuidance;
    public string MemoryPolicy => FoundryLocalStandaloneRuntimeService.MemoryPolicy;
    public string InstallationGuidance => _setup.AvailabilityGuidance;
    public bool IsPreparingAnything => IsInstallingRuntime || IsPreparingModelFiles || IsPreparingInitialModel;
    public bool CanInstallRuntime => _setup.CanInstall && !IsPreparingAnything;
    public bool CanStageModelFiles => _setup.CanStageModelFiles && !IsPreparingAnything;
    public bool CanPrepareInitialModel => _initialSetup is not null && !IsPreparingAnything;
    public string SetupCacheLocation => "Setup cache: " + _setup.CacheLocation;
    public string ModelStagingLocation => "Model-file staging (not the Foundry runtime cache): " + _setup.ModelCacheLocation;

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
    }

    partial void OnEndpointChanged(string value)
    {
        _settings.AiFoundryLocalEndpoint = value;
        if (!_applyingReadyConfiguration) ConfigurationChanged();
    }

    partial void OnModelChanged(string value)
    {
        _settings.AiFoundryLocalModel = value;
        if (!_applyingReadyConfiguration) ConfigurationChanged();
    }

    private void ConfigurationChanged()
    {
        InvalidateDiscovery();
        RunOperationCommand.Cancel();
        _capabilities.Invalidate();
        _settings.Save();
        InventoryText = "Configuration changed. Refresh metadata; cached observations were cleared.";
        Status = "No operation requested for this configuration.";
    }

    public void OnProviderChanged()
    {
        InvalidateDiscovery();
        RunOperationCommand.Cancel();
        _capabilities.Invalidate();
        InventoryText = "Provider changed. Refresh metadata after selecting Foundry Local.";
        Status = "Pending requests cancelled. Refresh observed state; completed actions are not rolled back.";
    }

    private void InvalidateDiscovery()
    {
        _configurationRevision++;
        _runtimeSetupCancellation?.Cancel();
        DiscoverCommand.Cancel();
        _connectionPlan = null;
        CanUseDiscoveredEndpoint = false;
        SetupStatus = IsPreparingModelFiles
            ? "Configuration changed; model-file preparation cancelled. Completed and partial staging data are retained."
            : IsInstallingRuntime
            ? "Configuration changed; runtime setup cancelled. Windows deployment may still complete. Inspect installed packages before retrying."
            : "Configuration changed. Discover again before connecting; no runtime operation requested for these settings.";
    }

    public Task InstallRuntimeAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunSetupAsync(false, confirm);
    public Task StageModelFilesAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunSetupAsync(true, confirm);
    public Task PrepareInitialModelAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunInitialAsync(false, confirm);
    public Task StopServerAsync(Func<string, CancellationToken, Task<bool>> confirm) => RunInitialAsync(true, confirm);

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
                InventoryText = "Initial CPU model is loaded; run independent capability checks before assistant use.";
            }
            SetupStatus = result.Guidance;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Foundry initial-model operation cancelled.");
            if (Current()) SetupStatus = "Cancelled. Runtime/model state may have changed; files and packages retained.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry initial-model operation failed.");
            if (Current()) SetupStatus = "Preparation failed: " + AiTextSanitizer.Sanitize(ex.Message)
                + " No automatic rollback or retry.";
        }
        finally
        {
            inFlight = false;
            _runtimeSetupCancellation = null;
            IsPreparingInitialModel = false;
            _capabilities.Invalidate();
        }
    }

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
                ? "Preparing pinned model-file download; no network until you confirm…"
                : "Preparing runtime-only installation; no downloads or registration until you confirm…";
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
                SetupStatus = "Unexpected preparation failure. No automatic retry or cleanup. Inspect retained setup cache and any requested Windows registration before retrying.";
        }
        finally
        {
            inFlight = false;
            _runtimeSetupCancellation = null;
            IsInstallingRuntime = false;
            IsPreparingModelFiles = false;
        }
    }

    [RelayCommand]
    private void CancelRuntimeSetup() => _runtimeSetupCancellation?.Cancel();

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
            SetupStatus = "Discovering the existing standalone CLI/server; no installation or start requested…";
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
            SetupStatus = plan.Confirmation + (CanUseDiscoveredEndpoint ? "\nReview and confirm to change the endpoint setting."
                : "\nEnter an exact model ID advertised by this server, then discover again. No model was selected automatically.");
            InventoryText = AiTextSanitizer.Sanitize("Discovered catalog IDs (up to 100): " +
                string.Join(", ", plan.Inventory.Catalog.Take(100).Select(model => model.Id)));
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Foundry Local discovery cancelled.");
            if (revision == _configurationRevision && IsCurrent(original))
                SetupStatus = "Discovery cancelled or timed out. Settings and external runtime were not changed.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry Local discovery failed without fallback.");
            if (revision == _configurationRevision && IsCurrent(original))
                SetupStatus = "Discovery failed: " + AiTextSanitizer.Sanitize(ex.Message) +
                    "\nSettings and runtime were not changed. Enter the actual endpoint manually or inspect the standalone installation.";
        }
        finally
        {
            reading = false;
        }
    }

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
            SetupStatus = "Confirmed endpoint saved. Model selection preserved; no runtime or model was changed. Refresh metadata and test capabilities before inference.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Foundry Local connection confirmation failed.");
            if (revision == _configurationRevision && IsCurrent(plan.Original))
                SetupStatus = "Connection was not confirmed. Inspect settings and discover again.";
        }
        finally
        {
            _confirmingConnection = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunOperationAsync(string operation, CancellationToken ct)
    {
        var configuration = AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(configuration))
                throw new OperationCanceledException("Foundry Local is no longer the selected provider.");
            Status = operation == "refresh" ? "Reading metadata only…" : "Requesting model memory change…";
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
            var rows = inventory.Catalog.Take(100).Select(m =>
                $"{m.Id} | cached: {(inventory.CacheStateKnown ? inventory.Cached.Contains(m.Id).ToString() : "unknown")} | loaded: {(inventory.LoadStateKnown ? inventory.Loaded.Contains(m.Id).ToString() : "unknown")}\n" +
                $"  version: {Known(m.Version)}; size MB: {m.FileSizeMb?.ToString() ?? "unknown"}; license: {Known(m.License)}\n" +
                $"  license information: {Known(m.LicenseDescription)}; task: {Known(m.Task)}; format: {Known(m.ModelType)}\n" +
                $"  hardware target: {Known(m.DeviceType)}; EP: {Known(m.ExecutionProvider)} (advertised, not validated); tools advertised: {m.SupportsToolCalling?.ToString() ?? "unknown"}");
            InventoryText = AiTextSanitizer.Sanitize(
                $"Catalog: {inventory.Catalog.Count}; cached: {(inventory.CacheStateKnown ? inventory.Cached.Count.ToString() : "unknown")}; loaded: {(inventory.LoadStateKnown ? inventory.Loaded.Count.ToString() : "unknown")}.\n" +
                $"Cached IDs: {(inventory.CacheStateKnown ? string.Join(", ", inventory.Cached.Take(100)) : "unknown")}\nLoaded IDs: {(inventory.LoadStateKnown ? string.Join(", ", inventory.Loaded.Take(100)) : "unknown")}\n" +
                "Showing up to 100 catalog entries. Registered external entries are not eligible for local inference.\n" +
                string.Join("\n", rows), AiTextSanitizer.DiagnosticLimit);
            if (operation == "refresh") Status = "Metadata refreshed. No load, download or inference was requested.";
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Foundry Local operation cancelled or invalidated.");
            if (IsCurrent(configuration))
                Status = "Request cancelled or timed out. Memory/model state is unknown until refreshed; completed actions are not rolled back.";
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

    private void ShowFailure(Exception error, string operation, AiChatConfiguration configuration, bool unexpected)
    {
        if (!IsCurrent(configuration)) return; // Never publish stale feedback into another provider's UI.
        var feedback = AiErrorClassifier.Classify(error, AiErrorContext.For(AiProviderKind.FoundryLocal,
            operation, configuration.Endpoint, configuration.Model));
        Status = AiTextSanitizer.Sanitize(
            (unexpected ? "Unexpected Foundry Local error. " : "") + feedback.Title + ": " + feedback.Message
            + "\nNo fallback or acquisition was attempted. Refresh state before retrying.");
    }

    private bool IsCurrent(AiChatConfiguration configuration) =>
        _settings.AiProvider == AiProviderKind.FoundryLocal
        && configuration == AiConversationContext.Capture(_settings, AiProviderKind.FoundryLocal);
    private static string Known(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
