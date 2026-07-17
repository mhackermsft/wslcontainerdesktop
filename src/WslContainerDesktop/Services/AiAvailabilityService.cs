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
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private readonly ISettingsService _settings;
    private readonly IEnumerable<IAiProvider> _providers;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<AiAvailabilityService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _debounceCts;
    private bool _isAvailable;

    public AiAvailabilityService(
        ISettingsService settings,
        IEnumerable<IAiProvider> providers,
        DispatcherQueue dispatcher,
        ILogger<AiAvailabilityService> logger)
    {
        _settings = settings;
        _providers = providers;
        _dispatcher = dispatcher;
        _logger = logger;

        _settings.Changed += OnSettingsChanged;

        // Verify once at startup so the buttons reflect real connectivity from the first frame.
        ScheduleRefresh();
    }

    public bool IsAvailable => _isAvailable;

    public event EventHandler? Changed;

    private void OnSettingsChanged(object? sender, EventArgs e) => ScheduleRefresh();

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
            // A newer schedule superseded this one (debounce delay, gate wait, or in-flight probe). Ignore.
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Serialize checks; TestAsync runs a real (and possibly slow) model round-trip.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var available = await ProbeAsync(ct).ConfigureAwait(false);
            if (available == _isAvailable)
            {
                return;
            }

            _isAvailable = available;
            _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (!_settings.AiFeaturesEnabled || _settings.AiProvider == AiProviderKind.None)
        {
            return false;
        }

        var provider = _providers.FirstOrDefault(p => p.Kind == _settings.AiProvider);
        if (provider is null)
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TestTimeout);
            await provider.TestAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer schedule superseded this probe; propagate so we don't record a false negative.
            throw;
        }
        catch (Exception ex)
        {
            // Genuine failure or the TestTimeout elapsed (linked token, not ct) — treat as unavailable.
            _logger.LogDebug(ex, "AI availability probe failed for provider {Provider}.", _settings.AiProvider);
            return false;
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _gate.Dispose();
    }
}
