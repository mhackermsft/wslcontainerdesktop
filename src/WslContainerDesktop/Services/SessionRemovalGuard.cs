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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Fences pending removals and observations of a removed session generation.</summary>
public sealed class SessionRemovalGuard(ManagedSessionStore sessions)
{
    private sealed record Fence(AutomationSessionOwner Owner, WslcSessionInfo? Generation,
        DateTimeOffset Cutoff, bool Active, bool IsRemoval, bool IsStop = false, bool IsResuming = false);
    private readonly object _gate = new();
    private readonly List<Fence> _fences = [];

    internal void Begin(AutomationSessionOwner owner, WslcSessionInfo? generation)
    {
        lock (_gate)
        {
            if (_fences.Any(fence => fence.Active && AutostartStateStore.SameOwner(fence.Owner, owner)))
                throw new InvalidOperationException(UiText.Get("ManagedSession_OperationInProgress",
                    "A session action is already in progress."));
            _fences.RemoveAll(fence => AutostartStateStore.SameOwner(fence.Owner, owner));
            _fences.Add(new(owner, generation, DateTimeOffset.UtcNow, true, false));
        }
    }

    internal void Complete(AutomationSessionOwner owner, WslcSessionInfo? generation)
    {
        lock (_gate)
        {
            var previous = _fences.FirstOrDefault(fence => AutostartStateStore.SameOwner(fence.Owner, owner));
            generation ??= previous?.Generation;
            _fences.RemoveAll(fence => AutostartStateStore.SameOwner(fence.Owner, owner));
            _fences.Add(new(owner, generation, DateTimeOffset.UtcNow, false, previous?.IsRemoval == true));
        }
    }

    internal void CancelStop(AutomationSessionOwner owner)
    {
        lock (_gate)
            _fences.RemoveAll(fence => fence.Active && fence.IsStop &&
                AutostartStateStore.SameOwner(fence.Owner, owner));
    }

    internal void ReleaseStoppedSession(AutomationSessionOwner owner)
    {
        lock (_gate)
        {
            var wasStopped = _fences.Any(fence => fence.IsStop &&
                (fence.Active || fence.Generation is not null || fence.IsResuming) &&
                AutostartStateStore.SameOwner(fence.Owner, owner));
            _fences.RemoveAll(fence => fence.IsStop && AutostartStateStore.SameOwner(fence.Owner, owner));
            _fences.Add(new(owner, null, DateTimeOffset.UtcNow, false, false, true, wasStopped));
        }
    }

    internal bool IsResuming(AutomationSessionOwner owner)
    {
        lock (_gate)
            return _fences.Any(fence => fence.IsResuming &&
                DateTimeOffset.UtcNow - fence.Cutoff <= AutomationSessionScopePlanner.ObservationMaxAge &&
                AutostartStateStore.SameOwner(fence.Owner, owner));
    }

    internal void CompleteResumeObservation(AutomationSessionOwner owner, DateTimeOffset observedAt)
    {
        lock (_gate)
            for (var index = 0; index < _fences.Count; index++)
                if (_fences[index] is { IsResuming: true } fence && observedAt > fence.Cutoff &&
                    AutostartStateStore.SameOwner(fence.Owner, owner))
                    _fences[index] = fence with { IsResuming = false };
    }

    internal bool IsIntentionallyStopped(AutomationSessionOwner owner)
    {
        lock (_gate)
            return _fences.Any(fence => fence.IsStop && (fence.Active || fence.Generation is not null) &&
                AutostartStateStore.SameOwner(fence.Owner, owner));
    }

    internal void CompleteStop(AutomationSessionOwner owner)
    {
        lock (_gate)
        {
            var fence = _fences.FirstOrDefault(item => item.IsStop &&
                AutostartStateStore.SameOwner(item.Owner, owner));
            if (fence is not null)
            {
                _fences.Remove(fence);
                _fences.Add(fence with { Active = false, Cutoff = DateTimeOffset.UtcNow });
            }
        }
    }

