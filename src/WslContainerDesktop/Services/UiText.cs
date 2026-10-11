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
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WslContainerDesktop.Services;

/// <summary>Localizes application-owned display text without coupling data models to WinUI.</summary>
public static class UiText
{
    private static Func<string, object?[], string>? _resolve;
    private static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private static List<DisplayTemplate> _templates = new();
    private static Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _chinese = new(StringComparer.Ordinal);
    private static Dictionary<string, List<(string Property, string Key)>> _properties = new(StringComparer.Ordinal);
    internal static string LanguageTag { get; private set; } = "en-US";

    /// <summary>Raised on the UI thread after resource contexts have changed.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Installs the packaged resource resolver before constructing view models.</summary>
    internal static void Initialize(Func<string, object?[], string> resolve)
    {
        var assembly = typeof(UiText).Assembly;
        using var english = assembly.GetManifestResourceStream("UiText.en-US.resw");
        using var chinese = assembly.GetManifestResourceStream("UiText.zh-Hans.resw");
        if (english is null || chinese is null)
            throw new InvalidOperationException("The application display-text catalogs are missing.");
        var en = ReadCatalog(english);
        var zh = ReadCatalog(chinese);
        Configure(resolve, en.Select(pair => (pair.Key, pair.Value, zh.GetValueOrDefault(pair.Key, pair.Value))));
    }

    internal static void Configure(Func<string, object?[], string>? resolve,
        IEnumerable<(string Key, string English, string Chinese)>? entries = null)
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new List<DisplayTemplate>();
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        var chinese = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            english[entry.Key] = entry.English;
            chinese[entry.Key] = entry.Chinese;
            // A positional-only format belongs to its explicit Get call. Registering it as a
            // free-text pattern would match arbitrary stderr, user names and other unknown text.
            var hasTemplateWords = FormatPlaceholder.Replace(entry.English, "").Any(char.IsLetter);
            foreach (var source in new[] { entry.English, entry.Chinese }.Distinct())
            {
                if (source.Length == 0) continue;
                if (!FormatPlaceholder.IsMatch(source))
                    exact.TryAdd(source, entry.Key);
                else if (hasTemplateWords && DisplayTemplate.Create(entry.Key, source) is { } template)
                    templates.Add(template);
            }
        }
        _exact = exact;
        _templates = templates.OrderByDescending(template => template.Prefix.Length).ToList();
        _english = english;
        _chinese = chinese;
        _properties = english.Keys.Where(key => key.Contains('.')).GroupBy(key => key[..key.IndexOf('.')])
            .ToDictionary(group => group.Key,
                group => group.Select(key => (key[(key.IndexOf('.') + 1)..], key)).ToList(), StringComparer.Ordinal);
        _resolve = resolve;
    }

    /// <summary>Gets a resource; the English fallback also permits engine-only tests without an app.</summary>
    public static string Get(string key, string english, params object?[] args)
    {
        // Attached-property names contain namespace dots. Read their exact catalog key rather
        // than converting those dots into MRT subtree separators.
        if (key.Contains(".[using:", StringComparison.Ordinal))
        {
            var catalog = LanguageTag == "zh-Hans" ? _chinese : _english;
            if (catalog.TryGetValue(key, out var propertyText))
            {
                if (args.Length == 0) return propertyText;
                try { return string.Format(CultureInfo.CurrentUICulture, propertyText, args); }
                catch (FormatException) { /* Keep the same English fallback as normal lookups. */ }
            }
        }
        var resolve = _resolve;
        if (resolve is not null)
        {
            var translated = resolve(key, args);
            if (!string.IsNullOrEmpty(translated) && translated != key) return translated;
        }
        try { return string.Format(CultureInfo.CurrentCulture, english, args); }
        catch (FormatException) { return english; }
    }

    /// <summary>
    /// Reprojects known application messages in either locale. Unknown text, external output and
    /// user data are preserved. Call only at an application-owned display boundary, never on logs,
    /// identifiers, commands, configuration, user templates or assistant conversation content.
    /// </summary>
    public static string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var resolve = _resolve;
        if (resolve is null) return text;
        if (_exact.TryGetValue(text, out var key)) return Get(key, text);
        if (text.Length > 32768) return text;
        foreach (var template in _templates)
        {
            if (!text.StartsWith(template.Prefix, StringComparison.Ordinal)) continue;
            Match match;
            try { match = template.Pattern.Match(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            var args = new object?[template.ArgumentCount];
            for (var index = 0; index < args.Length; index++)
                args[index] = match.Groups["p" + index].Value;
            return Get(template.Key, text, args);
        }
        return text;
    }

    /// <summary>Translates known lines in application-owned guidance, preserving unknown details.</summary>
    public static string TranslateLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var whole = Translate(text);
        if (whole != text) return whole;
        return string.Join('\n', text.Split('\n').Select(line =>
        {
            var ending = line.EndsWith('\r') ? "\r" : "";
            var body = ending.Length == 0 ? line : line[..^1];
            return Translate(body) + ending;
        }));
    }

    internal static void NotifyLanguageChanged() => LanguageChanged?.Invoke(null, EventArgs.Empty);
    internal static void SetLanguage(string effective) => LanguageTag = effective;

    internal static IEnumerable<(string Property, string Value)> Properties(string uid)
    {
        var catalog = LanguageTag == "zh-Hans" ? _chinese : _english;
        if (_properties.TryGetValue(uid, out var properties))
            foreach (var (property, key) in properties)
                yield return (property, catalog[key]);
    }

    private static readonly Regex FormatPlaceholder = new(@"(?<!\{)\{(\d+)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static Dictionary<string, string> ReadCatalog(Stream stream) => XDocument.Load(stream).Root!
        .Elements("data").ToDictionary(element => element.Attribute("name")!.Value,
            element => element.Element("value")!.Value, StringComparer.Ordinal);

    private sealed record DisplayTemplate(string Key, string Prefix, Regex Pattern, int ArgumentCount)
    {
        public static DisplayTemplate? Create(string key, string source)
        {
            var placeholders = FormatPlaceholder.Matches(source);
            if (placeholders.Count == 0) return null;
            var pattern = new StringBuilder("\\A");
            var seen = new HashSet<int>();
            var position = 0;
            foreach (Match placeholder in placeholders)
            {
                pattern.Append(Regex.Escape(source[position..placeholder.Index]));
                var index = int.Parse(placeholder.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index > 32) return null;
                pattern.Append(seen.Add(index) ? $"(?<p{index}>.*?)" : $"\\k<p{index}>");
                position = placeholder.Index + placeholder.Length;
            }
            pattern.Append(Regex.Escape(source[position..])).Append("\\z");
            return new(key, source[..placeholders[0].Index],
                new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(100)), seen.Max() + 1);
        }
    }
}
