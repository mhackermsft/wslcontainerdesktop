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
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

internal static class AiProviderJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static AiDiagnosis ParseDiagnosis(string content)
    {
        var json = ExtractJson(content);
        var diagnosis = JsonSerializer.Deserialize<AiDiagnosis>(json, Options)
            ?? throw new InvalidOperationException("The AI provider returned an empty diagnosis.");

        diagnosis.Summary = Clean(diagnosis.Summary);
        diagnosis.LikelyCause = Clean(diagnosis.LikelyCause);
        diagnosis.SuggestedFix ??= new AiSuggestedFix();
        diagnosis.EvidenceCited = diagnosis.EvidenceCited.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Clean).ToList();
        diagnosis.SuggestedFix.Commands = diagnosis.SuggestedFix.Commands.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Clean).ToList();
        diagnosis.SuggestedFix.FileEdits = diagnosis.SuggestedFix.FileEdits.Where(e => !string.IsNullOrWhiteSpace(e)).Select(Clean).ToList();
        diagnosis.Confidence = Math.Clamp(diagnosis.Confidence, 0, 1);
        return diagnosis;
    }

    private static string ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The AI provider returned no content.");
        }

        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        throw new InvalidOperationException("The AI provider did not return a parseable JSON diagnosis.");
    }

    private static string Clean(string value) => value.Trim();
}
