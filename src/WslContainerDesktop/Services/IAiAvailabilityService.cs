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

namespace WslContainerDesktop.Services;

/// <summary>
/// Tracks whether the app can actually talk to the configured AI provider. AI entry points
/// (the assistant button, the Diagnose button) observe this so they only appear once a real
/// round-trip to the model has succeeded — not merely because AI is toggled on in Settings.
/// </summary>
public interface IAiAvailabilityService
{
    /// <summary>True when AI is enabled, a provider is chosen, and a live test call succeeded.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised (on the UI thread) whenever <see cref="IsAvailable"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Re-verifies connectivity to the configured provider. Coalesces rapid calls.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
