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

using System.Text.Json.Serialization;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Models;

/// <summary>Persisted engine and session explicitly chosen for an automatic operation.</summary>
public sealed record AutomationSessionOwner
{
    /// <summary>The executable selected when ownership was assigned.</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>The exact named session, or null for the Default CLI session.</summary>
    public string? SessionName { get; init; }

    /// <summary>Whether the stored identity can be used without guessing.</summary>
    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(ExecutablePath) &&
        (SessionName is null || !string.IsNullOrWhiteSpace(SessionName));

    /// <summary>Captures the explicitly selected destination.</summary>
    public static AutomationSessionOwner Capture(string executablePath, WslcSessionTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(target);
        return new AutomationSessionOwner { ExecutablePath = executablePath, SessionName = target.Name };
    }

    /// <summary>Matches Windows executable paths and case-sensitive WSLC session names.</summary>
    public bool Matches(string executablePath, WslcSessionTarget target) =>
        IsValid && string.Equals(ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SessionName, target.Name, StringComparison.Ordinal);
}
