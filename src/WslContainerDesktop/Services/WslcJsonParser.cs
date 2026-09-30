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

using System.Text;
using System.Text.Json;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Parses the JSON shapes emitted by <c>wslc.exe</c> inventory commands and rejects ambiguous or
/// partial data so callers do not act on an incomplete resource list.
/// </summary>
internal static class WslcJsonParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowMultipleValues = true,
    };

    /// <summary>Parses a successful volume inventory and requires each volume to have a unique name.</summary>
    internal static IReadOnlyList<VolumeInfo> ParseVolumes(CommandResult result)
    {
        CheckInventoryResult(result);
        var volumes = ParseList<VolumeInfo>(result.StandardOutput);
        if (volumes.Any(v => v is null || string.IsNullOrWhiteSpace(v.Name)) ||
            volumes.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != volumes.Count)
            throw new JsonException("Volume inventory contains missing or duplicate identities.");
        return volumes;
    }

    /// <summary>Parses a successful image inventory and requires each image to have an identity.</summary>
    internal static IReadOnlyList<ImageInfo> ParseImages(CommandResult result)
    {
        CheckInventoryResult(result);
        var images = ParseList<ImageInfo>(result.StandardOutput);
        if (images.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id)))
            throw new JsonException("Image inventory contains missing identities.");
        return images;
    }

    /// <summary>Throws when an inventory command failed, because failure means absence is unknown.</summary>
    private static void CheckInventoryResult(CommandResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException($"Resource inventory failed (exit {result.ExitCode}); absence is unknown.");
    }

    /// <summary>Reject incomplete inventories rather than exposing a partial reconciliation input.</summary>
    internal static IReadOnlyList<ContainerInfo> ParseContainers(string output)
    {
        var containers = ParseList<ContainerInfo>(output);
        if (containers.Any(c => c is null) ||
            containers.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != containers.Count)
            throw new JsonException("Container list contains null or duplicate records.");
        return containers;
    }

    /// <summary>Parses either a JSON array or newline-delimited object stream.</summary>
    internal static IReadOnlyList<T> ParseList<T>(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var json = output.Trim();
        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            json = json[1..].TrimStart();
        }

        if (json.Length == 0)
        {
            return Array.Empty<T>();
        }

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), ReaderOptions);
        if (!reader.Read())
        {
            return Array.Empty<T>();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var items = JsonSerializer.Deserialize<List<T>>(ref reader, JsonOptions)
                ?? throw new JsonException("The wslc JSON array was null.");

            if (reader.Read())
            {
                throw new JsonException("Unexpected JSON value after the wslc JSON array.");
            }

            return items;
        }

        var objects = new List<T>();
        do
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected each top-level wslc JSON value to be an object.");
            }

            var item = JsonSerializer.Deserialize<T>(ref reader, JsonOptions);
            if (item is null)
            {
                throw new JsonException("A top-level wslc JSON object deserialized to null.");
            }

            objects.Add(item);
        }
        while (reader.Read());

        return objects;
    }
}
