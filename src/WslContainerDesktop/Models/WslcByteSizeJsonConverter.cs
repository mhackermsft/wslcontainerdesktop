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

/// <summary>Reads legacy byte counts and the human-readable sizes emitted by WSL 2.9.9.</summary>
public sealed class WslcByteSizeJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var bytes))
        {
            return bytes;
        }

        if (reader.TokenType == JsonTokenType.String &&
            TryParseHumanSize(reader.GetString(), out bytes))
        {
            return bytes;
        }

        throw new JsonException("Expected an image size as a byte count or a value such as '887MB'.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);

    private static bool TryParseHumanSize(string? value, out long bytes)
    {
        bytes = 0;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var unitStart = text.Length;
        while (unitStart > 0 && char.IsLetter(text[unitStart - 1]))
        {
            unitStart--;
        }

        if (!decimal.TryParse(
                text[..unitStart].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount) ||
            amount < 0)
        {
            return false;
        }

        var multiplier = text[unitStart..].ToUpperInvariant() switch
        {
            "" or "B" => 1m,
            "KB" => 1_000m,
            "MB" => 1_000_000m,
            "GB" => 1_000_000_000m,
            "TB" => 1_000_000_000_000m,
            "PB" => 1_000_000_000_000_000m,
            "EB" => 1_000_000_000_000_000_000m,
            "KIB" => 1_024m,
            "MIB" => 1_048_576m,
            "GIB" => 1_073_741_824m,
            "TIB" => 1_099_511_627_776m,
            "PIB" => 1_125_899_906_842_624m,
            "EIB" => 1_152_921_504_606_846_976m,
            _ => 0m,
        };

        if (multiplier == 0 || amount > long.MaxValue / multiplier)
        {
            return false;
        }

        bytes = decimal.ToInt64(decimal.Round(amount * multiplier, 0, MidpointRounding.AwayFromZero));
        return true;
    }
}
