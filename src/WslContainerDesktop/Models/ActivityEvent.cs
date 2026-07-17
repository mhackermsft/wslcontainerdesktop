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

using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>High-level grouping used by the activity feed for filtering and iconography.</summary>
public enum ActivityCategory
{
    Engine,
    Container,
    Image,
    Assistant,
}

/// <summary>The specific thing that happened, used to pick a glyph and phrasing.</summary>
public enum ActivityKind
{
    EngineUp,
    EngineDown,
    ContainerCreated,
    ContainerStarted,
    ContainerStopped,
    ContainerRemoved,
    ImagePulled,
    ImageBuilt,
    AssistantToolInvoked,
    AssistantApprovalApproved,
    AssistantApprovalRejected,
}

/// <summary>
/// A single entry in the activity feed. Events are synthesized from engine snapshot diffs
/// (container lifecycle + engine up/down) and from image pull/build outcomes. Instances are
/// persisted to JSON so the timeline survives app restarts.
/// </summary>
public sealed class ActivityEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public ActivityCategory Category { get; init; }

    public ActivityKind Kind { get; init; }

    /// <summary>Primary line, e.g. "nginx started" or "Pulled alpine:latest".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Optional secondary line, e.g. a short id or an error message.</summary>
    public string? Detail { get; init; }

    /// <summary>True for failures (pull/build errors), which render with an error accent.</summary>
    public bool IsError { get; init; }

    /// <summary>Segoe MDL2 glyph for the row icon.</summary>
    [JsonIgnore]
    public string Glyph => Kind switch
    {
        ActivityKind.EngineUp => "\uE73E",        // completed check
        ActivityKind.EngineDown => "\uEB90",      // error badge
        ActivityKind.ContainerCreated => "\uE710", // add
        ActivityKind.ContainerStarted => "\uE768", // play
        ActivityKind.ContainerStopped => "\uE71A", // stop
        ActivityKind.ContainerRemoved => "\uE74D", // delete
        ActivityKind.ImagePulled => "\uE896",      // download
        ActivityKind.ImageBuilt => "\uE9F9",       // build
        ActivityKind.AssistantToolInvoked => "\uE8D4", // robot
        ActivityKind.AssistantApprovalApproved => "\uE8FB", // accept
        ActivityKind.AssistantApprovalRejected => "\uE711", // cancel
        _ => "\uE9D9",
    };

    /// <summary>Short category label shown as a chip.</summary>
    [JsonIgnore]
    public string CategoryLabel => Category switch
    {
        ActivityCategory.Engine => "Engine",
        ActivityCategory.Container => "Container",
        ActivityCategory.Image => "Image",
        ActivityCategory.Assistant => "AI assistant",
        _ => "Event",
    };

    /// <summary>Absolute local date and time shown on the row (e.g. "Jul 15, 2026 2:00:21 PM").</summary>
    [JsonIgnore]
    public string TimestampLabel => Timestamp.ToLocalTime().ToString("MMM d, yyyy h:mm:ss tt");
}
