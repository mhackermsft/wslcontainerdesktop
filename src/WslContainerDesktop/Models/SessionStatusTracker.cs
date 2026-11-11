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

namespace WslContainerDesktop.Models;

internal enum SessionIndicatorState { Unknown, Paused, Removing, Active, Attention, Failed }

internal sealed record SessionStatusObservation(string Name, WslcSessionInfo? Generation,
    SessionIndicatorState State, DateTimeOffset? ObservedAt, int? Running = null, int? Total = null);

/// <summary>Retains small status summaries from existing reads; never performs I/O or retains containers.</summary>
internal sealed class SessionStatusTracker
{
    private readonly Dictionary<string, SessionStatusObservation> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _removedAt = new(StringComparer.Ordinal);
    private DateTimeOffset _discoveredAt;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(90);

    internal void Clear()
    {
        _sessions.Clear();
        _removedAt.Clear();
        _discoveredAt = default;
    }

    internal void Discover(IReadOnlyList<WslcSessionInfo> sessions, DateTimeOffset at)
    {
        if (at < _discoveredAt) return;
        _discoveredAt = at;
        foreach (var name in _removedAt.Keys.Where(name => _removedAt[name] <= at).ToArray())
            _removedAt.Remove(name);
        var current = sessions.ToDictionary(session => session.Name, StringComparer.Ordinal);
        foreach (var name in _sessions.Keys.Where(name => !current.ContainsKey(name) &&
                     (_sessions[name].ObservedAt is null || _sessions[name].ObservedAt <= at)).ToArray())
            _sessions.Remove(name);
        foreach (var session in sessions)
        {
            if (_removedAt.ContainsKey(session.Name)) continue;
            if (!_sessions.TryGetValue(session.Name, out var previous) ||
                previous.Generation != session &&
                (previous.ObservedAt is null || previous.ObservedAt <= at))
                _sessions[session.Name] = new(session.Name, session, SessionIndicatorState.Active, at);
        }
    }

    internal void Observe(SessionStatusObservation observation)
    {
        if (observation.ObservedAt is null &&
            observation.State is not (SessionIndicatorState.Paused or SessionIndicatorState.Removing))
            return;
        if (_removedAt.TryGetValue(observation.Name, out var removedAt) &&
            observation.ObservedAt is { } observedAt && observedAt <= removedAt)
            return;
        if (!_sessions.ContainsKey(observation.Name) && observation.ObservedAt is { } timestamp &&
            timestamp < _discoveredAt)
            return;
        if (_sessions.TryGetValue(observation.Name, out var previous) &&
            previous.ObservedAt is { } previousAt && observation.ObservedAt is { } currentAt &&
            previousAt > currentAt)
            return;
        _sessions[observation.Name] = observation;
    }

    internal void Remove(string name, DateTimeOffset at)
    {
        _removedAt[name] = at;
        if (_sessions.TryGetValue(name, out var previous) && previous.ObservedAt <= at)
            _sessions.Remove(name);
    }

    internal void ObserveContainers(WslcSessionContainerInventory observation)
    {
        var containers = observation.Containers.Items;
        Observe(new(observation.Session.Name, observation.Session,
            observation.SessionChanged ? SessionIndicatorState.Unknown :
            !observation.IsComplete ? SessionIndicatorState.Attention : SessionIndicatorState.Active,
            observation.ObservedAt,
            observation.IsComplete ? containers!.Count(container => container.State == ContainerState.Running) : null,
            observation.IsComplete ? containers!.Count : null));
    }

    internal IReadOnlyList<SessionStatusObservation> Snapshot(DateTimeOffset now) =>
        _sessions.Values.Select(item => item.State is not (SessionIndicatorState.Paused or SessionIndicatorState.Removing) &&
                (item.ObservedAt is null || now - item.ObservedAt > MaximumAge)
                ? item with { State = SessionIndicatorState.Unknown, Running = null, Total = null } : item)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    internal static SessionIndicatorState Overall(IEnumerable<SessionStatusObservation> sessions, bool hasProblems)
    {
        var states = sessions.Select(session => session.State).ToArray();
        if (states.Contains(SessionIndicatorState.Failed)) return SessionIndicatorState.Failed;
        if (hasProblems || states.Contains(SessionIndicatorState.Attention)) return SessionIndicatorState.Attention;
        if (states.Contains(SessionIndicatorState.Unknown)) return SessionIndicatorState.Unknown;
        return states.Contains(SessionIndicatorState.Active) ? SessionIndicatorState.Active : SessionIndicatorState.Paused;
    }
}
