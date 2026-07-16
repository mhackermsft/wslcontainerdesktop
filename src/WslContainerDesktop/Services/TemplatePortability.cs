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
using System.Text.Json.Serialization;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Serializes and parses the <see cref="TemplateExportEnvelope"/> used to share templates as
/// <c>.wsltmpl</c> files. Parsing is defensive: any malformed or unsupported file throws
/// <see cref="InvalidDataException"/> with a user-facing message rather than yielding bad data.
/// </summary>
public static class TemplatePortability
{
    /// <summary>Current envelope schema version this build writes and can read.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The suggested file extension for exported template libraries.</summary>
    public const string FileExtension = ".wsltmpl";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Produces the JSON for an export envelope wrapping the given templates.</summary>
    public static string Serialize(IEnumerable<StackTemplate> templates)
    {
        var envelope = new TemplateExportEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            Templates = templates.ToList(),
        };
        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>
    /// Parses an export file into its templates. Throws <see cref="InvalidDataException"/> if the
    /// content is not a recognizable, supported template export.
    /// </summary>
    public static IReadOnlyList<StackTemplate> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The file is empty.");
        }

        TemplateExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<TemplateExportEnvelope>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid template export.", ex);
        }

        if (envelope is null)
        {
            throw new InvalidDataException("The file is not a valid template export.");
        }

        if (envelope.SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"This export was created by a newer version (schema {envelope.SchemaVersion}). "
                    + "Update the app to import it.");
        }

        var valid = envelope.Templates
            .Where(t => t is not null
                && !string.IsNullOrWhiteSpace(t.Name)
                && !string.IsNullOrWhiteSpace(t.Category))
            .ToList();

        if (valid.Count == 0)
        {
            throw new InvalidDataException("The file contained no usable templates.");
        }

        return valid;
    }
}
