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

using System.Collections.ObjectModel;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Applies owner-qualified engine events to the bounded, newest-first activity list.</summary>
internal static class ActivityEventBatch
{
    internal static bool Apply(ObservableCollection<ActivityEvent> entries, IReadOnlyList<EngineEvent> events, int limit)
    {
        var changed = false;
        foreach (var evt in events)
        {
            var owner = evt.SessionOwner;
            var sameSession = entries.Where(entry => EngineEventActivity.HasSameOwner(entry, owner)).ToArray();
            if (sameSession.Any(entry => string.Equals(entry.SourceEventKey, evt.StableKey, StringComparison.Ordinal)) ||
                EngineEventActivity.IsRedundantStop(evt, sameSession))
                continue;
            if (EngineEventActivity.FindSupersededStop(evt, sameSession) is { } stop)
            {
                entries.Remove(stop);
                changed = true;
            }
            changed |= Insert(entries, EngineEventActivity.ToActivityEvent(evt, owner), limit);
        }
        return changed;
    }

    internal static bool Insert(ObservableCollection<ActivityEvent> entries, ActivityEvent evt, int limit)
    {
        var index = ActivityEvent.NewestFirstInsertIndex(entries, evt.Timestamp);
        if (index >= limit)
            return false;
        entries.Insert(index, evt);
        while (entries.Count > limit)
            entries.RemoveAt(entries.Count - 1);
        return true;
    }
}
