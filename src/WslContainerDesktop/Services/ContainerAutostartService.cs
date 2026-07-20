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

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;
using WslContainerDesktop.Tray;

namespace WslContainerDesktop.Services;

/// <summary>
/// Remembers which containers were running the last time the app observed them and, on the next
/// launch, starts any that are stopped again — the app's answer to "bring my containers back after
/// a reboot." A Windows reboot or <c>wsl --shutdown</c> stops every wslc container (they remain in
/// the list as <em>stopped</em>), and there is no background daemon to restart them, so this closes
/// that gap.
///
/// <para><b>Desired-running set.</b> While the app runs, <see cref="StatusMonitor"/> polls the
/// engine; each healthy snapshot's running containers are persisted to
/// <c>container-autostart.json</c> (next to <c>settings.json</c>). Because a container the user
/// stops leaves that set on the very next poll, manually-stopped containers are naturally excluded
/// from restore — only containers running when the host went down come back.</para>
///
/// <para><b>Ordering.</b> The persisted set is loaded at construction and used by
/// <see cref="RestoreThenAttachAsync"/> <em>before</em> live tracking is attached, so the first
/// post-reboot snapshot (everything stopped) cannot overwrite the desired set before it is used.</para>
///
/// <para>Load/persist failures never crash the app: a corrupt or missing state file simply yields
/// an empty desired set.</para>
/// </summary>
public sealed class ContainerAutostartService : IDisposable
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslContainerDesktop");

    private static readonly string StateFile = Path.Combine(SettingsDirectory, "container-autostart.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>How long to wait for the engine to come up after launch before giving up on restore.</summary>
    private static readonly TimeSpan EngineWaitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Delay between engine-availability probes while waiting for restore.</summary>
    private static readonly TimeSpan EngineProbeInterval = TimeSpan.FromSeconds(3);

    private readonly IWslcService _wslc;
    private readonly StatusMonitor _monitor;
    private readonly ISettingsService _settings;
    private readonly ILogger<ContainerAutostartService> _logger;

    /// <summary>Containers running when the app last observed them, loaded from disk at construction.</summary>
    private readonly IReadOnlyList<AutostartEntry> _desired;

    private readonly object _persistGate = new();
    private string _lastPersistedSignature;
    private bool _tracking;
    private bool _disposed;

    public ContainerAutostartService(IWslcService wslc, StatusMonitor monitor, ISettingsService settings, ILogger<ContainerAutostartService> logger)
    {
        _wslc = wslc;
        _monitor = monitor;
        _settings = settings;
        _logger = logger;

        _desired = Load();
        _lastPersistedSignature = Signature(_desired);
    }

    /// <summary>
    /// Restores previously-running containers (when enabled) and then begins tracking the running
    /// set so future launches have an up-to-date picture. Never throws — safe to fire-and-forget
    /// from launch so a failure can't block startup.
    /// </summary>
    public async Task RestoreThenAttachAsync(CancellationToken ct = default)
    {
        try
        {
            await RestoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Container autostart restore failed.");
        }
        finally
        {
            Attach();
        }
    }

    private async Task RestoreAsync(CancellationToken ct)
    {
        if (!_settings.RestartRunningContainersOnLaunch)
        {
            _logger.LogDebug("Container autostart disabled; skipping restore.");
            return;
        }

        if (_desired.Count == 0)
        {
            return;
        }

        if (!await WaitForEngineAsync(ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Engine did not become available in time; skipping container autostart restore.");
            return;
        }

        var containers = await _wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
        var byId = new Dictionary<string, ContainerInfo>(StringComparer.Ordinal);
        foreach (var c in containers)
        {
            byId[c.Id] = c;
        }

        var restored = 0;
        foreach (var entry in _desired)
        {
            ct.ThrowIfCancellationRequested();

            var container = ResolveContainer(containers, byId, entry);
            if (container is null)
            {
                // The container no longer exists (e.g. it was created with --rm, or removed).
                continue;
            }

            if (container.State == ContainerState.Running || container.State == ContainerState.Paused)
            {
                continue;
            }

            var result = await _wslc.StartContainerAsync(container.Id, ct).ConfigureAwait(false);
            if (result.Success)
            {
                restored++;
                _logger.LogInformation("Autostart: restarted container {Name} ({Id}) after reboot.",
                    string.IsNullOrWhiteSpace(container.Name) ? container.ShortId : container.Name, container.ShortId);
            }
            else
            {
                _logger.LogWarning("Autostart: failed to restart container {Name} ({Id}): {Detail}",
                    string.IsNullOrWhiteSpace(container.Name) ? container.ShortId : container.Name, container.ShortId,
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            }
        }

        if (restored > 0)
        {
            _logger.LogInformation("Autostart restored {Count} container(s) that were running before the last shutdown.", restored);
            // Nudge the monitor so the newly-started containers are reflected (and persisted) promptly.
            _monitor.RequestRefresh();
        }
    }

    /// <summary>Begins persisting the running set from status snapshots. Idempotent.</summary>
    private void Attach()
    {
        if (_tracking || _disposed)
        {
            return;
        }

        _tracking = true;
        _monitor.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, EngineStatusSnapshot snapshot)
    {
        // Only record while the engine is healthy; a down/unknown snapshot carries no container list
        // and must not clobber the desired set with an empty one.
        if (snapshot.Health is not (EngineHealth.Healthy or EngineHealth.Degraded))
        {
            return;
        }

        var running = snapshot.Containers
            .Where(c => c.State == ContainerState.Running)
            .Select(c => new AutostartEntry { Id = c.Id, Name = c.Name?.TrimStart('/') ?? string.Empty })
            .ToList();

        Persist(running);
    }

    private void Persist(IReadOnlyList<AutostartEntry> entries)
    {
        var signature = Signature(entries);

        lock (_persistGate)
        {
            if (signature == _lastPersistedSignature)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                var json = JsonSerializer.Serialize(entries, SerializerOptions);
                File.WriteAllText(StateFile, json);
                _lastPersistedSignature = signature;
            }
            catch (Exception ex)
            {
                // Best effort; a failed write just means the set is a poll stale.
                _logger.LogDebug(ex, "Failed to persist container autostart state to {Path}.", StateFile);
            }
        }
    }

    private List<AutostartEntry> Load()
    {
        try
        {
            if (!File.Exists(StateFile))
            {
                return new List<AutostartEntry>();
            }

            var json = File.ReadAllText(StateFile);
            var loaded = JsonSerializer.Deserialize<List<AutostartEntry>>(json);
            if (loaded is null)
            {
                return new List<AutostartEntry>();
            }

            return loaded
                .Where(e => e is not null && (!string.IsNullOrWhiteSpace(e.Id) || !string.IsNullOrWhiteSpace(e.Name)))
                .ToList();
        }
        catch (Exception ex)
        {
            // A corrupt state file must never crash the app; start with an empty set.
            _logger.LogWarning(ex, "Failed to load container autostart state from {Path}; starting empty.", StateFile);
            return new List<AutostartEntry>();
        }
    }

    /// <summary>
    /// Resolves a persisted entry to a current container, preferring the stable id and falling back
    /// to the name (which survives even if the engine reassigned ids across a rebuild).
    /// </summary>
    private static ContainerInfo? ResolveContainer(
        IReadOnlyList<ContainerInfo> containers,
        IReadOnlyDictionary<string, ContainerInfo> byId,
        AutostartEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Id) && byId.TryGetValue(entry.Id, out var byIdMatch))
        {
            return byIdMatch;
        }

        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            return containers.FirstOrDefault(c =>
                string.Equals(c.Name?.TrimStart('/'), entry.Name, StringComparison.Ordinal));
        }

        return null;
    }

    private async Task<bool> WaitForEngineAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + EngineWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await _wslc.IsEngineAvailableAsync(ct).ConfigureAwait(false))
            {
                return true;
            }

            try
            {
                await Task.Delay(EngineProbeInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return false;
    }

    private static string Signature(IReadOnlyList<AutostartEntry> entries) =>
        string.Join('\n', entries
            .Select(e => e.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .OrderBy(id => id, StringComparer.Ordinal));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_tracking)
        {
            _monitor.StatusChanged -= OnStatusChanged;
        }
    }

    /// <summary>A container that was running when the app last observed it, keyed by id (with name fallback).</summary>
    public sealed class AutostartEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
