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
    /// <summary>Represents the engine option.</summary>
    Engine,
    /// <summary>Represents the container option.</summary>
    Container,
    /// <summary>Represents the image option.</summary>
    Image,
    /// <summary>Represents the network option.</summary>
    Network,
    /// <summary>Represents the assistant option.</summary>
    Assistant,
}

/// <summary>The specific thing that happened, used to pick a glyph and phrasing.</summary>
public enum ActivityKind
{
    /// <summary>Represents the engine up option.</summary>
    EngineUp,
    /// <summary>Represents the engine down option.</summary>
    EngineDown,
    /// <summary>Represents the container created option.</summary>
    ContainerCreated,
    /// <summary>Represents the container started option.</summary>
    ContainerStarted,
    /// <summary>Represents the container stopped option.</summary>
    ContainerStopped,
    /// <summary>Represents the container removed option.</summary>
    ContainerRemoved,
    /// <summary>Represents the network created option.</summary>
    NetworkCreated,
    /// <summary>Represents the network connected option.</summary>
    NetworkConnected,
    /// <summary>Represents the network disconnected option.</summary>
    NetworkDisconnected,
    /// <summary>Represents the network removed option.</summary>
    NetworkRemoved,
    /// <summary>Represents the image pulled option.</summary>
    ImagePulled,
    /// <summary>Represents the image built option.</summary>
    ImageBuilt,
    /// <summary>Represents the assistant tool invoked option.</summary>
    AssistantToolInvoked,
    /// <summary>Represents the assistant approval approved option.</summary>
    AssistantApprovalApproved,
    /// <summary>Represents the assistant approval rejected option.</summary>
    AssistantApprovalRejected,
}

/// <summary>
/// A single entry in the activity feed. Events are synthesized from engine snapshot diffs
/// (container lifecycle + engine up/down) and from image pull/build outcomes. Instances are
/// persisted to JSON so the timeline survives app restarts.
/// </summary>
public sealed class ActivityEvent
{
    /// <summary>
    /// Returns the event title. List controls use this as each row's screen-reader name;
    /// without it Narrator announces the .NET type name instead.
    /// </summary>
    public override string ToString() => Title;

    /// <summary>Gets or sets the id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Gets or sets the category.</summary>
    public ActivityCategory Category { get; init; }

    /// <summary>Gets or sets the kind.</summary>
    public ActivityKind Kind { get; init; }

    /// <summary>Primary line, e.g. "nginx started" or "Pulled alpine:latest".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Optional secondary line, e.g. a short id or an error message.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets or sets the source event key.</summary>
    public string? SourceEventKey { get; init; }

    /// <summary>Gets or sets the source type.</summary>
    public string? SourceType { get; init; }

    /// <summary>Gets or sets the source action.</summary>
    public string? SourceAction { get; init; }

    /// <summary>Gets or sets the actor id.</summary>
    public string? ActorId { get; init; }

    /// <summary>Gets or sets the container id.</summary>
    public string? ContainerId { get; init; }

    /// <summary>Gets or sets the attributes.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

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
        ActivityKind.NetworkCreated => "\uE968",
        ActivityKind.NetworkConnected => "\uE839",
        ActivityKind.NetworkDisconnected => "\uE711",
        ActivityKind.NetworkRemoved => "\uE74D",
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
        ActivityCategory.Network => "Network",
        ActivityCategory.Assistant => "AI assistant",
        _ => "Event",
    };

    /// <summary>Absolute local date and time shown on the row (e.g. "Jul 15, 2026 2:00:21 PM").</summary>
    [JsonIgnore]
    public string TimestampLabel => Timestamp.ToLocalTime().ToString("MMM d, yyyy h:mm:ss tt");

    /// <summary>
    /// Where an event with <paramref name="timestamp"/> belongs in a newest-first list: after every
    /// strictly newer entry, so events that arrive late (back-filled history, a replay after the
    /// live stream reconnects) still land in time order, and equal times keep arrival order.
    /// </summary>
    public static int NewestFirstInsertIndex(IReadOnlyList<ActivityEvent> events, DateTimeOffset timestamp)
    {
        var index = 0;
        while (index < events.Count && events[index].Timestamp > timestamp)
        {
            index++;
        }

        return index;
    }
}
