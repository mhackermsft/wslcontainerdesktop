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
/// Tracks which templates the user has hidden from the gallery, keyed by
/// <c>StackTemplate.Id</c>. Applies to built-in and user templates alike (built-ins can be hidden
/// but not deleted). Persisted as a simple id set in <c>template-visibility.json</c>.
/// </summary>
public interface ITemplateVisibilityStore
{
    /// <summary>Raised whenever the hidden set changes.</summary>
    event EventHandler? Changed;

    /// <summary>True when the template with this id is currently hidden.</summary>
    bool IsHidden(string id);

    /// <summary>Hides or unhides a template. No-op if already in the requested state.</summary>
    void SetHidden(string id, bool hidden);
}
