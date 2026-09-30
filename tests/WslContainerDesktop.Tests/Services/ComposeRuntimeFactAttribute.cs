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

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Opt-in xUnit fact for tests that may create disposable Compose resources in a real <c>wslc</c> engine.
/// </summary>
public sealed class ComposeRuntimeFactAttribute : FactAttribute
{
    /// <summary>
    /// Environment variable value required to prove the developer intentionally enabled real-engine Compose tests.
    /// </summary>
    public const string Consent = "I-authorize-disposable-WSLC-resources";

    /// <summary>
    /// Skips the test unless the opt-in environment variable matches <see cref="Consent"/> exactly.
    /// </summary>
    public ComposeRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WCD_COMPOSE_RUNTIME") != Consent)
            Skip = "Explicit disposable-engine permission required; no engine probe performed.";
    }
}
