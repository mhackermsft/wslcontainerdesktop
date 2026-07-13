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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Turns a <em>basic</em> <c>docker-compose.yml</c> into one <see cref="RunProfile"/> per service.
/// This intentionally covers only the common single-container fields (image, container_name,
/// ports, environment, volumes, networks) — full compose orchestration (depends_on, build,
/// healthchecks, deploy, etc.) is out of scope and simply ignored. A tiny indentation-aware
/// reader is used rather than pulling in a YAML dependency, mirroring the manual parsing already
/// used elsewhere in the app (see <see cref="K8sManifestSanitizer"/>).
/// </summary>
public static class ComposeImporter
{
    private sealed record Line(int Indent, string Text);

    /// <summary>
    /// Parses <paramref name="yaml"/> and returns a profile for each service found. Returns an
    /// empty list when no <c>services:</c> block is present. Never throws for malformed content —
    /// unrecognized keys are skipped.
    /// </summary>
    public static IReadOnlyList<RunProfile> Parse(string yaml)
    {
        var lines = Tokenize(yaml);
        var servicesIndex = lines.FindIndex(l => l.Indent == 0 && KeyOf(l.Text) == "services");
        if (servicesIndex < 0)
        {
            return Array.Empty<RunProfile>();
        }

        // Everything more-indented than the "services:" line, up to the next top-level key.
        var block = new List<Line>();
        for (var i = servicesIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].Indent == 0)
            {
                break;
            }

