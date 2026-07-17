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
