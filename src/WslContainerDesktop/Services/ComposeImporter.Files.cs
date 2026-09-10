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

namespace WslContainerDesktop.Services;

public static partial class ComposeImporter
{
    private static readonly string[] ResourceKinds = ["services", "networks", "volumes", "secrets", "configs"];

    // Logical source breadcrumbs identify the offending input without printing interpolated paths,
    // which may contain credentials. All state belongs to one import, never a global file cache.
    private sealed class FileGraph(List<string> warnings)
    {
        private readonly HashSet<string> _includes = new(PathComparer);
        private readonly HashSet<(string File, string Service)> _extends = [];
        private int _reads;

        public MappingNode LoadFiles(IReadOnlyList<string> paths, string? directory,
            IReadOnlyDictionary<string, string> env)
        {
            MappingNode? root = null;
            for (var i = 0; i < paths.Count; i++)
            {
                var source = $"Compose files[{i + 1}]";
                var part = Decode(ReadInput(paths[i], true, source)!, directory, env, source);
                root = root is null ? part : MergeMappings(root, part);
            }
            return Finish(root!, directory, env, PathIdentity(paths[0]), 0, reportTopLevelWarnings: false);
        }

        public MappingNode LoadMain(string yaml, string? directory, IReadOnlyDictionary<string, string> env)
        {
            var root = Decode(yaml, directory, env, "Compose input");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                foreach (var candidate in OverrideFileNames)
                {
                    var source = "Compose input override";
                    var text = ReadInput(ResolvePath(candidate, directory), false, source);
                    if (text is null) continue;
                    root = MergeMappings(root, Decode(text, directory, env, source));
                    break;
                }
            }
            return Finish(root, directory, env, "<main>", 0);
        }

        private MappingNode Decode(string text, string? directory,
            IReadOnlyDictionary<string, string> env, string source)
        {
            if (++_reads > 256)
                throw FileError(source, "file graph exceeds the 256-document limit");
            MappingNode root;
            var firstWarning = warnings.Count;
            try
            {
                root = ReadComposeYaml(text, env, warnings);
            }
            catch (ComposeConfigurationException ex)
            {
                throw new ComposeConfigurationException($"{source}: {ex.Message}");
            }
            if (source != "Compose input")
                for (var i = firstWarning; i < warnings.Count; i++)
                    warnings[i] = source + ": " + warnings[i];
            SetSource(root, source);
            ResolveDocumentPaths(root, directory);
            return root;
        }

        private MappingNode Finish(MappingNode root, string? directory,
            IReadOnlyDictionary<string, string> env, string identity, int depth, bool reportTopLevelWarnings = true)
        {
            if (depth > 64) throw FileError(root.Source, "file graph exceeds the 64-level limit");
            var includedProjects = new List<MappingNode>();
            var includeNode = root.Child("include");
            if (includeNode is not null && includeNode.Tag != "!reset")
            {
                if (includeNode is not SequenceNode includes)
                    throw FileError(root.Source + " include", "unsupported form; expected a list");
                for (var i = 0; i < includes.Items.Count; i++)
                    includedProjects.Add(LoadInclude(includes.Items[i], directory, env,
                        $"{includeNode.Source} include[{i + 1}]", depth + 1));
            }

            // An included project has its own inheritance namespace. It is not an override
            // layer, and extends must not accidentally find a service in a different project.
            if (root.Child("services") is MappingNode services && services.Tag != "!reset")
            {
                var resolved = new Dictionary<string, Node>(StringComparer.Ordinal);
                foreach (var (name, node) in services.Map)
                    resolved[name] = node is MappingNode service && node.Tag != "!reset"
                        ? ResolveService(name, service, services, env, identity, depth)
                        : node;
                root.Map["services"] = new MappingNode(resolved) { Source = services.Source };
            }
            root = (MappingNode)ApplyTags(StripKey(root, "include"));
            if (root.Child("services") is MappingNode completedServices)
                foreach (var service in completedServices.Map.Values.OfType<MappingNode>())
                    if (service.Child("build") is MappingNode build && build.Child("context") is null or NullNode)
                        build.Map["context"] = new ScalarNode(build.DefaultBuildContext ?? ResolvePath(".", directory));
            ValidateResourceShapes(root);
            if (identity != "<main>" && reportTopLevelWarnings)
            {
                var firstWarning = warnings.Count;
                CollectTopLevelWarnings(root, warnings);
                for (var i = firstWarning; i < warnings.Count; i++)
                    warnings[i] = root.Source + ": " + warnings[i];
            }
            foreach (var included in includedProjects)
            foreach (var kind in ResourceKinds)
            {
                if (included.Child(kind) is not MappingNode resources) continue;
                if (root.Child(kind) is null or NullNode)
                    root.Map[kind] = new MappingNode(new(StringComparer.Ordinal));
                if (root.Child(kind) is not MappingNode destination)
                    throw FileError(root.Source + " " + kind, "expected a resource mapping");
                foreach (var (name, resource) in resources.Map)
                {
                    if (destination.Map.TryGetValue(name, out var existing))
                    {
                        if (EqualNodes(existing, resource)) continue;
                        throw FileError(resource.Source + " " + kind,
                            $"duplicate resource conflicts with {existing.Source}; include does not merge resources");
                    }
                    destination.Map.Add(name, resource);
                }
            }
            return root;
        }

