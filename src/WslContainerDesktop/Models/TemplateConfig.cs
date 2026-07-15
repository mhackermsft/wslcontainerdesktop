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

/// <summary>
/// A user's saved configuration for a gallery template, keyed by <see cref="StackTemplate.Id"/>.
/// Once a template is configured via its Settings button, its Launch button reuses this instead of
/// the catalog defaults. Persisted as JSON (see <c>TemplateConfigStore</c>).
/// </summary>
public sealed class TemplateConfig
{
    /// <summary>The <see cref="StackTemplate.Id"/> this configuration belongs to.</summary>
    public required string TemplateId { get; set; }

    /// <summary>Saved run options for single-container templates.</summary>
    public RunContainerOptions? RunOptions { get; set; }

    /// <summary>Saved (possibly edited) compose YAML for compose templates.</summary>
    public string? ComposeYaml { get; set; }

    /// <summary>Saved project name for compose templates.</summary>
    public string? ComposeProjectName { get; set; }
}
