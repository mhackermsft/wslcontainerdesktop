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

using System.Text;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Releases complete plain-prose sentences, never arbitrary transport fragments.
/// Structured/quoted/code/credential syntax freezes incremental release for the rest
/// of this message, so later fields cannot retroactively turn an emitted value into a secret.
/// This shares the sanitizer's documented inability to identify arbitrary unmarked secrets.
/// </summary>
internal sealed class AiStreamingText(Action<AiChatProgress>? progress)
{
    private const int InputLimit = 128 * 1024;
    private readonly StringBuilder _input = new();
    private string _published = "";
    private bool _held;
    private bool _complete;
    private int _boundary;
    private int _processedBoundary;

    public void Append(string fragment)
    {
        if (_complete)
            throw new InvalidOperationException("Text arrived after message completion.");
        if (fragment.Length > InputLimit - _input.Length)
            throw new InvalidOperationException("Assistant streaming text exceeded its bounded message limit.");
        var start = _input.Length;
        _input.Append(fragment);
        if (_held)
            return;
        for (var i = start; i < _input.Length; i++)
        {
            var ch = _input[i];
            // Restrict early release rather than attempting to parse incomplete JSON,
            // YAML, markdown fences, URLs, quoted strings or multiline credentials.
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '\t' or '\r' or '\n' or '.' or ',' or '!' or '?'))
            {
                _held = true;
                break;
            }
            if (ch == '\n' || (ch is ' ' or '\t' or '\r' && i > 0 && _input[i - 1] is '.' or '!' or '?'))
                _boundary = i + 1;
        }
        if (_boundary > _processedBoundary && _boundary <= AiTextSanitizer.EvidenceLimit)
        {
            _processedBoundary = _boundary;
            PublishPrefix(AiTextSanitizer.Redact(_input.ToString(0, _boundary)));
        }
    }

    public void Complete()
    {
        if (_complete)
            throw new InvalidOperationException("Assistant text completed more than once.");
        _complete = true;
        // Never append a rewritten/truncated prefix; the final result replaces the
        // preview in the conversation and retains the complete-input privacy boundary.
        PublishPrefix(AiTextSanitizer.Sanitize(_input.ToString()));
    }

    private void PublishPrefix(string safe)
    {
        if (safe.Length > AiTextSanitizer.EvidenceLimit ||
            !safe.StartsWith(_published, StringComparison.Ordinal) || safe.Length == _published.Length)
            return;
        var delta = safe[_published.Length..];
        _published = safe;
        progress?.Invoke(new(AiChatProgressKind.TextDelta, delta));
    }
}
