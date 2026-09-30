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

/// <summary>Values that describe assistant message role states or choices in WSL Container Desktop workflows.</summary>
public enum AssistantMessageRole
{
    /// <summary>Represents the user option.</summary>
    User,
    /// <summary>Represents the assistant option.</summary>
    Assistant,
    /// <summary>Represents the tool option.</summary>
    Tool,
    /// <summary>Represents the error option.</summary>
    Error,
}

/// <summary>Values that describe assistant permission category states or choices in WSL Container Desktop workflows.</summary>
public enum AssistantPermissionCategory
{
    /// <summary>Represents the read only option.</summary>
    ReadOnly,
    /// <summary>Represents the create run option.</summary>
    CreateRun,
    /// <summary>Represents the lifecycle option.</summary>
    Lifecycle,
    /// <summary>Represents the destructive option.</summary>
    Destructive,
    /// <summary>Represents the compose template option.</summary>
    ComposeTemplate,
    /// <summary>Represents the kubernetes option.</summary>
    Kubernetes,
    /// <summary>Represents the container exec option.</summary>
    ContainerExec,
}

/// <summary>Values that describe assistant action risk states or choices in WSL Container Desktop workflows.</summary>
public enum AssistantActionRisk
{
    /// <summary>Represents the read only option.</summary>
    ReadOnly,
    /// <summary>Represents the state changing option.</summary>
    StateChanging,
    /// <summary>Represents the high risk option.</summary>
    HighRisk,
}

/// <summary>Model object that stores assistant chat message information used by services, view models, or dialogs.</summary>
public sealed class AssistantChatMessage
{
    /// <summary>Gets or sets the role.</summary>
    public AssistantMessageRole Role { get; init; }

    /// <summary>Gets or sets the text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Gets the role label.</summary>
    public string RoleLabel => Role switch
    {
        AssistantMessageRole.User => "You",
        AssistantMessageRole.Tool => "Tool",
        AssistantMessageRole.Error => "Error",
        _ => "Assistant",
    };
}

/// <summary>Model object that stores assistant approval request information used by services, view models, or dialogs.</summary>
public sealed class AssistantApprovalRequest
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the tool name.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>Gets or sets the category.</summary>
    public AssistantPermissionCategory Category { get; init; }

    /// <summary>Gets or sets the risk.</summary>
    public AssistantActionRisk Risk { get; init; }

    /// <summary>Gets or sets the summary.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Gets or sets the details.</summary>
    public string Details { get; init; } = string.Empty;
}

/// <summary>Model object that stores assistant turn result information used by services, view models, or dialogs.</summary>
public sealed class AssistantTurnResult
{
    /// <summary>Gets or sets the messages.</summary>
    public List<AssistantChatMessage> Messages { get; init; } = new();

    /// <summary>Gets or sets the approval.</summary>
    public AssistantApprovalRequest? Approval { get; set; }
}
