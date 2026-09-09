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

namespace WslContainerDesktop.Models;

public sealed class AiChatMessage
{
    public string Role { get; init; } = "user";

    public string? Content { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
}

public sealed class AiToolDefinition
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string JsonSchemaParameters { get; init; }
}

public sealed class AiToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string ArgumentsJson { get; init; } = "{}";
}

public sealed class AiToolTurn
{
    public string? AssistantText { get; init; }

    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
}

public sealed record AiChatConfiguration(AiProviderKind Kind, string Endpoint, string Model);

public sealed record AiChatRequest(
    AiChatConfiguration Configuration,
    IReadOnlyList<AiChatMessage> History)
{
    // Synchronous delivery preserves ordering. TextDelta contains sanitized safe segments,
    // not raw transport fragments. Tool arguments remain inside the provider adapter.
    public Action<AiChatProgress>? Progress { get; init; }
}

public enum AiChatProgressKind
{
    Loading,
    Generating,
    TextDelta,
    ToolRequested,
    AwaitingApproval,
    ExecutingTool,
    ToolResult,
    Completed,
    Failed,
    Cancelled,
}

// TextDelta appends model narration; ToolResult replaces evidence for the same call ID.
// Only the assistant service publishes approval, execution and terminal events.
// ToolCallId is a turn-local display correlation ID, not the provider's raw protocol ID.
public sealed record AiChatProgress(AiChatProgressKind Kind, string Text, string? ToolCallId = null);

public sealed record AiChatTurnResult(string FinalText, IReadOnlyList<AiChatMessage> Messages);
