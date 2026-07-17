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
/// A dedicated <see cref="HttpClient"/> for AI provider calls. Chat and diagnosis requests can
/// take far longer than the app's general-purpose 20s client — a local Ollama model may need to
/// cold-load and then generate — so this uses a generous timeout. It is a distinct type purely so
/// DI can hand the AI providers this long-timeout client without changing the shared client used
/// by registry/image-update/WSL calls (which should fail fast).
/// </summary>
public sealed class AiHttpClient : HttpClient
{
    public AiHttpClient()
    {
        Timeout = TimeSpan.FromMinutes(5);
    }
}