    internal void BeginStop(AutomationSessionOwner owner, WslcSessionInfo generation)
    {
        lock (_gate)
        {
            if (_fences.Any(fence => fence.Active && AutostartStateStore.SameOwner(fence.Owner, owner)))
                throw new InvalidOperationException(UiText.Get("ManagedSession_OperationInProgress",
                    "A session action is already in progress."));
            _fences.RemoveAll(fence => AutostartStateStore.SameOwner(fence.Owner, owner));
            _fences.Add(new(owner, generation, DateTimeOffset.UtcNow, true, false, true));
        }
    }

    internal bool IsExpectedStopObservation(AutomationSessionOwner owner, DateTimeOffset observedAt)
    {
        lock (_gate)
            return _fences.Any(fence => fence.IsStop && AutostartStateStore.SameOwner(fence.Owner, owner) &&
                (fence.Active || observedAt <= fence.Cutoff));
    }

    internal void ResumeRemoval(AutomationSessionOwner owner, WslcSessionInfo? generation)
    {
        lock (_gate)
        {
            var existing = _fences.FirstOrDefault(fence => fence.Active &&
                AutostartStateStore.SameOwner(fence.Owner, owner));
            if (existing is not null)
            {
                if (!existing.IsRemoval)
                    throw new InvalidOperationException(UiText.Get("ManagedSession_OperationInProgress",
                        "A session action is already in progress."));
                return;
            }
            generation ??= _fences.FirstOrDefault(fence => AutostartStateStore.SameOwner(fence.Owner, owner))?.Generation;
            _fences.RemoveAll(fence => AutostartStateStore.SameOwner(fence.Owner, owner));
            _fences.Add(new(owner, generation, DateTimeOffset.UtcNow, true, true));
        }
    }

    internal void EndRemovalAttempt(AutomationSessionOwner owner)
    {
        lock (_gate)
        {
            var fence = _fences.FirstOrDefault(item => item.Active && item.IsRemoval &&
                AutostartStateStore.SameOwner(item.Owner, owner));
            if (fence is null)
                return;
            _fences.Remove(fence);
            _fences.Add(fence with { Active = false, Cutoff = DateTimeOffset.UtcNow });
        }
    }

    internal bool IsBlocked(AutomationSessionOwner owner, WslcSessionInfo? generation, DateTimeOffset observedAt)
    {
        if (sessions.Snapshot().Any(session => session.PendingDeletion &&
                AutostartStateStore.SameOwner(session.PendingRemovalOwner, owner)))
            return true;
        lock (_gate)
        {
            if (generation is not null)
                for (var index = 0; index < _fences.Count; index++)
                    if (_fences[index] is { IsStop: true, Active: false, Generation: not null } fence &&
                        !SameGeneration(fence.Generation, generation) && observedAt > fence.Cutoff &&
                        AutostartStateStore.SameOwner(fence.Owner, owner))
                        _fences[index] = fence with { Generation = null, IsResuming = true };
            return _fences.Any(fence => AutostartStateStore.SameOwner(fence.Owner, owner) &&
                (fence.Active || observedAt <= fence.Cutoff || SameGeneration(fence.Generation, generation) ||
                 fence.IsStop && fence.Generation is not null && generation is null));
        }
    }

    internal bool IsRemovalInProgress(AutomationSessionOwner owner)
    {
        lock (_gate)
            return _fences.Any(fence => fence.Active && fence.IsRemoval &&
                AutostartStateStore.SameOwner(fence.Owner, owner));
    }

    internal bool IsExpectedRemovalObservation(
        AutomationSessionOwner owner, WslcSessionInfo? generation, DateTimeOffset observedAt)
    {
        var pending = sessions.Snapshot().Any(session => session.PendingDeletion &&
            AutostartStateStore.SameOwner(session.PendingRemovalOwner, owner));
        lock (_gate)
            return _fences.Any(fence => fence.IsRemoval && AutostartStateStore.SameOwner(fence.Owner, owner) &&
                (fence.Active || !pending && (observedAt <= fence.Cutoff || SameGeneration(fence.Generation, generation))));
    }

    private static bool SameGeneration(WslcSessionInfo? left, WslcSessionInfo? right) =>
        left is not null && right is not null && left.ID == right.ID && left.CreatorPid == right.CreatorPid &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal);
}
