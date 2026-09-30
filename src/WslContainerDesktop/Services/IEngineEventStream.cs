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

/// <summary>
/// Streams live <c>wslc events</c> output into app events and connection state for activity views.
/// </summary>
public interface IEngineEventStream
{
    /// <summary>Raised when a new engine event is parsed from the stream.</summary>
    event EventHandler<EngineEvent>? EventReceived;

    /// <summary>Raised when the event stream connects or disconnects.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>True while the background event process is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Recent events retained for display when the activity page opens.</summary>
    IReadOnlyList<EngineEvent> RecentEvents { get; }

    /// <summary>Starts the event stream if it is not already running.</summary>
    void Start();

    /// <summary>Restarts the event stream after engine settings or availability change.</summary>
    void Restart();

    /// <summary>Stops the event stream and releases its process.</summary>
    void Stop();
}
