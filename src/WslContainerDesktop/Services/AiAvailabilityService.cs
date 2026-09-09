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

        // Startup reads metadata only. Generation/warm-up is an explicit Settings operation.
        ScheduleRefresh();
    }

    public bool IsAvailable => Observation?.CanChat == true;
    public bool CanUseTools => Observation?.CanUseTools == true;
    public AiCapabilitySnapshot? Observation => IsConfigured()
        ? _capabilities.GetCached(AiConversationContext.Capture(_settings, _settings.AiProvider)) : null;

    public event EventHandler? Changed;

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

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        // An in-flight refresh may still release the semaphore after cancellation.
    }
}
