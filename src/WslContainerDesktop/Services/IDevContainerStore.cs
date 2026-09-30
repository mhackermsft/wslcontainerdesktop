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

/// <summary>Persists Dev Container definitions imported by the app so they are available after restart.</summary>
public interface IDevContainerStore
{
    /// <summary>Returns all saved Dev Container configurations.</summary>
    IReadOnlyList<DevContainerConfig> GetAll();
    /// <summary>Finds a saved configuration by id, or null when it is unknown.</summary>
    DevContainerConfig? Get(string id);
    /// <summary>Adds or replaces one Dev Container configuration and persists it.</summary>
    void Save(DevContainerConfig config);
    /// <summary>Removes a saved configuration by id and persists the change.</summary>
    void Delete(string id);
}
