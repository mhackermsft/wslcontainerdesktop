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

/// <summary>A single text event emitted by <c>wslc events</c>.</summary>
public sealed class EngineEvent
{
    /// <summary>Gets or sets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets or sets the type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets or sets the action.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Gets or sets the actor id.</summary>
    public string ActorId { get; init; } = string.Empty;

    /// <summary>Gets or sets the attributes.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the stable key.</summary>
    public string StableKey =>
        $"{Timestamp:O}|{Type}|{Action}|{ActorId}|{string.Join('\u001f', Attributes.Select(kvp => kvp.Key + "=" + kvp.Value))}";

    /// <summary>Converts model data for to unix time seconds scenarios.</summary>
    /// <returns>The requested value for the caller.</returns>
    public long UnixSeconds => Timestamp.ToUnixTimeSeconds();

    /// <summary>Gets the display name.</summary>
    public string DisplayName =>
        Attribute("name") ??
        Attribute("container") ??
        (ActorId.Length > 12 ? ActorId[..12] : ActorId);

    /// <summary>Gets the container id.</summary>
    public string? ContainerId =>
        IsType("container") ? ActorId :
        Attributes.TryGetValue("container", out var container) && !string.IsNullOrWhiteSpace(container) ? container : null;

    /// <summary>Gets the image.</summary>
    public string? Image => Attribute("image");

    /// <summary>Gets the network.</summary>
    public string? Network => IsType("network") ? Attribute("name") ?? ActorId : Attribute("network");

    /// <summary>Gets the parsed process exit code from the event attributes, when one was reported.</summary>
    public int? ExitCode
    {
        get
        {
            if (Attributes.TryGetValue("exitCode", out var value) &&
                int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var code))
            {
                return code;
            }

            return null;
        }
    }

    /// <summary>Checks whether this value is type.</summary>
    /// <param name="type">The type value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public bool IsType(string type) => string.Equals(Type, type, StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks whether this value is action.</summary>
    /// <param name="actions">The actions value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public bool IsAction(params string[] actions) =>
        actions.Any(action => string.Equals(Action, action, StringComparison.OrdinalIgnoreCase));

    /// <summary>Performs the attribute helper used by this model or dialog.</summary>
    /// <param name="key">The key value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public string? Attribute(string key) =>
        Attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
