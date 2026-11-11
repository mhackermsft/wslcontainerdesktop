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

/// <summary>A session created by this app and restored when the app opens.</summary>
public sealed record ManagedSessionDefinition(
    Guid Id, string Name, bool PendingDeletion = false, bool StorageCreated = false)
{
    /// <summary>The engine used when this app last started the session.</summary>
    public string? LastExecutablePath { get; init; }
    /// <summary>The captured owner whose automatic-start history must be removed.</summary>
    public AutomationSessionOwner? PendingRemovalOwner { get; init; }
}
