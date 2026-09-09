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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public static class NativeHealthParser
{
    public static string? ContainerId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
                root = root[0];
            var id = Property(root, "Id");
            return id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            // Callers treat a missing identity as an explicit failure, never a matching container.
            return null;
        }
    }

    public static NativeHealthObservation Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
                root = root[0];
            if (root.ValueKind != JsonValueKind.Object)
                return new(NativeHealthState.Unknown, Diagnostic: "Invalid container inspect health response.");
            if (Property(root, "Config").ValueKind != JsonValueKind.Object &&
                Property(root, "State").ValueKind != JsonValueKind.Object)
                return new(NativeHealthState.Unknown, Diagnostic: "Inspect response does not identify container configuration or state.");

            NativeHealthOptions? options = null;
            if (Property(Property(root, "Config"), "Healthcheck") is { ValueKind: JsonValueKind.Object } config)
            {
                options = new NativeHealthOptions();
                var test = Property(config, "Test");
                if (test.ValueKind == JsonValueKind.Array)
                    options.Test = test.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!).ToList();
                options.Interval = Duration(config, "Interval");
                options.Timeout = Duration(config, "Timeout");
                options.StartPeriod = Duration(config, "StartPeriod");
                options.StartInterval = Duration(config, "StartInterval");
                if (Property(config, "Retries").TryGetInt32Safe(out var retries) && retries > 0)
                    options.Retries = retries;
            }
            if (options?.IsDisabled == true)
                return new(NativeHealthState.Disabled, options);
            var health = Property(Property(root, "State"), "Health");
            var status = Property(health, "Status");
            if (status.ValueKind == JsonValueKind.String)
            {
                var state = status.GetString()?.ToLowerInvariant() switch
                {
                    "starting" => NativeHealthState.Starting,
                    "healthy" => NativeHealthState.Healthy,
                    "unhealthy" => NativeHealthState.Unhealthy,
                    _ => NativeHealthState.Unknown,
                };
                return new(state, options, state == NativeHealthState.Unknown ? "Unrecognized engine health status." : null);
            }
            return options?.HasCommand == true || health.ValueKind == JsonValueKind.Object
                ? new(NativeHealthState.Unknown, options, "Engine health is configured but its status is unavailable.")
                : new(NativeHealthState.Absent, options);
        }
        catch (JsonException ex)
        {
            return new(NativeHealthState.Unknown, Diagnostic: $"Unreadable engine health: {ex.Message}");
        }
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
        return default;
    }

    private static bool TryGetInt32Safe(this JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static string? Duration(JsonElement config, string name)
    {
        var value = Property(config, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var nanos) && nanos > 0
            ? (nanos / 1_000_000_000m).ToString(CultureInfo.InvariantCulture) + "s"
            : null;
    }
}
