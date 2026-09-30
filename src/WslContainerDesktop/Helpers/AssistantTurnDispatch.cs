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

namespace WslContainerDesktop.Helpers;

/// <summary>Checks ownership at delivery, not just when a callback is queued.</summary>
public static class AssistantTurnDispatch
{
    /// <summary>Queues a UI update only if the same assistant conversation turn is still active when delivered.</summary>
    /// <param name="generation">The turn generation that scheduled the update.</param>
    /// <param name="currentGeneration">Reads the generation currently owned by the view model.</param>
    /// <param name="isActive">Returns whether the owning conversation is still accepting updates.</param>
    /// <param name="enqueue">Dispatcher-style function that runs the callback on the UI thread.</param>
    /// <param name="update">UI update to run if the generation and activity checks still pass.</param>
    public static void Queue(
        int generation,
        Func<int> currentGeneration,
        Func<bool> isActive,
        Action<Action> enqueue,
        Action update)
    {
        enqueue(() =>
        {
            if (generation == currentGeneration() && isActive())
            {
                update();
            }
        });
    }
}
