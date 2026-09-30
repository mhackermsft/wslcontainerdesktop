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

/// <summary>Model object that stores ai chat message information used by services, view models, or dialogs.</summary>
public sealed class AiChatMessage
{
    /// <summary>Gets or sets the role.</summary>
    public string Role { get; init; } = "user";

    /// <summary>Gets or sets the content.</summary>
    public string? Content { get; init; }

    /// <summary>Gets or sets the tool call id.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Gets or sets the tool name.</summary>
    public string? ToolName { get; init; }

    /// <summary>Gets or sets the tool calls.</summary>
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
}

/// <summary>Model object that stores ai tool definition information used by services, view models, or dialogs.</summary>
public sealed class AiToolDefinition
{
    /// <summary>Gets or sets the name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets or sets the description.</summary>
    public required string Description { get; init; }

    /// <summary>Gets or sets the json schema parameters.</summary>
    public required string JsonSchemaParameters { get; init; }
}

/// <summary>Model object that stores ai tool call information used by services, view models, or dialogs.</summary>
public sealed class AiToolCall
{
    /// <summary>Gets or sets the id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets or sets the arguments json.</summary>
    public string ArgumentsJson { get; init; } = "{}";
}

/// <summary>Model object that stores ai tool turn information used by services, view models, or dialogs.</summary>
public sealed class AiToolTurn
{
    /// <summary>Gets or sets the assistant text.</summary>
    public string? AssistantText { get; init; }

    /// <summary>Gets or sets the tool calls.</summary>
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = Array.Empty<AiToolCall>();
}

/// <summary>Immutable or init-only data model that carries ai chat configuration information between services and view models.</summary>
public sealed record AiChatConfiguration(AiProviderKind Kind, string Endpoint, string Model);

/// <summary>Immutable or init-only data model that carries ai chat request information between services and view models.</summary>
public sealed record AiChatRequest(
    AiChatConfiguration Configuration,
    IReadOnlyList<AiChatMessage> History)
{
    // Synchronous delivery preserves ordering. TextDelta contains sanitized safe segments,
    // not raw transport fragments. Tool arguments remain inside the provider adapter.
    /// <summary>Gets or sets the progress.</summary>
    public Action<AiChatProgress>? Progress { get; init; }
}

/// <summary>Values that describe ai chat progress kind states or choices in WSL Container Desktop workflows.</summary>
public enum AiChatProgressKind
{
    /// <summary>Represents the loading option.</summary>
    Loading,
    /// <summary>Represents the generating option.</summary>
    Generating,
    /// <summary>Represents the text delta option.</summary>
    TextDelta,
    /// <summary>Represents the tool requested option.</summary>
    ToolRequested,
    /// <summary>Represents the awaiting approval option.</summary>
    AwaitingApproval,
    /// <summary>Represents the executing tool option.</summary>
    ExecutingTool,
    /// <summary>Represents the tool result option.</summary>
    ToolResult,
    /// <summary>Represents the completed option.</summary>
    Completed,
    /// <summary>Represents the failed option.</summary>
    Failed,
    /// <summary>Represents the cancelled option.</summary>
    Cancelled,
}

// TextDelta appends model narration; ToolResult replaces evidence for the same call ID.
// Only the assistant service publishes approval, execution and terminal events.
// ToolCallId is a turn-local display correlation ID, not the provider's raw protocol ID.
/// <summary>Immutable or init-only data model that carries ai chat progress information between services and view models.</summary>
public sealed record AiChatProgress(AiChatProgressKind Kind, string Text, string? ToolCallId = null);

/// <summary>Immutable or init-only data model that carries ai chat turn result information between services and view models.</summary>
public sealed record AiChatTurnResult(string FinalText, IReadOnlyList<AiChatMessage> Messages);
