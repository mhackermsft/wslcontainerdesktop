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

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>Normalizes legacy numeric rows and the display-oriented WSLC 2.9.11 rows.</summary>
public sealed class ContainerInfoJsonConverter : JsonConverter<ContainerInfo>
{
    public override ContainerInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a container object.");

        var result = new ContainerInfo
        {
            Id = ReadString(root, "Id"),
            Name = ReadString(root, "Name"),
            Image = ReadString(root, "Image"),
        };
        if (string.IsNullOrWhiteSpace(result.Id))
            throw new JsonException("A container row has no Id.");

        if (string.IsNullOrEmpty(result.Name))
        {
            var names = Property(root, "Names");
            result.Name = names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? "" : throw new JsonException("Invalid container Names entry.")).FirstOrDefault() ?? ""
                : ReadString(root, "Names");
        }
        result.Name = result.Name.TrimStart('/');
        var state = Property(root, "State");
        result.StateValue = state.ValueKind switch
        {
            JsonValueKind.Number when state.TryGetInt32(out var number) => number,
            JsonValueKind.String => state.GetString()?.Trim().ToLowerInvariant() switch
            {
                "created" => (int)ContainerState.Created,
                "running" => (int)ContainerState.Running,
                "stopped" or "exited" => (int)ContainerState.Stopped,
                "paused" => (int)ContainerState.Paused,
                _ => (int)ContainerState.Unknown,
            },
            JsonValueKind.Null or JsonValueKind.Undefined => (int)ContainerState.Unknown,
            _ => throw new JsonException("Invalid container State."),
        };
        result.CreatedAtKnown = TryTimestamp(Property(root, "CreatedAt"), out var created);
        result.CreatedAt = created;
        result.StateChangedAtKnown = TryTimestamp(Property(root, "StateChangedAt"), out var changed) && changed > 0;
        result.StateChangedAt = result.StateChangedAtKnown ? (ulong)changed : 0;
        var ports = Property(root, "Ports");
        if (ports.ValueKind == JsonValueKind.Array)
        {
            result.Ports = ContainerPortParser.ReadStructured(ports, options);
            result.PortsKnown = true;
        }
        else if (ports.ValueKind == JsonValueKind.String)
        {
            result.PortsKnown = ContainerPortParser.TryDisplay(ports.GetString()!, out var mappings);
            result.Ports = mappings;
        }
        else if (ports.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException("Invalid container Ports.");
        return result;
    }

    internal static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
        return default;
    }

    internal static string ReadString(JsonElement root, string name)
    {
        var value = Property(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => throw new JsonException($"Invalid container {name}."),
        };
    }

    internal static bool TryTimestamp(JsonElement value, out long seconds)
    {
        seconds = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ||
            value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            if (number is < -62135596800 or > 253402300799)
                return false;
            seconds = number;
            return true;
        }
        if (value.ValueKind == JsonValueKind.String &&
            ImageInfo.TryParseCreatedAt(value.GetString(), out var date))
        {
            seconds = date.ToUnixTimeSeconds();
            return true;
        }
        return false;
    }

    public override void Write(Utf8JsonWriter writer, ContainerInfo value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id);
        writer.WriteString("Name", value.Name);
        writer.WriteString("Image", value.Image);
        writer.WriteNumber("CreatedAt", value.CreatedAt);
        writer.WriteNumber("StateChangedAt", value.StateChangedAt);
        writer.WriteNumber("State", value.StateValue);
        writer.WritePropertyName("Ports");
        if (value.PortsKnown)
            JsonSerializer.Serialize(writer, value.Ports, options);
        else
            writer.WriteNullValue();
        writer.WriteBoolean("PortsKnown", value.PortsKnown);
        writer.WriteEndObject();
    }
}
