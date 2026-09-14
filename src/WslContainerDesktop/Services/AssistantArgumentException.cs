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
/// The model called a tool with arguments its schema does not accept.
///
/// Derives from <see cref="InvalidOperationException"/> so existing catch filters still contain it
/// as an ordinary tool failure, while remaining distinguishable for presentation: this is the
/// assistant getting a call shape wrong, not the user misconfiguring anything, and telling them to
/// check their configuration would send them looking for a problem that does not exist.
/// </summary>
public sealed class AssistantArgumentException : InvalidOperationException
{
    public AssistantArgumentException(string message) : base(message)
    {
    }

    public AssistantArgumentException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
