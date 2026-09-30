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
/// Common contract for AI chat providers that can run one assistant turn and call back into app tools.
/// </summary>
public interface IAiChatProvider
{
    /// <summary>Provider identifier used by settings and diagnostics.</summary>
    AiProviderKind Kind { get; }

    /// <summary>
    /// Runs one chat turn using the supplied tool catalog and tool invocation callback.
    /// </summary>
    /// <param name="request">Messages, model and provider options for this turn.</param>
    /// <param name="tools">Tools the provider may call during the turn.</param>
    /// <param name="invokeToolAsync">Callback used when the model requests an app tool.</param>
    /// <param name="ct">Cancels provider and tool work.</param>
    /// <returns>The final assistant text, tool messages and model metadata.</returns>
    Task<AiChatTurnResult> RunTurnAsync(
        AiChatRequest request,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolCall, CancellationToken, Task<string>> invokeToolAsync,
        CancellationToken ct);
}
