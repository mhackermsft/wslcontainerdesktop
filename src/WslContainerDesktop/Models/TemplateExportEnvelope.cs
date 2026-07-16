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
/// The on-disk envelope for exported templates (a <c>.wsltmpl</c> file). Versioned so future schema
/// changes can be detected on import.
/// </summary>
public sealed class TemplateExportEnvelope
{
    /// <summary>Envelope schema version; bumped when the shape changes incompatibly.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>When the file was produced, for the user's reference.</summary>
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The exported templates.</summary>
    public List<StackTemplate> Templates { get; set; } = new();
}
