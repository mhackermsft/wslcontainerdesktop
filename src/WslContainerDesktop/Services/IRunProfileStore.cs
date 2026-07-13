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
/// Persists named run profiles (reusable image run configurations) alongside the app's other
/// settings. Storage is deliberately a small, standalone JSON file so the same profiles can be
/// reused by other features (e.g. a health watchdog) without loading the whole settings object.
/// </summary>
public interface IRunProfileStore
{
    /// <summary>All saved profiles, ordered by name.</summary>
    IReadOnlyList<RunProfile> GetAll();

    /// <summary>Saved profiles whose image reference matches <paramref name="image"/> (ordered by name).</summary>
    IReadOnlyList<RunProfile> GetForImage(string image);

    /// <summary>The profile with the given name, or null if none exists (case-insensitive).</summary>
    RunProfile? Get(string name);

    /// <summary>
    /// Adds or replaces a profile (matched by name, case-insensitive) and persists the change.
    /// </summary>
    void Save(RunProfile profile);

    /// <summary>Removes the profile with the given name (case-insensitive) and persists the change.</summary>
    void Delete(string name);
}
