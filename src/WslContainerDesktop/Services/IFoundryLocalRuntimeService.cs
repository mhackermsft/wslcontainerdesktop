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
/// Controls and observes the local Foundry model runtime used by the Foundry Local AI provider.
/// </summary>
public interface IFoundryLocalRuntimeService
{
    /// <summary>Raised when loaded model state changes and provider capability should be refreshed.</summary>
    event Action? StateChanged;
    /// <summary>Reads the currently loaded Foundry models without mutating the runtime.</summary>
    Task<FoundryLocalInventory> ReadInventoryAsync(AiChatConfiguration configuration, CancellationToken ct);
    /// <summary>Requests that Foundry Local load the configured model.</summary>
    Task<FoundryLocalMutationResult> LoadAsync(AiChatConfiguration configuration, CancellationToken ct);
    /// <summary>Requests that Foundry Local unload the configured model.</summary>
    Task<FoundryLocalMutationResult> UnloadAsync(AiChatConfiguration configuration, CancellationToken ct);
}
