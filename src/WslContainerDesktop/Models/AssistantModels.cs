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

public enum AssistantMessageRole
{
    User,
    Assistant,
    Tool,
    Error,
}

public enum AssistantPermissionCategory
{
    ReadOnly,
    CreateRun,
    Lifecycle,
    Destructive,
    ComposeTemplate,
    Kubernetes,
    ContainerExec,
}

public enum AssistantActionRisk
{
    ReadOnly,
    StateChanging,
    HighRisk,
}

public sealed class AssistantChatMessage
{
    public AssistantMessageRole Role { get; init; }

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string RoleLabel => Role switch
    {
        AssistantMessageRole.User => "You",
        AssistantMessageRole.Tool => "Tool",
        AssistantMessageRole.Error => "Error",
        _ => "Assistant",
    };
}

public sealed class AssistantApprovalRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string ToolName { get; init; } = string.Empty;

    public AssistantPermissionCategory Category { get; init; }

    public AssistantActionRisk Risk { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;
}

public sealed class AssistantTurnResult
{
    public List<AssistantChatMessage> Messages { get; init; } = new();

    public AssistantApprovalRequest? Approval { get; set; }
}
