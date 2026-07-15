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

/// <summary>
/// Records a persisted, in-app timeline of engine, container and image events. Container
/// lifecycle and engine up/down events are synthesized by diffing consecutive
/// <see cref="StatusMonitor"/> snapshots; image pull/build outcomes are recorded directly by
/// the images view model. The feed is independent of toast notification settings.
/// </summary>
public interface IActivityLog
{
    /// <summary>Most-recent-first collection bound by the activity page (mutated on the UI thread).</summary>
    ObservableCollection<ActivityEvent> Events { get; }

    /// <summary>Begins diffing engine snapshots into events. Safe to call once at startup.</summary>
    void Attach();

    /// <summary>Records an arbitrary event (inserted at the top and persisted).</summary>
    void Record(ActivityEvent evt);

    /// <summary>Records an image pull outcome.</summary>
    void RecordImagePull(string reference, bool success, string? error = null);

    /// <summary>Records an image build outcome.</summary>
    void RecordImageBuild(string tag, bool success, string? error = null);

    /// <summary>Clears the entire timeline (and the persisted file).</summary>
    void Clear();
}
