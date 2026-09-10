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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public sealed partial class AssistantToolset
{
    private static bool IsEvidenceFailure(Exception ex) => ex is IOException or InvalidOperationException or
        System.ComponentModel.Win32Exception or TimeoutException or JsonException;

    private string GetHealthObservations()
    {
        var snapshot = healthObservations?.GetSnapshot();
        var now = DateTimeOffset.UtcNow;
        if (snapshot is null || !snapshot.Available ||
            !string.Equals(snapshot.ExecutablePath, settings.WslcPath, StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { status = "unavailable", state = "Unknown",
                message = "No observations for the current available engine. No health probe was started." });
        var stale = now - snapshot.ObservedAt > TimeSpan.FromSeconds(15) || snapshot.ObservedAt > now;
        return JsonSerializer.Serialize(new
        {
            status = stale ? "stale" : "observed",
            source = "StatusMonitor native health and HealthWatchdog app supervision; no new probes or auto-heal",
            snapshot.ObservedAt,
            ageSeconds = Math.Max(0, (now - snapshot.ObservedAt).TotalSeconds),
            freshnessWindowSeconds = 15,
            message = "Cached point-in-time evidence only. Unknown or stale is not healthy. Absent/Disabled is not healthy. No probes started.",
            containers = snapshot.Containers.Select(c => new
            {
                c.Id, c.Name,
                processState = c.ContainerState.ToString(),
                observedState = c.State.ToString(),
                state = stale || now - c.ObservedAt > TimeSpan.FromSeconds(15) || c.ObservedAt > now
                    ? "Unknown" : c.State.ToString(),
                freshness = stale || now - c.ObservedAt > TimeSpan.FromSeconds(15) || c.ObservedAt > now ? "stale" : "observed",
                c.ObservedAt,
                ageSeconds = Math.Max(0, (now - c.ObservedAt).TotalSeconds),
            }),
            appObservationsAvailable = appHealthObservations is not null,
            appObservations = (appHealthObservations?.GetObservations() ?? []).Select(c =>
            {
                var matches = snapshot.Containers.Where(n => n.Id == c.ContainerId && n.Name == c.ContainerName &&
                    n.Generation == c.ContainerGeneration && n.ContainerState == ContainerState.Running).ToArray();
                var fresh = !stale && matches.Length == 1 && c.ObservedAt <= now &&
                    now - c.ObservedAt <= c.ObservationMaxAge && c.ObservationMaxAge > TimeSpan.Zero;
                return new
                {
                    c.ContainerId, c.ContainerName, c.ObservedAt,
                    ageSeconds = Math.Max(0, (now - c.ObservedAt).TotalSeconds),
                    freshnessWindowSeconds = c.ObservationMaxAge.TotalSeconds,
                    state = fresh ? c.State.ToString() : "Unknown",
                    freshness = fresh ? "observed" : "stale or identity unavailable",
                    c.RestartCount, c.MaxRestarts,
                };
            }),
        });
    }

    private async Task<string> GetVolumeUsageAsync(CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var executable = settings.WslcPath;
        try
        {
            var volumes = await wslc.ListVolumesAsync(ct).ConfigureAwait(false);
            var containers = await wslc.ListContainersAsync(all: true, ct).ConfigureAwait(false);
            var diagnostics = await VolumeUsageResolver.ResolveAsync(volumes, containers,
                wslc.InspectContainerAsync, ct).ConfigureAwait(false);
            if (!string.Equals(executable, settings.WslcPath, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { status = "unavailable",
                    message = "Engine configuration changed during scan; usage is Unknown. Refresh before any decision." });
            return JsonSerializer.Serialize(new
            {
                status = diagnostics.Count == 0 ? "observed" : "partial",
                startedAt, completedAt = DateTimeOffset.UtcNow,
                message = "Point-in-time mount scan, not an atomic inventory or deletion authorization. Refresh before cleanup. Estimated and partial users are not complete.",
                diagnosticCount = diagnostics.Count,
                diagnostics = diagnostics.Count == 0 ? null : "Some mount evidence is unavailable or incomplete; technical values withheld.",
                volumes = volumes.Select(v => new { v.Name, usage = v.UsageState.ToString(), users = v.ContainerUsers }),
            });
        }
        catch (Exception ex) when (IsEvidenceFailure(ex))
        {
            return JsonSerializer.Serialize(new { status = "unavailable", usage = "Unknown", startedAt,
                message = "Volume inventory or mount evidence is unavailable. No volume can be classified unused. Technical values withheld." });
        }
    }
}
