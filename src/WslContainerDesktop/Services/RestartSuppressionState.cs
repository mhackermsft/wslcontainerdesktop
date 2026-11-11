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

using System.Collections.Concurrent;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>A manual start can consume only the stop intent that preceded that operation.</summary>
public sealed class RestartSuppressionState
{
    /// <summary>
    /// Carries immutable service data between WSL Container Desktop components.
    /// </summary>
    public readonly record struct ResumeToken(string ContainerName, long StopVersion, AutomationSessionOwner? SessionOwner = null);

    private readonly record struct StopKey(AutomationSessionOwner? Owner, string Name);
    private sealed class StopKeyComparer : IEqualityComparer<StopKey>
    {
        public bool Equals(StopKey left, StopKey right) =>
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            (left.Owner is null && right.Owner is null || AutostartStateStore.SameOwner(left.Owner, right.Owner));
        public int GetHashCode(StopKey key) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(key.Name),
            key.Owner is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(key.Owner.ExecutablePath),
            key.Owner?.SessionName is null ? 0 : StringComparer.Ordinal.GetHashCode(key.Owner.SessionName));
    }

    private readonly ConcurrentDictionary<StopKey, long> _stops = new(new StopKeyComparer());
    private long _version;

    /// <summary>Raised when a manual stop is recorded for a specific owner.</summary>
    public event Action<string, AutomationSessionOwner?, string?>? Suppressed;

    /// <summary>
    /// Gets whether has suppressed containers for the current app or engine state.
    /// </summary>
    public bool HasSuppressedContainers => !_stops.IsEmpty;

    /// <summary>Whether this particular session has any manual-stop intent.</summary>
    public bool HasSuppressedContainersFor(AutomationSessionOwner? owner) =>
        _stops.Keys.Any(key => key.Owner is null && owner is null || AutostartStateStore.SameOwner(key.Owner, owner));
    /// <summary>
    /// Gets version for callers in the service or view-model layer.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// Temporarily suppresses restart reactions for a container.
    /// </summary>
    public void Suppress(string containerName, AutomationSessionOwner? owner = null, string? containerId = null)
    {
        var name = Normalize(containerName);
        var version = Interlocked.Increment(ref _version);
        _stops.AddOrUpdate(new StopKey(owner, name), version, (_, current) => Math.Max(current, version));
        Suppressed?.Invoke(name, owner, containerId);
    }

    /// <summary>
    /// Gets whether automatic restart handling is currently suppressed for the container.
    /// </summary>
    public bool IsSuppressed(string containerName, AutomationSessionOwner? owner = null) =>
        _stops.ContainsKey(new StopKey(owner, Normalize(containerName)));

    /// <summary>
    /// Records that the next observed restart came from a user-requested start.
    /// </summary>
    public ResumeToken CaptureExplicitStart(string containerName, long maximumVersion = long.MaxValue,
        AutomationSessionOwner? owner = null)
    {
        var name = Normalize(containerName);
        _stops.TryGetValue(new StopKey(owner, name), out var version);
        return new(name, version <= maximumVersion ? version : 0, owner);
    }

    /// <summary>
    /// Clears explicit-start tracking after the restart observation has been consumed.
    /// </summary>
    public bool CompleteExplicitStart(ResumeToken token, bool success)
    {
        if (!success || token.StopVersion == 0)
            return false;
        // Key/value removal is atomic: a stop arriving during the operation wins.
        return ((ICollection<KeyValuePair<StopKey, long>>)_stops)
            .Remove(new(new StopKey(token.SessionOwner, token.ContainerName), token.StopVersion));
    }

    /// <summary>
    /// Records that a restart came from an explicit user start operation used by WSL Container Desktop.
    /// </summary>
    public async Task<CommandResult> RunExplicitStartAsync(string containerName,
        Func<CancellationToken, Task<CommandResult>> operation, CancellationToken ct = default)
    {
        var token = CaptureExplicitStart(containerName);
        var result = await operation(ct).ConfigureAwait(false);
        CompleteExplicitStart(token, result.Success);
        return result;
    }

    private static string Normalize(string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        var name = containerName.TrimStart('/');
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