            block.Add(lines[i]);
        }

        if (block.Count == 0)
        {
            return Array.Empty<RunProfile>();
        }

        var serviceIndent = block[0].Indent;
        var profiles = new List<RunProfile>();

        for (var i = 0; i < block.Count; i++)
        {
            if (block[i].Indent != serviceIndent)
            {
                continue;
            }

            var serviceName = KeyOf(block[i].Text);
            if (string.IsNullOrEmpty(serviceName))
            {
                continue;
            }

            // Collect this service's more-indented child lines.
            var children = new List<Line>();
            var j = i + 1;
            for (; j < block.Count && block[j].Indent > serviceIndent; j++)
            {
                children.Add(block[j]);
            }

            i = j - 1;

            var profile = BuildProfile(serviceName, children);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    private static RunProfile? BuildProfile(string serviceName, List<Line> children)
    {
        if (children.Count == 0)
        {
            return null;
        }

        var propIndent = children[0].Indent;
        var options = new RunContainerOptions { Name = serviceName };

        for (var i = 0; i < children.Count; i++)
        {
            if (children[i].Indent != propIndent)
            {
                continue;
            }

            var key = KeyOf(children[i].Text);
            var inline = ValueOf(children[i].Text);

            // The nested block belonging to this property (list items or a mapping). YAML allows a
            // block sequence whose "- item" lines sit at the SAME column as the parent key (a very
            // common docker-compose style), so those same-indent sequence items must be gathered
            // here too — otherwise the outer loop would mistake each "- item" for its own property
            // and silently drop ports/volumes/environment.
            var nested = new List<Line>();
            var j = i + 1;
            for (; j < children.Count; j++)
            {
                var deeper = children[j].Indent > propIndent;
                var sameIndentSequenceItem = children[j].Indent == propIndent && children[j].Text.StartsWith('-');
                if (!deeper && !sameIndentSequenceItem)
                {
                    break;
                }

                nested.Add(children[j]);
            }

            switch (key)
            {
                case "image":
                    options.Image = Unquote(inline);
                    break;
                case "container_name":
                    var name = Unquote(inline);
                    options.Name = string.IsNullOrWhiteSpace(name) ? serviceName : name;
                    break;
                case "command":
                    options.Command = string.IsNullOrWhiteSpace(inline) ? null : Unquote(inline);
                    break;
                case "ports":
                    options.PortMappings = CollectSequence(inline, nested);
                    break;
                case "volumes":
                    options.Volumes = CollectSequence(inline, nested);
                    break;
                case "environment":
                    options.EnvironmentVariables = CollectEnvironment(inline, nested);
                    break;
                case "network_mode":
                    options.Network = NormalizeNetwork(Unquote(inline));
                    break;
                case "networks":
                    var networks = CollectSequence(inline, nested);
                    options.Network = networks.Count > 0 ? NormalizeNetwork(networks[0]) : options.Network;
                    break;
            }

            i = j - 1;
        }

        if (string.IsNullOrWhiteSpace(options.Image))
        {
            return null;
        }

        return new RunProfile { Name = serviceName, Options = options };
    }

    /// <summary>Reads a compose sequence written either inline (<c>[a, b]</c>) or as <c>- item</c> lines.</summary>
    private static List<string> CollectSequence(string inline, List<Line> nested)
    {
        var items = new List<string>();

        foreach (var value in InlineFlowItems(inline))
        {
            items.Add(value);
        }

        foreach (var line in nested)
        {
            if (line.Text.StartsWith('-'))
            {
                var value = Unquote(line.Text[1..].Trim());
                if (!string.IsNullOrEmpty(value))
                {
                    items.Add(value);
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Reads compose <c>environment</c>, which may be a list of <c>KEY=VALUE</c> items or a mapping
    /// of <c>KEY: VALUE</c>. Both are normalized to raw <c>KEY=VALUE</c> strings.
    /// </summary>
    private static List<string> CollectEnvironment(string inline, List<Line> nested)
    {
        var items = new List<string>();

        // Honor an inline flow list (environment: ["FOO=bar", ...]) the same way CollectSequence
        // does for ports/volumes; otherwise inline environments are silently dropped.
        foreach (var value in InlineFlowItems(inline))
        {
            items.Add(value);
        }

        foreach (var line in nested)
        {
            if (line.Text.StartsWith('-'))
            {
                var value = Unquote(line.Text[1..].Trim());
                if (!string.IsNullOrEmpty(value))
                {
                    items.Add(value);
                }
            }
            else
            {
                var key = KeyOf(line.Text);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var value = Unquote(ValueOf(line.Text));
                items.Add(string.IsNullOrEmpty(value) ? key : $"{key}={value}");
            }
        }

        return items;
    }

    private static IEnumerable<string> InlineFlowItems(string inline)
    {
        var trimmed = inline.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            yield break;
        }

        foreach (var part in trimmed[1..^1].Split(','))
        {
            var value = Unquote(part.Trim());
            if (!string.IsNullOrEmpty(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>Maps compose network conventions to a `wslc run --network` value (null = default bridge).</summary>
    private static string? NormalizeNetwork(string network)
    {
        if (string.IsNullOrWhiteSpace(network) ||
            string.Equals(network, "default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return network;
    }

    private static List<Line> Tokenize(string yaml)
    {
        var result = new List<Line>();
        if (string.IsNullOrEmpty(yaml))
        {
            return result;
        }

        // Normalize every line-ending style to '\n' before splitting. In particular WinUI's TextBox
        // returns multi-line text with bare '\r' separators, which would otherwise collapse the whole
        // document into a single line and break parsing.
        foreach (var raw in yaml.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Split('\n'))
        {
            var withoutComment = StripComment(raw);
            if (string.IsNullOrWhiteSpace(withoutComment))
            {
                continue;
            }

            var indent = 0;
            while (indent < withoutComment.Length && withoutComment[indent] == ' ')
            {
                indent++;
            }

            result.Add(new Line(indent, withoutComment.Trim()));
        }

        return result;
    }

    /// <summary>Removes a trailing <c>#</c> comment that is not inside quotes.</summary>
    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || line[i - 1] == ' '))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string KeyOf(string text)
    {
        var colon = text.IndexOf(':');
        var key = colon < 0 ? text : text[..colon];
        return key.Trim();
    }

    private static string ValueOf(string text)
    {
        var colon = text.IndexOf(':');
        return colon < 0 || colon == text.Length - 1 ? string.Empty : text[(colon + 1)..].Trim();
    }

    private static string Unquote(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
        {
            return v[1..^1];
        }

        return v;
    }
}
