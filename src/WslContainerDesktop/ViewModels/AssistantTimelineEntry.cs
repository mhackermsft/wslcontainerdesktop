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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.ViewModels;

/// <param name="Label">Row heading; for activity rows this is the collapsed summary line.</param>
/// <param name="Text">Full detail, shown inline for conversation and on expand for activity.</param>
public sealed record AssistantTimelineEntry(
    int Generation, AiChatProgressKind? Kind, string? ToolCallId, string Label, string Text)
{
    /// <summary>
    /// Tool machinery rather than conversation. Raw container ids and JSON evidence are proof of
    /// what happened, but they are not something the user is reading, so these rows collapse to
    /// their heading and open only when someone wants to check.
    /// </summary>
    public bool IsActivity => Kind is AiChatProgressKind.ToolRequested
        or AiChatProgressKind.AwaitingApproval
        or AiChatProgressKind.ExecutingTool
        or AiChatProgressKind.ToolResult;

    /// <summary>Conversation rows stay fully visible: they are what the user came to read.</summary>
    public bool IsConversation => !IsActivity;

    /// <summary>True when expanding would actually reveal something.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Text);
}
