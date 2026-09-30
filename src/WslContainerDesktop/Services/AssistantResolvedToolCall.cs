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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Describes an assistant tool request after the app has mapped it to a concrete operation.
/// </summary>
public sealed record AssistantResolvedToolCall(
    AiToolCall Call,
    AssistantPermissionCategory Category,
    string Summary,
    string Details,
    Func<CancellationToken, Task<string>> ExecuteAsync)
{
    /// <summary>
    /// Gets whether this assistant tool call must be approved before it can run.
    /// </summary>
    public bool RequiresExplicitApproval { get; init; }
    /// <summary>
    /// Gets the result text returned when the tool call is blocked before execution.
    /// </summary>
    public string? BlockedResult { get; init; }
    /// <summary>
    /// Gets the callback that runs when the user declines the assistant tool request.
    /// </summary>
    public Func<Task<string>>? DeclineAsync { get; init; }
}
