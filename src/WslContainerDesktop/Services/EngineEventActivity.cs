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
/// Translates raw <c>wslc events</c> into Activity timeline entries. Handles the event shapes of both
/// WSL 3.0.1 (an exit is a single <c>stop</c> carrying <c>exitCode</c>) and WSL 3.0.2+ (an exit is a
/// <c>die</c> carrying <c>exitCode</c>, followed by an exit-code-less <c>stop</c> for user stops, plus
/// <c>health_status: &lt;state&gt;</c> events).
/// </summary>
public static class EngineEventActivity
{
    // 3.0.2 stamps die and stop with the same time; allow slack for clock rounding and replays.
    private static readonly TimeSpan StopDieWindow = TimeSpan.FromSeconds(2);

    /// <summary>Compares event ownership without conflating sessions or executable paths.</summary>
    internal static bool HasSameOwner(ActivityEvent entry, AutomationSessionOwner? owner) =>
        owner is null ? entry.SessionOwner is null :
        owner.IsValid && entry.SessionOwner?.Matches(owner.ExecutablePath,
            new WslcSessionTarget(owner.SessionName)) == true;

    /// <summary>Maps an engine event to the Activity entry shown on the timeline.</summary>
    public static ActivityEvent ToActivityEvent(EngineEvent evt, AutomationSessionOwner? owner = null)
    {
        var category = evt.Type.ToLowerInvariant() switch
        {
            "container" => ActivityCategory.Container,
            "image" => ActivityCategory.Image,
            "network" => ActivityCategory.Network,
            _ => ActivityCategory.Engine,
        };
        var health = evt.HealthStatus;
        var kind = health is not null
            ? ActivityKind.ContainerHealth
            : (category, evt.Action.ToLowerInvariant()) switch
            {
                (ActivityCategory.Container, "create") => ActivityKind.ContainerCreated,
                (ActivityCategory.Container, "start") => ActivityKind.ContainerStarted,
                (ActivityCategory.Container, "stop" or "die" or "kill") => ActivityKind.ContainerStopped,
                (ActivityCategory.Container, "destroy" or "remove") => ActivityKind.ContainerRemoved,
                (ActivityCategory.Network, "create") => ActivityKind.NetworkCreated,
                (ActivityCategory.Network, "connect") => ActivityKind.NetworkConnected,
                (ActivityCategory.Network, "disconnect") => ActivityKind.NetworkDisconnected,
                (ActivityCategory.Network, "destroy" or "remove") => ActivityKind.NetworkRemoved,
                (ActivityCategory.Image, _) => ActivityKind.ImagePulled,
                _ => ActivityKind.EngineUp,
            };

        var attrs = evt.Attributes.Count == 0
            ? null
            : string.Join(", ", evt.Attributes
                .Where(kvp => kvp.Key is "image" or "exitCode" or "network" or "name" or "container" or "type")
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var name = evt.DisplayName;
        return new ActivityEvent
        {
            Timestamp = evt.Timestamp,
            Category = category,
            Kind = kind,
            Title = health is null ? $"{evt.Type} {evt.Action}: {name}" : $"{name} is {health}",
            Detail = string.IsNullOrWhiteSpace(attrs) ? evt.ActorId : attrs,
            IsError = evt.ExitCode is > 0 || string.Equals(health, "unhealthy", StringComparison.OrdinalIgnoreCase),
            SourceEventKey = evt.StableKey,
            SourceType = evt.Type,
            SourceAction = evt.Action,
            ActorId = evt.ActorId,
            ContainerId = evt.ContainerId,
            SessionOwner = owner,
            Attributes = evt.Attributes,
        };
    }

    /// <summary>
    /// True when <paramref name="evt"/> is a WSL 3.0.2+ <c>stop</c> without an exit code that repeats a
    /// <c>die</c> already on the timeline for the same container. 3.0.1 stops carry an exit code and are
    /// never treated as duplicates.
    /// </summary>
    public static bool IsRedundantStop(EngineEvent evt, IEnumerable<ActivityEvent> existing) =>
        IsBareStop(evt) &&
        existing.Any(e => IsSourceAction(e, "die") && SameContainerNearby(e, evt.ActorId, evt.Timestamp));

    /// <summary>
    /// For a <c>die</c> that arrives after its matching exit-code-less <c>stop</c>, returns that stop entry
    /// so it can be replaced by the more informative <c>die</c>; otherwise <see langword="null"/>.
    /// </summary>
    public static ActivityEvent? FindSupersededStop(EngineEvent evt, IEnumerable<ActivityEvent> existing)
    {
        if (!evt.IsType("container") || !evt.IsAction("die"))
        {
            return null;
        }

        return existing.FirstOrDefault(e =>
            IsSourceAction(e, "stop") &&
            !(e.Attributes?.ContainsKey("exitCode") ?? false) &&
            SameContainerNearby(e, evt.ActorId, evt.Timestamp));
    }

    private static bool IsBareStop(EngineEvent evt) =>
        evt.IsType("container") && evt.IsAction("stop") && !evt.Attributes.ContainsKey("exitCode");

    private static bool IsSourceAction(ActivityEvent e, string action) =>
        string.Equals(e.SourceType, "container", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.SourceAction, action, StringComparison.OrdinalIgnoreCase);

    private static bool SameContainerNearby(ActivityEvent e, string actorId, DateTimeOffset timestamp) =>
        !string.IsNullOrEmpty(actorId) &&
        string.Equals(e.ActorId, actorId, StringComparison.Ordinal) &&
        (e.Timestamp - timestamp).Duration() <= StopDieWindow;
}
