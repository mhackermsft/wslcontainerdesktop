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

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <inheritdoc cref="IAiAvailabilityService"/>
public sealed class AiAvailabilityService : IAiAvailabilityService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    private readonly ISettingsService _settings;
    private readonly IAiCapabilityService _capabilities;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<AiAvailabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _debounceCts;
    private AiChatConfiguration? _configuration;

    /// <summary>
    /// Initializes a new <c>AiAvailabilityService</c> with the collaborators it needs from dependency injection.
    /// </summary>
    public AiAvailabilityService(
        ISettingsService settings,
        IAiCapabilityService capabilities,
        DispatcherQueue dispatcher,
        ILogger<AiAvailabilityService> logger)
    {
        _settings = settings;
        _capabilities = capabilities;
        _dispatcher = dispatcher;
        _logger = logger;

        _settings.Changed += OnSettingsChanged;
        // The cache is also refreshed by an assistant turn, which never routes through this service.
        // Without this the badge could show caution while a turn had just observed tool support.
        _capabilities.Changed += OnCapabilitiesChanged;

        // Startup reads metadata only. Generation/warm-up is an explicit Settings operation.
        ScheduleRefresh();
    }

    /// <summary>
    /// Gets whether this service is currently available for callers.
    /// </summary>
    public bool IsAvailable => Observation?.CanChat == true;
    /// <summary>
    /// Gets whether callers can use tools for the current app or engine state.
    /// </summary>
    public bool CanUseTools => Observation?.CanUseTools == true;
    /// <summary>
    /// Gets the latest capability observation when an AI provider is configured.
    /// </summary>
    public AiCapabilitySnapshot? Observation => IsConfigured()
        ? _capabilities.GetCached(AiConversationContext.Capture(_settings, _settings.AiProvider)) : null;

    /// <summary>
    /// Raised when stored data changes and bound UI should refresh.
    /// </summary>
    public event EventHandler? Changed;

    private void OnCapabilitiesChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var current = IsConfigured() ? AiConversationContext.Capture(_settings, _settings.AiProvider) : null;
        if (current != _configuration)
        {
            _configuration = current;
            _capabilities.Invalidate();
            _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = DebouncedRefreshAsync(cts.Token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceDelay, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer schedule superseded this one (debounce, retry delay, gate wait, or in-flight probe).
        }
        catch (Exception)
        {
            // Never log provider evidence or exception objects from a background observation.
            _logger.LogDebug("AI metadata refresh could not complete. Use Test capabilities in Settings.");
        }
    }

    private bool IsConfigured() =>
        _settings.AiFeaturesEnabled && _settings.AiProvider != AiProviderKind.None;

    /// <summary>
    /// Refreshes the cached AI availability observation.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Serialize metadata reads; never call the diagnosis-style TestAsync here.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var configuration = IsConfigured() ? AiConversationContext.Capture(_settings, _settings.AiProvider) : null;
            _ = configuration is null ? null
                : await _capabilities.GetAsync(configuration, ct: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _configuration = configuration;
            _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases long-lived resources owned by this service.
    /// </summary>
    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _capabilities.Changed -= OnCapabilitiesChanged;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        // An in-flight refresh may still release the semaphore after cancellation.
    }
}
