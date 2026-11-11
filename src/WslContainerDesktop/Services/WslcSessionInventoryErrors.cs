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

namespace WslContainerDesktop.Services;

/// <summary>Stable error kinds whose app-authored wording can be refreshed when the language changes.</summary>
internal static class WslcSessionInventoryErrors
{
    internal enum Kind { InvalidSessions, AmbiguousSessions, ExecutableChanged, DiscoveryTimedOut, NoActiveSessions }

    internal sealed class InventoryException(Kind kind) : InvalidOperationException(Describe(kind))
    {
        internal Kind Reason { get; } = kind;
    }

    internal sealed class SystemInfoException() : JsonException(Describe(Kind.InvalidSessions)) { }

    internal static string Describe(Kind kind) => kind switch
    {
        Kind.InvalidSessions => UiText.Get("WslcSystemInfo_InvalidSessions",
            "WSLC system information did not contain a valid session list."),
        Kind.AmbiguousSessions => UiText.Get("WslcSessionInventory_InvalidSessions",
            "WSLC returned invalid or ambiguous active session identities."),
        Kind.ExecutableChanged => UiText.Get("WslcSessionInventory_ExecutableChanged",
            "The WSLC executable changed while session inventory was being read."),
        Kind.DiscoveryTimedOut => UiText.Get("WslcSessionInventory_DiscoveryTimedOut",
            "WSLC session discovery timed out."),
        Kind.NoActiveSessions => UiText.Get("SessionPicker_NoActive",
            "There are no active sessions. Start a session in WSL engine, then try again."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string Describe(Exception error) => error switch
    {
        InventoryException inventory => Describe(inventory.Reason),
        SystemInfoException => Describe(Kind.InvalidSessions),
        OperationCanceledException => UiText.Get("CombinedSession_UnexpectedCancellation",
            "The engine cancelled the request unexpectedly."),
        _ => error.Message,
    };
}
