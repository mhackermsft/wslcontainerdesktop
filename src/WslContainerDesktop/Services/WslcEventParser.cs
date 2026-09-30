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

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Parses the text-only output of <c>wslc events</c>.</summary>
public static partial class WslcEventParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"^(?<timestamp>\S+)\s+(?<type>\S+)\s+(?<action>\S+)\s+(?<actor>\S+)(?:\s+\((?<attrs>.*)\))?\s*$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventLineRegex();

    [GeneratedRegex(@", (?=[A-Za-z_][A-Za-z0-9_.-]*=)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeBoundaryRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeKeyRegex();

    /// <summary>Parses all non-empty event lines, skipping malformed lines with optional debug logging.</summary>
    public static IReadOnlyList<EngineEvent> ParseLines(string text, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var events = new List<EngineEvent>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseLine(line, out var evt, logger))
            {
                events.Add(evt);
            }
        }

        return events;
    }

    /// <summary>Attempts to parse one <c>wslc events</c> line into an <see cref="EngineEvent"/>.</summary>
    public static bool TryParseLine(string? line, out EngineEvent evt, ILogger? logger = null)
    {
        evt = new EngineEvent();
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var match = EventLineRegex().Match(line);
            if (!match.Success ||
                !DateTimeOffset.TryParse(match.Groups["timestamp"].Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                logger?.LogDebug("Skipping unparseable wslc event line: {Line}", line);
                return false;
            }

            if (!TryParseAttributes(match.Groups["attrs"].Value, out var attributes))
            {
                logger?.LogDebug("Skipping wslc event line with unparseable attributes: {Line}", line);
                return false;
            }

            evt = new EngineEvent
            {
                Timestamp = timestamp,
                Type = match.Groups["type"].Value,
                Action = match.Groups["action"].Value,
                ActorId = match.Groups["actor"].Value,
                Attributes = attributes,
            };
            return true;
        }
        catch (RegexMatchTimeoutException ex)
        {
            logger?.LogDebug(ex, "Skipping wslc event line after parser timeout: {Line}", line);
            return false;
        }
    }

    /// <summary>Parses the parenthesized key/value attribute section while guarding ambiguous label text.</summary>
    private static bool TryParseAttributes(string text, out IReadOnlyDictionary<string, string> attributes)
    {
        attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        // The engine emits attributes sorted by key (ordinal), but label values are free text and can
        // contain ", key=" sequences, so boundaries are ambiguous. Parse both ways — trusting the
        // leftmost and the rightmost reading of the sorted order — and keep the reading in which the
        // engine's own attributes have well-formed values (a spliced-in fake always leaves ", " in one
        // of them). Both readings only ever accept strictly increasing keys.
        var parts = AttributeBoundaryRegex().Split(text);
        var left = ParseLeftToRight(parts);
        var right = ParseRightToLeft(parts);
        var chosen = (left, right) switch
        {
            (null, null) => null,
            (null, _) => right,
            (_, null) => left,
            _ => Score(right!) >= Score(left!) ? right : left,
        };

        if (chosen is null)
        {
            return false;
        }

        attributes = chosen;
        return true;
    }

    /// <summary>Greedy left-to-right interpretation of sorted attributes used to disambiguate values.</summary>
    private static Dictionary<string, string>? ParseLeftToRight(string[] parts)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? previousKey = null;
        foreach (var part in parts)
        {
            if (TryKey(part, out var key, out var value) &&
                (previousKey is null || string.CompareOrdinal(key, previousKey) > 0))
            {
                result[key] = value;
                previousKey = key;
            }
            else if (previousKey is null)
            {
                return null;
            }
            else
            {
                result[previousKey] = result[previousKey] + ", " + part;
            }
        }

        return result;
    }

    /// <summary>Greedy right-to-left interpretation of sorted attributes used to disambiguate values.</summary>
    private static Dictionary<string, string>? ParseRightToLeft(string[] parts)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? nextKey = null;
        var pending = string.Empty;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var segment = parts[i] + pending;
            if (TryKey(segment, out var key, out var value) &&
                (nextKey is null || string.CompareOrdinal(key, nextKey) < 0))
            {
                result[key] = value;
                nextKey = key;
                pending = string.Empty;
            }
            else
            {
                // Not a real boundary: this text belongs to the value on its left.
                pending = ", " + segment;
            }
        }

        return pending.Length == 0 ? result : null;
    }

    /// <summary>Splits one potential <c>key=value</c> segment and validates the key shape.</summary>
    private static bool TryKey(string segment, out string key, out string value)
    {
        var separator = segment.IndexOf('=');
        key = separator > 0 ? segment[..separator] : string.Empty;
        value = separator > 0 ? segment[(separator + 1)..] : string.Empty;
        return separator > 0 && AttributeKeyRegex().IsMatch(key);
    }

    /// <summary>+1 for each engine-owned attribute with a well-formed value, -1 for each malformed one.</summary>
    private static int Score(Dictionary<string, string> attributes)
    {
        var score = 0;
        foreach (var (key, pattern) in EngineAttributePatterns)
        {
            if (attributes.TryGetValue(key, out var value))
            {
                score += pattern.IsMatch(value) ? 1 : -1;
            }
        }

        return score;
    }

    /// <summary>Known engine-owned attributes whose shape helps score ambiguous parsing choices.</summary>
    private static readonly (string Key, Regex Pattern)[] EngineAttributePatterns =
    [
        ("container", new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant, RegexTimeout)),
        ("exitCode", new Regex(@"^-?\d+$", RegexOptions.CultureInvariant, RegexTimeout)),
        ("image", new Regex(@"^[^\s,]+$", RegexOptions.CultureInvariant, RegexTimeout)),
        ("name", new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant, RegexTimeout)),
        ("type", new Regex(@"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant, RegexTimeout)),
    ];
}
