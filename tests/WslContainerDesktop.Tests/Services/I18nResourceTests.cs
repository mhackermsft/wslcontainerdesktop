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

using System.Xml.Linq;
using System.Text.RegularExpressions;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class I18nResourceTests
{
    [Fact]
    public void SettingsUidsDoNotApplyPropertiesToDifferentControlTypes()
    {
        var source = SourceDirectory();
        var page = XDocument.Load(Path.Combine(source, "Views", "SettingsPage.xaml"));
        var uid = XName.Get("Uid", "http://schemas.microsoft.com/winfx/2006/xaml");
        var controls = page.Descendants()
            .Where(element => element.Attribute(uid) is not null)
            .GroupBy(element => element.Attribute(uid)!.Value);

        foreach (var group in controls)
        {
            Assert.True(group.Select(element => element.Name.LocalName).Distinct().Count() == 1,
                $"x:Uid '{group.Key}' is shared by different control types. Give each type its own resource key so WinUI never applies an unsupported property.");
        }

        Assert.All(page.Descendants().Where(element => element.Name.LocalName == "ToggleSwitch"),
            element => Assert.NotNull(element.Attribute(uid)));
    }

    [Fact]
    public void EnglishAndChineseResourceKeysStayInSync()
    {
        var strings = Path.Combine(SourceDirectory(), "Strings");
        var english = ResourceKeys(Path.Combine(strings, "en-US", "Resources.resw"));
        var chinese = ResourceKeys(Path.Combine(strings, "zh-Hans", "Resources.resw"));

        Assert.Equal(english, chinese);
        Assert.Equal(english.Length, english.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(chinese.Length, chinese.Distinct(StringComparer.Ordinal).Count());
        // PRI names are case insensitive even though XML permits differently cased data keys.
        Assert.Equal(english.Length, english.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(chinese.Length, chinese.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AllXamlUidsHaveResourcesAndCompatibleControlTypes()
    {
        var source = SourceDirectory();
        var keys = ResourceKeys(Path.Combine(source, "Strings", "en-US", "Resources.resw"));
        var uid = XName.Get("Uid", "http://schemas.microsoft.com/winfx/2006/xaml");
        var liveUid = XName.Get("Localization.Uid", "using:WslContainerDesktop.Helpers");
        var assignments = Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => element.Attribute(uid) is not null)
                .Select(element =>
                {
                    Assert.Equal(element.Attribute(uid)!.Value, element.Attribute(liveUid)?.Value);
                    return (Uid: element.Attribute(uid)!.Value, Type: element.Name.LocalName);
                }));
        foreach (var group in assignments.GroupBy(item => item.Uid))
        {
            Assert.Single(group.Select(item => item.Type).Distinct());
            Assert.Contains(keys, key => key.StartsWith(group.Key + ".", StringComparison.Ordinal));
            if (group.First().Type is "TextBlock" or "TextBox" or "PasswordBox" or "Run")
                Assert.DoesNotContain(group.Key + ".Content", keys);
        }
    }

    [Fact]
    public void StaticEnglishUiTextHasResourcesAndLiveLanguageRefresh()
    {
        var source = SourceDirectory();
        var keys = ResourceKeys(Path.Combine(source, "Strings", "en-US", "Resources.resw"))
            .ToHashSet(StringComparer.Ordinal);
        var uid = XName.Get("Uid", "http://schemas.microsoft.com/winfx/2006/xaml");
        var liveUid = XName.Get("Localization.Uid", "using:WslContainerDesktop.Helpers");
        var textProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "Text", "Header", "Content", "PlaceholderText", "Title", "Description", "Message",
            "OnContent", "OffContent", "Label", "PaneTitle", "PrimaryButtonText", "SecondaryButtonText",
            "CloseButtonText", "ToolTipService.ToolTip", "AutomationProperties.Name",
        };
        // Exact technical labels and product names stay English. Do not exempt prose merely
        // because it contains one of these words (such as an untranslated WSL error message).
        var technicalLabels = new HashSet<string>(StringComparer.Ordinal)
        {
            "CPU", "GPU", "IP", "IPV4", "IPv4", "IPV6", "IPv6", "ID", "PID", "API", "Aa",
            "TCP", "UDP", "HTTP", "HTTPS", "DNS", "URL", "YAML", "JSON", "STDIN", "TTY",
            "Kubernetes", "k3s", "Compose", "Docker", "WSL", "WSLC", "WSL Container Desktop",
            "OpenAI", "Azure OpenAI", "Ollama", "Foundry Local", "GitHub Copilot", "MiniMax",
        };
        var missing = new List<string>();
        foreach (var folder in new[] { "Views", "Dialogs" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(source, folder), "*.xaml", SearchOption.AllDirectories))
                foreach (var element in XDocument.Load(file).Descendants())
                    foreach (var attribute in element.Attributes().Where(attribute =>
                        attribute.Name.NamespaceName.Length == 0 && textProperties.Contains(attribute.Name.LocalName)))
                    {
                        var value = attribute.Value.Trim();
                        if (value.StartsWith("{}", StringComparison.Ordinal)) value = value[2..];
                        else if (value.StartsWith('{')) continue; // Binding, x:Bind and other markup extensions.
                        if (!value.Any(char.IsAsciiLetter) || technicalLabels.Contains(value) ||
                            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                            continue;

                        var property = attribute.Name.LocalName switch
                        {
                            "AutomationProperties.Name" => "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                            "ToolTipService.ToolTip" => "[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip",
                            var name => name,
                        };
                        var resourceUid = element.Attribute(uid)?.Value;
                        if (string.IsNullOrWhiteSpace(resourceUid) || element.Attribute(liveUid)?.Value != resourceUid ||
                            !keys.Contains(resourceUid + "." + property))
                            missing.Add($"{Path.GetRelativePath(source, file)}: {element.Name.LocalName}.{attribute.Name.LocalName} = '{value}'");
                    }

        Assert.True(missing.Count == 0,
            "Static UI prose needs x:Uid, matching Localization.Uid and a resource for its exact property:\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void LocalesKeepTheSameFormatArgumentsAndEveryExplicitLookupExists()
    {
        var source = SourceDirectory();
        Dictionary<string, string> Read(string locale) => XDocument.Load(Path.Combine(source, "Strings", locale, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(element => element.Attribute("name")!.Value,
                element => element.Element("value")!.Value, StringComparer.Ordinal);
        var english = Read("en-US");
        var chinese = Read("zh-Hans");
        Assert.DoesNotContain(english.Keys, key => key.EndsWith(".Tag", StringComparison.Ordinal));
        Assert.All(english.Keys.Where(key => key.Contains("ToolTipService.ToolTip")),
            key => Assert.Contains(".[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip", key));
        Assert.All(english.Keys.Where(key => key.Contains("AutomationProperties.Name")),
            key => Assert.Contains(".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", key));
        var placeholders = new Regex(@"(?<!\{)\{(\d+)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})");
        string[] Args(string value) => placeholders.Matches(value).Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
        foreach (var entry in english)
            Assert.Equal(Args(entry.Value), Args(chinese[entry.Key]));

        var lookups = new Regex("UiText\\.Get\\(\\s*\"([^\"]+)\"");
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            foreach (Match match in lookups.Matches(File.ReadAllText(file)))
                Assert.True(english.ContainsKey(match.Groups[1].Value), $"Missing resource {match.Groups[1].Value} used in {file}.");
    }

    /// <summary>
    /// Guards the crash where "follow the system" was written into a WinRT language property as an
    /// empty string. Both FrameworkElement.Language and ApplicationLanguages.PrimaryLanguageOverride
    /// reject it as a malformed BCP-47 tag and throw, ending the launch. An empty tag must stay
    /// empty through normalization so callers can branch on it instead of forwarding it.
    /// </summary>
    [Fact]
    public void FollowingTheSystemLanguageNormalizesToEmptyRatherThanAMalformedTag()
    {
        Assert.Equal(string.Empty, AppLanguage.Normalize(null));
        Assert.Equal(string.Empty, AppLanguage.Normalize(string.Empty));
        Assert.Equal(string.Empty, AppLanguage.Normalize("   "));

        // An unrecognized tag also falls back to the system rather than reaching WinUI unvalidated.
        Assert.Equal(string.Empty, AppLanguage.Normalize("not a tag"));
        Assert.Equal(string.Empty, AppLanguage.Normalize("fr-FR"));

        // Real tags survive untouched.
        Assert.Equal("zh-Hans", AppLanguage.Normalize("zh-Hans"));
        Assert.Equal("en-US", AppLanguage.Normalize("EN-US"));
    }

    /// <summary>
    /// Maps independent Windows UI preferences to the shipped resources. The script subtag decides:
    /// a traditional Chinese language must not land on the simplified resources.
    /// </summary>
    [Theory]
    [InlineData("en-US", "en-US")]        // exact
    [InlineData("EN-us", "en-US")]        // case-insensitive
    [InlineData("en-GB", "en-US")]        // same language, no script claimed either side
    [InlineData("en", "en-US")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-CN", "zh-Hans")]      // simplified Chinese script
    [InlineData("zh-SG", "zh-Hans")]      // same, different region
    [InlineData("zh-TW", "")]             // traditional script must not pick simplified resources
    [InlineData("zh-Hant", "")]
    [InlineData("zh", "")]                // ambiguous script: decline rather than guess
    [InlineData("de-DE", "")]             // unsupported: Resolve checks the next preference or falls back
    [InlineData("fr", "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void SystemLanguagesMapOntoTheShippedResource(string? languageTag, string expected)
    {
        Assert.Equal(expected, AppLanguage.MatchSupported(languageTag));
    }

    /// <summary>
    /// <see cref="AppLanguage.MatchSupported"/> skips the "follow the system" placeholder by testing
    /// for an empty tag, so the placeholder has to stay first and stay empty. Reordering the list
    /// would make it match profile languages as if it were a real language.
    /// </summary>
    [Fact]
    public void TheSystemPlaceholderStaysFirstAndEmpty()
    {
        Assert.Equal(AppLanguage.SystemDefault, AppLanguage.Supported[0].Tag);
        Assert.Equal(string.Empty, AppLanguage.Supported[0].Tag);

        // Every other entry is a real BCP-47 tag that could be written to the MRT override.
        foreach (var option in AppLanguage.Supported.Skip(1))
        {
            Assert.NotEqual(AppLanguage.SystemDefault, option.Tag);
            Assert.Equal(option.Tag, AppLanguage.Normalize(option.Tag));
        }
    }

    private static string[] ResourceKeys(string path) => XDocument.Load(path).Root!
        .Elements("data")
        .Select(element => (string)element.Attribute("name")!)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private static string SourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WslContainerDesktop.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "WslContainerDesktop");
    }
}
