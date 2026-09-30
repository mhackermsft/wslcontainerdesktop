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
/// Conversation facade used by the assistant UI. It sends user text to the configured AI provider,
/// pauses for approval when a tool may change state, and returns a safe transcript result.
/// </summary>
public interface IContainerAssistant
{
    /// <summary>Raised when the assistant is waiting for, or no longer waiting for, a tool approval.</summary>
    event EventHandler<AssistantApprovalRequest?>? ApprovalChanged;

    /// <summary>Sends a user message without progress callbacks.</summary>
    Task<AssistantTurnResult> SendAsync(string userMessage, CancellationToken ct = default);

    /// <summary>Sends a user message and reports streaming/progress updates to the caller.</summary>
    Task<AssistantTurnResult> SendAsync(string userMessage, Action<AiChatProgress> progress, CancellationToken ct = default)
        => SendAsync(userMessage, ct);

    /// <summary>Continues the paused conversation by allowing the requested action.</summary>
    Task<AssistantTurnResult> ApproveAsync(AssistantApprovalRequest approval, CancellationToken ct = default);

    /// <summary>Continues the paused conversation by denying the requested action.</summary>
    Task<AssistantTurnResult> RejectAsync(AssistantApprovalRequest approval, CancellationToken ct = default);

    /// <summary>Clears conversation state and any pending approval.</summary>
    void Reset();
}
