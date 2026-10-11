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

namespace WslContainerDesktop.Services;

/// <summary>
/// One UI language the app can run in.
/// </summary>
/// <param name="Tag">
/// BCP-47 tag matching the folder under <c>Strings/</c> and the <c>&lt;Resource Language="…" /&gt;</c>
/// entry in Package.appxmanifest. Empty means "follow the system", which is the default.
/// </param>
/// <param name="ResourceKey">String resource holding this option's display name in its own language.</param>
internal sealed record AppLanguageOption(string Tag, string ResourceKey);

/// <summary>
/// The single source of truth for which UI languages exist. Adding one means: create
/// <c>Strings/&lt;tag&gt;/Resources.resw</c>, declare the tag in Package.appxmanifest's
/// <c>&lt;Resources&gt;</c> block, and add it here — no other code needs to change, because the
/// resource compiler discovers every locale folder that is declared.
/// </summary>
internal static class AppLanguage
{
    /// <summary>Language tag meaning "do not override; use the system's display language".</summary>
    public const string SystemDefault = "";

    /// <summary>Resolves the saved preference into a nonempty tag suitable for WinRT setters.</summary>
    public static string Resolve(string? preference, IEnumerable<string> systemLanguages)
    {
        var explicitTag = Normalize(preference);
        if (explicitTag.Length > 0) return explicitTag;
        foreach (var systemTag in systemLanguages)
        {
            var match = MatchSupported(systemTag);
            if (match.Length > 0) return match;
        }
        return "en-US";
    }

    /// <summary>The boundary between the empty system preference and the native language setter.</summary>
    public static string Apply(string? preference, IEnumerable<string> systemLanguages, Action<string> writer)
    {
        var effective = Resolve(preference, systemLanguages);
        writer(effective);
        return effective;
    }

    /// <summary>Selectable languages, in the order the Settings page lists them.</summary>
    public static readonly IReadOnlyList<AppLanguageOption> Supported = new[]
    {
        new AppLanguageOption(SystemDefault, "Settings_Language_Option_System"),
        new AppLanguageOption("en-US", "Settings_Language_Option_English"),
        new AppLanguageOption("zh-Hans", "Settings_Language_Option_ChineseSimplified"),
    };

    /// <summary>
    /// Maps a stored or user-supplied tag onto a supported one. Unknown, empty and malformed values
    /// fall back to <see cref="SystemDefault"/> rather than throwing: this comes from a JSON file on
    /// disk, which may be hand-edited, and a bad language must never stop the app from starting.
    /// </summary>
    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return SystemDefault;
        }

        foreach (var option in Supported)
        {
            if (string.Equals(option.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return option.Tag;
            }
        }

        return SystemDefault;
    }

    /// <summary>
    /// Picks the shipped language that best matches a profile tag such as <c>zh-CN</c> or <c>en-GB</c>,
    /// mirroring how MRT resolves resources for a tag the app does not declare exactly. Used when the
    /// language override has to be rewritten to stand in for an unset one.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="SystemDefault"/> when nothing matches. The resolver then tries the next
    /// Windows language or its explicit English fallback. Scripts are compared after inferring one from the region, so a
    /// traditional Chinese tag (<c>zh-TW</c>, <c>zh-Hant</c>) does not pick the simplified resources
    /// while <c>zh-CN</c> still does.
    /// </remarks>
    /// <param name="profileTag">A language tag the system ranks, or null/empty.</param>
    public static string MatchSupported(string? profileTag)
    {
        if (string.IsNullOrWhiteSpace(profileTag))
        {
            return SystemDefault;
        }

        var profile = SplitTag(profileTag);
        var profileScript = ResolveScript(profile.Script, profile.Region);

        string? candidateWithoutScript = null;

        foreach (var option in Supported)
        {
            if (option.Tag.Length == 0)
            {
                continue; // the "follow the system" placeholder is not a BCP-47 tag to match against
            }

            var candidate = SplitTag(option.Tag);
            if (!string.Equals(candidate.Language, profile.Language, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(
                    ResolveScript(candidate.Script, candidate.Region),
                    profileScript,
                    StringComparison.OrdinalIgnoreCase))
            {
                return option.Tag; // same language and script, so nothing later can beat this
            }

            // Same language but the scripts disagree, so this option is the wrong flavour of that
            // language. A script-neutral option is still better than nothing.
            if (candidateWithoutScript is null
                && candidate.Script.Length == 0
                && candidate.Region.Length == 0)
            {
                candidateWithoutScript = option.Tag;
            }
        }

        return candidateWithoutScript ?? SystemDefault;
    }

    /// <summary>
    /// Maps a region subtag onto the script it implies, for the one distinction this app can get wrong:
    /// simplified versus traditional Chinese. A tag such as <c>zh-CN</c> names no script, but Windows
    /// resolves it to Hans, so matching has to apply the same inference — otherwise Chinese profiles
    /// would miss the resources MRT would actually have picked for them.
    /// </summary>
    private static string LikelyScript(string region) => region.ToUpperInvariant() switch
    {
        "CN" or "SG" or "MY" => "Hans",
        "TW" or "HK" or "MO" => "Hant",
        _ => string.Empty,
    };

    /// <summary>The tag's own script subtag when it has one, otherwise the script its region implies.</summary>
    private static string ResolveScript(string script, string region) =>
        script.Length > 0 ? script : LikelyScript(region);

    /// <summary>Splits a tag into its language, script and region subtags; absent parts become empty.</summary>
    private static (string Language, string Script, string Region) SplitTag(string tag)
    {
        var parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var language = parts.Length > 0 ? parts[0] : string.Empty;

        var script = string.Empty;
        var region = string.Empty;

        // Start past the language subtag: it is short and alphabetic, so scanning from zero would
        // mistake "zh" for a region and lose the real one.
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (script.Length == 0 && part.Length == 4 && IsAlpha(part))
            {
                script = part;
                continue;
            }

            if (region.Length == 0 && (part.Length == 2 || part.Length == 3) && (IsAlpha(part) || IsDigit(part)))
            {
                region = part;
            }
        }

        return (language, script, region);
    }

    private static bool IsAlpha(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsLetter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigit(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Index of <paramref name="tag"/> in <see cref="Supported"/>, defaulting to 0 (system).</summary>
    public static int IndexOf(string? tag)
    {
        var normalized = Normalize(tag);
        for (var i = 0; i < Supported.Count; i++)
        {
            if (Supported[i].Tag == normalized)
            {
                return i;
            }
        }

        return 0;
    }
}
