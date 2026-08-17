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

namespace WslContainerDesktop.Services;

/// <summary>
/// Shared text redaction/truncation for anything derived from AI provider requests or responses —
/// diagnostics payloads, provider exception messages, and error technical details. Centralizing
/// this keeps the "never leak secrets" rule enforced in one place instead of duplicated per
/// provider or view model.
/// </summary>
public static partial class AiTextSanitizer
{
    /// <summary>Truncates from the end, appending an ellipsis when content was cut.</summary>
    public static string Truncate(string text, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + "…";
    }

    /// <summary>Truncates from the middle, keeping head and tail context — used for large payloads
    /// (e.g. logs, inspect JSON) where both ends carry useful information.</summary>
    public static string TruncateMiddle(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        var head = maxChars / 2;
        var tail = maxChars - head;
        return text[..head] + "\n...[truncated]...\n" + text[^tail..];
    }

    /// <summary>Masks common secret shapes (env-style KEY=value assignments, connection-string
    /// fields, and Bearer/Basic auth headers) so redacted text is safe to send to a provider or show
    /// in a copyable technical-details section.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = SecretAssignmentRegex().Replace(text, "$1=<redacted>");
        redacted = ConnectionStringRegex().Replace(redacted, "$1=<redacted>");
        return BearerRegex().Replace(redacted, "$1 <redacted>");
    }

    [GeneratedRegex(@"(?im)\b([A-Z0-9_]*(?:TOKEN|KEY|SECRET|PASSWORD|PASSWD|PWD)[A-Z0-9_]*\s*[:=]\s*)([^\s,;""']+|""[^""]*""|'[^']*')")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?im)\b((?:DefaultEndpointsProtocol|AccountKey|SharedAccessKey|Password|User ID|Uid|Pwd)\s*=\s*)([^;,\s]+)")]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"(?im)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();
}
