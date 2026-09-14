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
/// The assistant kept calling tools until it hit the per-turn iteration limit, usually by retrying
/// a call that keeps reporting the same failure.
///
/// Derives from <see cref="InvalidOperationException"/> so existing catch filters still treat it as
/// an ordinary turn failure, while remaining distinguishable for presentation: the limit working as
/// designed is not a configuration problem, and saying so sends the user looking for a setting that
/// will not help.
/// </summary>
public sealed class AssistantIterationLimitException(string message) : InvalidOperationException(message);
