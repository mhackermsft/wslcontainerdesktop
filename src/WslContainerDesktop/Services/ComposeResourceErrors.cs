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
/// Recognizes "this resource does not exist" from an inspect failure.
///
/// A missing resource is the normal case when a project is deployed for the first time, so it must
/// be told apart from an engine that could not answer. WSLC reports it as plain text
/// (<c>Network not found: 'name'</c>); matching only a <c>WSLC_E_*</c> sentinel treats every
/// first-time deployment as unreadable inventory and blocks it.
/// </summary>
internal static class ComposeResourceErrors
{
    /// <summary>Returns true when a network inspect error means the network is absent.</summary>
    public static bool IsNetworkNotFound(string? errorText) =>
        Matches(errorText, "WSLC_E_NETWORK_NOT_FOUND", "network not found");

    /// <summary>Returns true when a volume inspect error means the volume is absent.</summary>
    public static bool IsVolumeNotFound(string? errorText) =>
        Matches(errorText, "WSLC_E_VOLUME_NOT_FOUND", "volume not found");

    /// <summary>
    /// Returns true when a container inspect error means the container is absent. WSL 3.0.1 reports
    /// this as <c>Object not found: name</c> (plain <c>inspect</c>) or <c>Container 'name' not found.</c>
    /// (<c>container inspect</c>) rather than a <c>WSLC_E_*</c> code.
    /// </summary>
    public static bool IsContainerNotFound(string? errorText) =>
        Matches(errorText, "WSLC_E_CONTAINER_NOT_FOUND", "object not found")
        || (errorText is not null
            && errorText.Contains("container '", StringComparison.OrdinalIgnoreCase)
            && errorText.Contains("' not found", StringComparison.OrdinalIgnoreCase));

    /// <summary>Dispatches not-found matching for the compose resource kind currently being inspected.</summary>
    public static bool IsNotFound(string kind, string? errorText) =>
        kind == "network" ? IsNetworkNotFound(errorText) : IsVolumeNotFound(errorText);

    private static bool Matches(string? errorText, string sentinel, string message)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;
        // The sentinel stays authoritative where the engine emits it; the message form is matched
        // case-insensitively because it is human-facing text, not a stable contract.
        return errorText.Contains(sentinel, StringComparison.Ordinal)
            || errorText.Contains(message, StringComparison.OrdinalIgnoreCase);
    }
}
