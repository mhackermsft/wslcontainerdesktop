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
/// Persists per-template configurations (see <see cref="TemplateConfig"/>) so a template's Launch
/// button can reuse the user's most recent Settings choices instead of the catalog defaults.
/// </summary>
public interface ITemplateConfigStore
{
    /// <summary>Returns the saved configuration for a template, or null if it was never configured.</summary>
    TemplateConfig? Get(string templateId);

    /// <summary>Inserts or replaces (by <see cref="TemplateConfig.TemplateId"/>) a saved configuration.</summary>
    void Save(TemplateConfig config);

    /// <summary>Removes a saved configuration, reverting the template to its catalog defaults.</summary>
    void Delete(string templateId);
}
