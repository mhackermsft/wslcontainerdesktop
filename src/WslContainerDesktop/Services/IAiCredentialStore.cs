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
/// Defines how AI provider credentials are stored without exposing callers to the underlying secure-storage mechanism.
/// </summary>
public interface IAiCredentialStore
{
    /// <summary>
    /// Attempts read secret and reports failure without throwing for expected conditions.
    /// </summary>
    bool TryReadSecret(AiProviderKind provider, out string? secret);

    /// <summary>
    /// Stores a secret value without putting it on a command line.
    /// </summary>
    void WriteSecret(AiProviderKind provider, string secret);

    /// <summary>
    /// Removes secret from persisted state or the engine.
    /// </summary>
    void DeleteSecret(AiProviderKind provider);
}
