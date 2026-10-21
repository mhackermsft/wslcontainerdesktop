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

/// <summary>Shared inline-config validation and encoding for import, saved state and execution.</summary>
public static class ComposeConfigContent
{
    private const int MaxCharacters = 8_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Validates inline sources even when they came from saved JSON rather than YAML.</summary>
    public static void Validate(ComposeProject project)
    {
        var remaining = MaxCharacters;
        if (project.Secrets.Any(resource => resource.Content is not null))
            throw InvalidSource();
        foreach (var resource in project.Configs)
        {
            if (resource.Content is not { } content) continue;
            if (resource.External || resource.File is not null)
                throw InvalidSource();
            remaining -= content.Length;
            if (remaining < 0) throw LimitExceeded();
            ValidateText(content);
        }
    }

    /// <summary>Encodes exactly the resolved text, with no BOM, newline insertion or lossy replacement.</summary>
    public static byte[] GetBytes(string content)
    {
        ValidateText(content);
        return Utf8.GetBytes(content);
    }

    private static void ValidateText(string content)
    {
        if (content.Length > MaxCharacters) throw LimitExceeded();
        try { _ = Utf8.GetByteCount(content); }
        catch (EncoderFallbackException) { throw InvalidSource(); }
    }

    private static InvalidOperationException InvalidSource() =>
        new(UiText.Get("Compose_InlineConfigInvalid", "Inline Compose config definitions are invalid."));

    private static InvalidOperationException LimitExceeded() =>
        new(UiText.Get("Compose_InlineConfigLimit", "Inline Compose configs exceed the {0}-character configuration limit.", MaxCharacters));
}
