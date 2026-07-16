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
/// Persists user-authored and imported templates (<see cref="TemplateSource.User"/> /
/// <see cref="TemplateSource.Imported"/>) in <c>user-templates.json</c>. These are the templates the
/// user may edit, duplicate, and delete; built-in templates are code-baked and never stored here.
/// </summary>
public interface IUserTemplateStore
{
    /// <summary>All stored user/imported templates, most recently changed last.</summary>
    IReadOnlyList<StackTemplate> Templates { get; }

    /// <summary>Raised whenever the stored set changes (add, update, or delete).</summary>
    event EventHandler? Changed;

    /// <summary>True when a template with this id is already stored (case-insensitive).</summary>
    bool Contains(string id);

    /// <summary>Returns the stored template with this id, or null.</summary>
    StackTemplate? Get(string id);

    /// <summary>Inserts or replaces (by <see cref="StackTemplate.Id"/>) a user/imported template.</summary>
    void Save(StackTemplate template);

    /// <summary>Inserts or replaces several templates at once (used by Import), persisting once.</summary>
    void SaveRange(IEnumerable<StackTemplate> templates);

    /// <summary>Removes a stored template. Returns true if one was removed.</summary>
    bool Delete(string id);
}
