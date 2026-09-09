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

    [ObservableProperty] private string _endpoint;
    [ObservableProperty] private string _model;
    [ObservableProperty] private string _inventoryText = "Not refreshed. Enter the actual runtime URL and model ID. No default port or model is assumed.";
    [ObservableProperty] private string _status = "No runtime operation requested.";

    public string AcquisitionGuidance => FoundryLocalRuntimeService.AcquisitionGuidance;
    public string MemoryPolicy => FoundryLocalRuntimeService.MemoryPolicy;

    public FoundryLocalSettingsViewModel(ISettingsService settings,
        IFoundryLocalRuntimeService runtime, IAiCapabilityService capabilities,
        ILogger<FoundryLocalSettingsViewModel> logger)
    {
        _settings = settings;
        _runtime = runtime;
        _capabilities = capabilities;
        _logger = AiTextSanitizer.WrapLogger(logger);
        _endpoint = settings.AiFoundryLocalEndpoint;
        _model = settings.AiFoundryLocalModel;
    }

    partial void OnEndpointChanged(string value)
    {
        _settings.AiFoundryLocalEndpoint = value;
        ConfigurationChanged();
    }

    partial void OnModelChanged(string value)
    {
        _settings.AiFoundryLocalModel = value;
        ConfigurationChanged();
    }

    private void ConfigurationChanged()
    {
        RunOperationCommand.Cancel();
        _capabilities.Invalidate();
        _settings.Save();
        InventoryText = "Configuration changed. Refresh metadata; cached observations were cleared.";
        Status = "No operation requested for this configuration.";
    }

    public void OnProviderChanged()
    {
        RunOperationCommand.Cancel();
        _capabilities.Invalidate();
        InventoryText = "Provider changed. Refresh metadata after selecting Foundry Local.";
        Status = "Pending requests cancelled. Refresh observed state; completed actions are not rolled back.";
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
                if (operation == "load") return; // Always blocked; do not imply a load or readiness check occurred.
            }
            else if (operation != "refresh") throw new ArgumentException("Unknown Foundry Local operation.");
            var inventory = await _runtime.ReadInventoryAsync(configuration, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrent(configuration)) return;
            var rows = inventory.Catalog.Take(100).Select(m =>
                $"{m.Id} | cached: {inventory.Cached.Contains(m.Id)} | loaded: {inventory.Loaded.Contains(m.Id)}\n" +
                $"  version: {Known(m.Version)}; size MB: {m.FileSizeMb?.ToString() ?? "unknown"}; license: {Known(m.License)}\n" +
                $"  license information: {Known(m.LicenseDescription)}; task: {Known(m.Task)}; format: {Known(m.ModelType)}\n" +
                $"  hardware target: {Known(m.DeviceType)}; EP: {Known(m.ExecutionProvider)} (advertised, not validated); tools advertised: {m.SupportsToolCalling?.ToString() ?? "unknown"}");
            InventoryText = AiTextSanitizer.Sanitize(
                $"Catalog: {inventory.Catalog.Count}; cached: {inventory.Cached.Count}; loaded: {inventory.Loaded.Count}.\n" +
                $"Cached IDs: {string.Join(", ", inventory.Cached.Take(100))}\nLoaded IDs: {string.Join(", ", inventory.Loaded.Take(100))}\n" +
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
