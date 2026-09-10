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

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace WslContainerDesktop.Services;

public static partial class ComposeImporter
{
    private static MappingNode ReadComposeYaml(string yaml, IReadOnlyDictionary<string, string>? environment,
        List<string>? warnings = null)
    {
        if (yaml.Length > 2_000_000)
            throw new ComposeConfigurationException("Compose YAML exceeds the 2,000,000-character configuration limit.");
        try
        {
            var reader = new YamlReader(yaml);
            var root = reader.Read();
            var budget = 100_000;
            var characterBudget = 8_000_000;
            return NormalizeRoot((MappingNode)InterpolateNode(root, environment, ref budget, ref characterBudget, warnings));
        }
        catch (YamlException ex)
        {
            // Parser exceptions can contain scalar values. Never retain their message/inner exception.
            throw YamlError(ex.Start, "Invalid YAML. Check indentation, quoting and collection delimiters.");
        }
    }

    private static ComposeConfigurationException YamlError(Mark mark, string message) =>
        new($"Compose YAML line {mark.Line}, column {mark.Column}: {message}");

    private static Node InterpolateNode(Node node, IReadOnlyDictionary<string, string>? env,
        ref int budget, ref int characterBudget, List<string>? warnings, int depth = 0)
    {
        if (--budget < 0 || depth > 64)
            throw new ComposeConfigurationException("Compose YAML alias expansion exceeds the 64-level / 100,000 node limit.");
        Node result;
        if (node is ScalarNode scalar)
        {
            result = new ScalarNode(ComposeInterpolation.Expand(scalar.Value, env, scalar.Location,
                message => warnings?.Add(message)));
            characterBudget -= ((ScalarNode)result).Value.Length;
            if (characterBudget < 0)
                throw new ComposeConfigurationException("Expanded Compose YAML exceeds the 8,000,000-character limit.");
        }
        else if (node is SequenceNode sequence)
        {
            var items = new List<Node>();
            foreach (var item in sequence.Items)
                items.Add(InterpolateNode(item, env, ref budget, ref characterBudget, warnings, depth + 1));
            result = new SequenceNode(items);
        }
        else if (node is MappingNode mapping)
        {
            var items = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var (key, value) in mapping.Map)
                items.Add(key, InterpolateNode(value, env, ref budget, ref characterBudget, warnings, depth + 1));
            result = new MappingNode(items);
        }
        else
            result = new NullNode();
        result.Tag = node.Tag;
        return result;
    }

    private sealed class YamlReader
    {
        private readonly Parser parser;
        private readonly Dictionary<string, Node> anchors = new(StringComparer.Ordinal);
        private readonly HashSet<string> pendingAnchors = new(StringComparer.Ordinal);
        private int remaining = 100_000;

        public YamlReader(string yaml) =>
            parser = new Parser(new StringReader(yaml.Replace("\r\n", "\n").Replace('\r', '\n')));

        public MappingNode Read()
        {
            parser.Consume<StreamStart>();
            if (parser.TryConsume<StreamEnd>(out _))
                return new MappingNode(new(StringComparer.Ordinal));
            parser.Consume<DocumentStart>();
            var node = ReadNode(0);
            if (node.Tag is not null)
                throw new ComposeConfigurationException("Compose merge tags are supported on mapping values, not the document root.");
            parser.Consume<DocumentEnd>();
            if (!parser.TryConsume<StreamEnd>(out _))
                throw YamlError(parser.Current!.Start, "Multiple YAML documents are unsupported; use a separate override file.");
            return node switch
            {
                MappingNode map => map,
                NullNode => new MappingNode(new(StringComparer.Ordinal)),
                _ => throw new ComposeConfigurationException("Compose YAML must have a mapping at the document root."),
            };
        }

        private Node ReadNode(int depth)
        {
            if (depth > 64 || --remaining < 0)
                throw new ComposeConfigurationException("Compose YAML exceeds the 64-level / 100,000 node limit.");
            if (parser.TryConsume<AnchorAlias>(out var alias))
            {
                if (pendingAnchors.Contains(alias.Value.Value) || !anchors.TryGetValue(alias.Value.Value, out var referenced))
                    throw YamlError(alias.Start, "Undefined or recursive YAML alias. Define the anchor before using it.");
                return referenced;
            }

            var start = parser.Current as NodeEvent
                ?? throw new ComposeConfigurationException("Expected a YAML value.");
            if (!start.Anchor.IsEmpty && !pendingAnchors.Add(start.Anchor.Value))
                throw YamlError(start.Start, "Reusing an unfinished YAML anchor is unsupported.");
            var tag = start.Tag.IsEmpty ? null : start.Tag.Value;
            var standardTag = tag?.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal) == true
                ? tag["tag:yaml.org,2002:".Length..] : null;
            if (tag is not null && tag is not "!" and not "!reset" and not "!override" &&
                standardTag is not ("str" or "null" or "bool" or "int" or "float" or "map" or "seq" or "merge"))
                throw YamlError(start.Start, "Unsupported YAML tag. Use ordinary YAML values, !reset or !override.");
            if (standardTag is not null &&
                ((start is MappingStart && standardTag != "map") ||
                 (start is SequenceStart && standardTag != "seq") ||
                 (start is Scalar && standardTag is "map" or "seq")))
                throw YamlError(start.Start, "YAML tag does not match the value's scalar/sequence/mapping shape.");

            Node result;
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                var isNull = standardTag == "null" || (tag is null && scalar.Style == ScalarStyle.Plain &&
                    scalar.Value is "" or "~" or "null" or "Null" or "NULL");
                result = isNull ? new NullNode() : new ScalarNode(scalar.Value)
                {
                    Location = $"Compose YAML line {scalar.Start.Line}, column {scalar.Start.Column}",
                    IsMergeKey = scalar.Value == "<<" &&
                        ((tag is null && scalar.Style == ScalarStyle.Plain) || standardTag == "merge"),
                };
            }
            else if (parser.TryConsume<SequenceStart>(out _))
            {
                var items = new List<Node>();
                while (!parser.TryConsume<SequenceEnd>(out _))
                {
                    var item = ReadNode(depth + 1);
                    if (item.Tag is not null)
                        throw YamlError(start.Start, "Compose merge tags on sequence items are unsupported; tag the entire attribute instead.");
                    items.Add(item);
                }
                result = new SequenceNode(items);
            }
            else
            {
                parser.Consume<MappingStart>();
                var map = new Dictionary<string, Node>(StringComparer.Ordinal);
                var explicitKeys = new HashSet<string>(StringComparer.Ordinal);
                var merged = false;
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    var keyMark = parser.Current!.Start;
                    if (ReadNode(depth + 1) is not ScalarNode key || key.Tag is not null)
                        throw YamlError(keyMark, "Mapping keys must be untagged scalar strings.");
                    var value = ReadNode(depth + 1);
                    if (key.IsMergeKey)
                    {
                        if (merged)
                            throw YamlError(keyMark, "Duplicate YAML merge key; use a sequence of mapping aliases.");
                        merged = true;
                        var sources = value is SequenceNode sequence ? sequence.Items : [value];
                        foreach (var source in sources)
                        {
                            if (source is not MappingNode mapping)
                                throw YamlError(keyMark, "YAML merge keys require a mapping or sequence of mappings.");
                            foreach (var (k, v) in mapping.Map)
                                map.TryAdd(k, v);
                        }
                    }
                    else
                    {
                        if (!explicitKeys.Add(key.Value))
                            throw YamlError(keyMark, "Duplicate explicit mapping key. Remove or rename the duplicate.");
                        map[key.Value] = value;
                    }
                }
                result = new MappingNode(map);
            }
            result.Tag = tag is "!reset" or "!override" ? tag : null;
            // Only completed nodes are published: self/cyclic aliases cannot create recursive graphs.
            if (!start.Anchor.IsEmpty)
            {
                pendingAnchors.Remove(start.Anchor.Value);
                anchors[start.Anchor.Value] = result;
            }
            return result;
        }
    }
}
