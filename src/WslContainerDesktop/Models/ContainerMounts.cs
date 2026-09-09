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

using System.Text.Json;

namespace WslContainerDesktop.Models;

/// <summary>
/// A schema-adaptive inspect snapshot. IsComplete describes volume-usage metadata, not whether
/// every mount can be recreated. Missing metadata is not an empty mount list.
/// </summary>
public sealed record ContainerMounts(
    IReadOnlyList<ContainerMount> Items, bool IsComplete, IReadOnlyList<string> Warnings)
{
    public static ContainerMounts Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return new([], false, ["Container mount metadata is not valid JSON."]);
        }
    }

    public static ContainerMounts Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
        {
            root = root[0];
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array)
        {
            return new([], false, ["Container mount metadata is unavailable; storage configuration and usage may be incomplete."]);
        }

        var items = new List<ContainerMount>();
        var warnings = new List<string>();
        foreach (var element in mounts.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("An invalid mount entry could not be read.");
                continue;
            }

            var mount = new ContainerMount(
                Text(element, "Type") ?? string.Empty,
                Text(element, "Name"), Text(element, "Source"), Text(element, "Destination"),
                ReadMode(element), Boolean(element, "IsAnonymous") ?? Boolean(element, "Anonymous"));
            items.Add(mount);
            if (string.IsNullOrWhiteSpace(mount.Destination) ||
                !(mount.Type.Equals("bind", StringComparison.OrdinalIgnoreCase) ||
                  mount.Type.Equals("tmpfs", StringComparison.OrdinalIgnoreCase) ||
                  mount.Type.Equals("volume", StringComparison.OrdinalIgnoreCase) && mount.VolumeName is not null))
            {
                warnings.Add("A mount has incomplete or unsupported type, source, or destination metadata.");
            }
        }

        return new(items, warnings.Count == 0, warnings);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static bool? ReadMode(JsonElement element)
    {
        bool? mode = null;
        foreach (var property in new[] { "ReadOnly", "ReadWrite", "RW" })
        {
            if (!element.TryGetProperty(property, out _))
            {
                continue;
            }

            var value = Boolean(element, property);
            if (value is null)
            {
                return null;
            }

            var readOnly = property == "ReadOnly" ? value.Value : !value.Value;
            if (mode is not null && mode != readOnly)
            {
                return null;
            }

            mode = readOnly;
        }

        return mode;
    }
}