        private MappingNode LoadInclude(Node item, string? directory,
            IReadOnlyDictionary<string, string> parentEnv, string source, int depth)
        {
            Node paths;
            MappingNode? options = null;
            if (item is ScalarNode) paths = item;
            else if (item is MappingNode map)
            {
                options = map;
                CheckKeys(map, ["path", "project_directory", "env_file"], source);
                paths = map.Child("path") ?? throw FileError(source + ".path", "required field is missing");
            }
            else throw FileError(source, "unsupported form; expected a path or mapping");

            var filenames = PathValues(paths, source + ".path")
                .Select(p => RequiredPath(p, directory, source + ".path")).ToList();
            var projectDirectory = options?.Child("project_directory") is { } projectDir
                ? RequiredPath(RequiredScalar(projectDir, source + ".project_directory"), directory, source + ".project_directory")
                : (Path.GetDirectoryName(filenames[0]) ?? Path.GetPathRoot(filenames[0]))!;
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (options?.Child("env_file") is { } envFiles)
            {
                var index = 0;
                foreach (var file in PathValues(envFiles, source + ".env_file"))
                {
                    var context = $"{source}.env_file[{++index}]";
                    foreach (var pair in ReadEnvFile(RequiredPath(file, directory, context), true, context))
                        env[pair.Key] = pair.Value;
                }
            }
            else
            {
                foreach (var pair in ReadEnvFile(Path.Combine(projectDirectory, ".env"), false, source + " .env"))
                    env[pair.Key] = pair.Value;
            }
            foreach (var pair in parentEnv) env[pair.Key] = pair.Value;

            var entered = new List<string>();
            try
            {
                MappingNode? root = null;
                for (var i = 0; i < filenames.Count; i++)
                {
                    var path = filenames[i];
                    var context = $"{source}.path[{i + 1}]";
                    if (!entered.Contains(path, PathComparer))
                    {
                        if (!_includes.Add(path)) throw FileError(context, "include cycle detected");
                        entered.Add(path);
                    }
                    var part = Decode(ReadInput(path, true, context)!, projectDirectory, env, context);
                    root = root is null ? part : MergeMappings(root, part);
                }
                return Finish(root!, projectDirectory, env, PathIdentity(filenames[0]), depth);
            }
            finally
            {
                foreach (var path in entered) _includes.Remove(path);
            }
        }

