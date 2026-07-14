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
/// Persists imported <see cref="ComposeProject"/> definitions (a named set of services with a
/// dependency graph) as a small standalone JSON file, so the app can bring a project up/down as a
/// unit and re-adopt it after a restart. Mirrors <see cref="IRunProfileStore"/>.
/// </summary>
public interface IComposeProjectStore
{
    /// <summary>All saved projects, ordered by name.</summary>
    IReadOnlyList<ComposeProject> GetAll();

    /// <summary>The project with the given name, or null if none exists (case-insensitive).</summary>
    ComposeProject? Get(string name);

    /// <summary>Adds or replaces a project (matched by name, case-insensitive) and persists it.</summary>
    void Save(ComposeProject project);

    /// <summary>Removes the project with the given name (case-insensitive) and persists the change.</summary>
    void Delete(string name);
}
