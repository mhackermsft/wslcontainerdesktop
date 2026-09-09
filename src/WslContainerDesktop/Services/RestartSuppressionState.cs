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
    public readonly record struct ResumeToken(string ContainerName, long StopVersion);

    private readonly ConcurrentDictionary<string, long> _stops = new(StringComparer.Ordinal);
    private long _version;

    public bool HasSuppressedContainers => !_stops.IsEmpty;
    public long Version => Volatile.Read(ref _version);

    public void Suppress(string containerName)
    {
        var name = Normalize(containerName);
        var version = Interlocked.Increment(ref _version);
        _stops.AddOrUpdate(name, version, (_, current) => Math.Max(current, version));
    }

    public bool IsSuppressed(string containerName) => _stops.ContainsKey(Normalize(containerName));

    public ResumeToken CaptureExplicitStart(string containerName, long maximumVersion = long.MaxValue)
    {
        var name = Normalize(containerName);
        _stops.TryGetValue(name, out var version);
        return new(name, version <= maximumVersion ? version : 0);
    }

    public bool CompleteExplicitStart(ResumeToken token, bool success)
    {
        if (!success || token.StopVersion == 0)
            return false;
        // Key/value removal is atomic: a stop arriving during the operation wins.
        return ((ICollection<KeyValuePair<string, long>>)_stops)
            .Remove(new(token.ContainerName, token.StopVersion));
    }

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