        private MappingNode ResolveService(string name, MappingNode service, MappingNode services,
            IReadOnlyDictionary<string, string> env, string identity, int depth)
        {
            var ext = service.Child("extends");
            if (ext is null || ext.Tag == "!reset") return StripKey(service, "extends");
            var context = ext.Source + " extends";
            if (ext is not MappingNode rawReference)
                throw FileError(context, "unsupported form; expected a mapping with service and optional file");
            var reference = (MappingNode)ApplyTags(rawReference);
            CheckKeys(reference, ["service", "file"], context);
            var target = RequiredScalar(reference.Child("service"), context + ".service");
            if (depth > 64) throw FileError(context, "extends graph exceeds the 64-level limit");
            var key = (identity, name);
            if (!_extends.Add(key)) throw FileError(context, "extends cycle detected");
            try
            {
                var baseServices = services;
                var baseIdentity = identity;
                if (reference.Child("file") is { } file)
                {
                    var path = RequiredScalar(file, context + ".file");
                    baseIdentity = PathIdentity(path);
                    var root = Decode(ReadInput(path, true, context + ".file")!,
                        Path.GetDirectoryName(path), env, context + ".file");
                    baseServices = root.Child("services") as MappingNode
                        ?? throw FileError(context + ".file services", "referenced service mapping is missing");
                }
                if (baseServices.Child(target) is not MappingNode basis || basis.Tag == "!reset")
                    throw FileError(context + ".service", "referenced service is missing or invalid");
                var resolved = ResolveService(target, basis, baseServices, env, baseIdentity, depth + 1);
                return ExtendService(resolved, StripKey(service, "extends"));
            }
            finally
            {
                _extends.Remove(key);
            }
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string PathIdentity(string path) =>
        OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static ComposeConfigurationException FileError(string context, string reason) =>
        new($"{context}: {reason}. Check the referenced input and its configuration.");

    private static string RequiredScalar(Node? node, string context) =>
        node is ScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value : throw FileError(context, "expected a nonempty string");

    private static IEnumerable<string> PathValues(Node node, string context)
    {
        if (node is ScalarNode) return [RequiredScalar(node, context)];
        if (node is SequenceNode sequence && sequence.Items.Count > 0)
            return sequence.Items.Select(n => RequiredScalar(n, context)).ToList();
        throw FileError(context, "unsupported form; expected a path or nonempty path list");
    }

    private static void CheckKeys(MappingNode map, string[] allowed, string context)
    {
        if (map.Map.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal)))
            throw FileError(context, "unsupported option");
    }

    private static void SetSource(Node node, string source)
    {
        node.Source = source;
        if (node is MappingNode mapping)
        {
            foreach (var (key, child) in mapping.Map)
            {
                // Resource names are also user-controlled. Ordinals remain useful without
                // leaking an environment substitution used as a path or resource name.
                if (ResourceKinds.Contains(key) && child is MappingNode resources)
                {
                    resources.Source = source + " " + key;
                    var index = 0;
                    foreach (var resource in resources.Map.Values)
                        SetSource(resource, resources.Source + $"[{++index}]");
                }
                else SetSource(child, source);
            }
        }
        else if (node is SequenceNode sequence)
            foreach (var child in sequence.Items) SetSource(child, source);
    }

    private static void ResolveDocumentPaths(MappingNode root, string? directory)
    {
        if (root.Child("services") is MappingNode services && services.Tag != "!reset")
        foreach (var service in services.Map.Values.OfType<MappingNode>().Where(s => s.Tag != "!reset"))
        {
            if (service.Child("extends") is MappingNode ext && ext.Tag != "!reset" &&
                ext.Child("file") is { } file && file.Tag != "!reset")
                ext.Map["file"] = PathNode(file, directory, ext.Source + " extends.file", required: true);
            if (service.Child("env_file") is SequenceNode envFiles && envFiles.Tag != "!reset")
            {
                for (var i = 0; i < envFiles.Items.Count; i++)
                {
                    var item = envFiles.Items[i];
                    var context = $"{item.Source} env_file[{i + 1}]";
                    if (item is MappingNode options)
                    {
                        ValidateEnvFile(options, context);
                        options.Map["path"] = PathNode(options.Child("path")!, directory, context, required: true);
                    }
                    else envFiles.Items[i] = PathNode(item, directory, context, required: true);
                }
            }
            if (service.Child("build") is MappingNode build && build.Tag != "!reset")
            {
                build.DefaultBuildContext = ResolvePath(".", directory);
                if (build.Child("context") is { } context && context is not NullNode && context.Tag != "!reset")
                    build.Map["context"] = PathNode(context, directory, build.Source + " build.context");
            }
            if (service.Child("volumes") is SequenceNode mounts && mounts.Tag != "!reset")
            foreach (var mount in mounts.Items.OfType<MappingNode>())
                if (mount.Child("source") is ScalarNode source &&
                    (mount.Scalar("type") == "bind" || mount.Scalar("type") is null && IsBindSource(source.Value)))
                    mount.Map["source"] = PathNode(source, directory, mount.Source + " volumes.source");
        }
        foreach (var kind in new[] { "secrets", "configs" })
            if (root.Child(kind) is MappingNode resources && resources.Tag != "!reset")
                foreach (var resource in resources.Map.Values.OfType<MappingNode>().Where(r => r.Tag != "!reset"))
                    if (resource.Child("file") is { } file && file is not NullNode && file.Tag != "!reset")
                        resource.Map["file"] = PathNode(file, directory, resource.Source + ".file", required: true);
    }

    private static ScalarNode PathNode(Node node, string? directory, string context, bool required = false)
    {
        var path = RequiredScalar(node, context);
        if (required)
            return new ScalarNode(RequiredPath(path, directory, context)) { Tag = node.Tag, Source = node.Source };
        try
        {
            return new ScalarNode(ResolvePath(path, directory)) { Tag = node.Tag, Source = node.Source };
        }
        catch (ComposeConfigurationException)
        {
            throw FileError(context, "invalid or drive-relative path");
        }
    }

    private static string RequiredPath(string path, string? directory, string context)
    {
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("git@", StringComparison.Ordinal))
            throw FileError(context, "unsupported remote input; use a local file");
        if (string.IsNullOrWhiteSpace(directory) && !IsAbsolutePath(path))
            throw FileError(context, "relative file requires a source project directory");
        try
        {
            var resolved = ResolvePath(path, directory);
            // These are host inputs, unlike Linux bind/build paths handed to the engine.
            return Path.GetFullPath(resolved);
        }
        catch (Exception ex) when (ex is ComposeConfigurationException or ArgumentException or NotSupportedException or IOException)
        {
            throw FileError(context, "invalid path");
        }
    }

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static bool IsBindSource(string path) =>
        IsAbsolutePath(path) || path is "." or ".." ||
        path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal) ||
        path.StartsWith(@".\", StringComparison.Ordinal) || path.StartsWith(@"..\", StringComparison.Ordinal) ||
        path.StartsWith('\\') || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private static string? ReadInput(string path, bool required, string context)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 8_000_000) throw FileError(context, "input exceeds the 8 MB limit");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!required) return null;
            throw FileError(context, "required file is missing");
        }
        catch (DecoderFallbackException)
        {
            throw FileError(context, "malformed text encoding");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw FileError(context, "file is unreadable; check permissions and sharing");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw FileError(context, "invalid file path");
        }
    }

    private static bool ValidateEnvFile(MappingNode map, string context)
    {
        CheckKeys(map, ["path", "required"], context);
        _ = RequiredScalar(map.Child("path"), context + ".path");
        if (map.Child("required") is null) return true;
        var value = RequiredScalar(map.Child("required"), context + ".required");
        if (!bool.TryParse(value, out var required))
            throw FileError(context + ".required", "expected true or false");
        return required;
    }

    private static void ValidateResourceShapes(MappingNode root)
    {
        foreach (var kind in ResourceKinds)
        {
            if (root.Child(kind) is null or NullNode) continue;
            if (root.Child(kind) is not MappingNode resources)
                throw FileError(root.Source + " " + kind, "expected a resource mapping");
            foreach (var (name, value) in resources.Map)
                if (string.IsNullOrWhiteSpace(name) || value is not MappingNode &&
                    !(kind is "volumes" or "networks" && value is NullNode))
                    throw FileError(value.Source, "invalid resource definition; expected a mapping");
        }
    }

    private static void ValidateFileResources(MappingNode root)
    {
        foreach (var kind in new[] { "secrets", "configs" })
        {
            if (root.Child(kind) is null or NullNode) continue;
            if (root.Child(kind) is not MappingNode resources)
                throw FileError(root.Source + " " + kind, "expected a resource mapping");
            foreach (var value in resources.Map.Values)
            {
                if (value is not MappingNode resource)
                    throw FileError(value.Source, "unsupported resource; expected file or external mapping");
                var context = resource.Source;
                CheckKeys(resource, ["file", "external", "name"], context);
                if (resource.Child("name") is { } resourceName)
                    _ = RequiredScalar(resourceName, context + ".name");
                if (resource.Child("external") is MappingNode external)
                {
                    CheckKeys(external, ["name"], context + ".external");
                    _ = RequiredScalar(external.Child("name"), context + ".external.name");
                }
                else if (resource.Child("external") is { } flag &&
                    (flag is not ScalarNode scalar || !bool.TryParse(scalar.Value, out _)))
                    throw FileError(context + ".external", "expected true or false");
                if (IsExternal(resource.Child("external")))
                {
                    if (resource.Child("file") is not null)
                        throw FileError(context, "external resource cannot also specify file");
                    continue;
                }
                var file = RequiredScalar(resource.Child("file"), context + ".file");
                try
                {
                    // File-backed resources may be binary. Check readability without parsing,
                    // retaining contents, or including an OS exception/path in diagnostics.
                    using var stream = File.OpenRead(file);
                    _ = stream.ReadByte();
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw FileError(context + ".file", "required file is missing");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw FileError(context + ".file", "file is unreadable; check permissions and sharing");
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    throw FileError(context + ".file", "invalid file path");
                }
            }
        }
        if (root.Child("services") is not MappingNode services) return;
        foreach (var service in services.Map.Values.OfType<MappingNode>())
        foreach (var kind in new[] { "secrets", "configs" })
            if (service.Child(kind) is SequenceNode refs)
                foreach (var reference in refs.Items.OfType<MappingNode>())
                    if (root.Child(kind) is not MappingNode declared || reference.Scalar("source") is not { } name ||
                        !declared.Map.ContainsKey(name))
                        throw FileError(service.Source + " " + kind, "referenced resource is not declared; extends does not import resources");
    }
}
