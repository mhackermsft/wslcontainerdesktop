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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>One remembered container or an explicitly enrolled automatic-start session.</summary>
public sealed record AutostartEntry
{
    /// <summary>Container ID, when known.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Container name used to identify the saved entry in the UI.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Explicit session ownership; null means a legacy entry needs review.</summary>
    public AutomationSessionOwner? SessionOwner { get; init; }
    /// <summary>True for a session enrollment marker rather than a container.</summary>
    public bool TracksSession { get; init; }
    /// <summary>Unknown fields are retained when a compatible state file is rewritten.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}
