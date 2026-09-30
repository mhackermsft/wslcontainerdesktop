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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using Xunit;

namespace WslContainerDesktop.Tests.Runtime;

/// <summary>Marks tests that may touch a disposable <c>wslc</c> engine and skips them unless Windows and explicit runtime consent are present.</summary>
public sealed class WslcRuntimeFactAttribute : FactAttribute
{
    public const string Consent = "I-authorize-disposable-WSLC-resources";

    // Separate from WCD_COMPOSE_RUNTIME so opting into this suite never enables the Compose
    // conformance harness, which needs its own configuration and an empty disposable session.
    public const string EnvironmentVariable = "WCD_WSLC_RUNTIME";

    public WslcRuntimeFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "WSLC runtime tests require Windows.";
            return;
        }

        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != Consent)
        {
            Skip = "Explicit disposable-engine permission required; no engine probe performed.";
        }
    }
}
