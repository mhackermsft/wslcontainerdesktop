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


using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Imports Dev Container JSONC into single-container or Compose-backed app models. Never throws.</summary>
public sealed class DevContainerImporter(ILogger<DevContainerImporter> logger) : IDevContainerImporter
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly HashSet<string> EngineUnsupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "capAdd", "cap_add", "capDrop", "cap_drop", "devices", "sysctls", "privileged",
        "readOnly", "read_only", "init", "pid", "ipc", "macAddress", "mac_address",
    };

    public async Task<DevContainerImportResult> ImportAsync(string workspacePath, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            {
                return Fail("Choose an existing workspace folder.");
            }

            var configPath = ResolveConfigPath(workspacePath);
            if (configPath is null)
            {
                return Fail("No .devcontainer/devcontainer.json was found in the selected folder.");
            }

            await using var stream = File.OpenRead(configPath);
            using var doc = await JsonDocument.ParseAsync(stream, JsonOptions, ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail("devcontainer.json must be a JSON object.");
            }

            var root = doc.RootElement;
            var workspace = Path.GetFullPath(workspacePath);
            var repoName = new DirectoryInfo(workspace).Name;
            var id = CreateId(workspace);
            var rawEnv = ReadRawStringMap(root, "containerEnv");
            var containerWorkspaceDefault = $"/workspaces/{repoName}";
            var vars = new DevContainerVariables(workspace, containerWorkspaceDefault, id, rawEnv);
            var workspaceFolder = vars.Substitute(GetString(root, "workspaceFolder") ?? containerWorkspaceDefault);
            vars = vars with { ContainerWorkspaceFolder = workspaceFolder };
            var warnings = CollectWarnings(root);

            var config = new DevContainerConfig
            {
                Id = id,
                Name = vars.Substitute(GetString(root, "name") ?? repoName),
                WorkspacePath = workspace,
                DevContainerJsonPath = configPath,
                WorkspaceFolder = workspaceFolder,
                WorkspaceMount = vars.Substitute(GetString(root, "workspaceMount") ?? $"{workspace}:{workspaceFolder}"),
                Mounts = ReadStringList(root, "mounts", vars),
                RunArgs = ReadStringList(root, "runArgs", vars),
                RemoteUser = vars.SubstituteNullable(GetString(root, "remoteUser")),
                ContainerUser = vars.SubstituteNullable(GetString(root, "containerUser")),
                UpdateRemoteUserUid = GetBool(root, "updateRemoteUserUID") ?? GetBool(root, "updateRemoteUserUid") ?? false,
                UserEnvProbe = vars.SubstituteNullable(GetString(root, "userEnvProbe")),
                ContainerEnv = ReadStringMap(root, "containerEnv", vars),
                RemoteEnv = ReadStringMap(root, "remoteEnv", vars),
                ForwardPorts = ReadPorts(root).Distinct().Order().ToList(),
                PortsAttributes = ReadPortAttributes(root),
                Features = ReadFeatures(root, vars),
                OverrideFeatureInstallOrder = ReadStringList(root, "overrideFeatureInstallOrder", vars),
                Lifecycle = ReadLifecycle(root, vars),
                Warnings = warnings,
            };

            config.Image = vars.SubstituteNullable(GetString(root, "image"));
            config.Build = ReadBuild(root, configPath, vars);
            config.Compose = ReadCompose(root, configPath, workspace, config, vars, warnings);
            if (config.Compose is null && string.IsNullOrWhiteSpace(config.Image) && config.Build is null)
            {
                return Fail("devcontainer.json must define image, build, or dockerComposeFile.", warnings);
            }

            config.RunOptions = ToRunOptions(config);
            return new DevContainerImportResult { Config = config, Options = config.RunOptions, Warnings = warnings };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to import devcontainer from {Workspace}.", workspacePath);
            return Fail("Could not parse devcontainer.json. Check the file for invalid JSONC syntax.");
        }
    }

    private static string? ResolveConfigPath(string workspacePath)
    {
        var direct = Path.Combine(workspacePath, ".devcontainer", "devcontainer.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        var selectedFile = Path.Combine(workspacePath, "devcontainer.json");
        return File.Exists(selectedFile) ? selectedFile : null;
    }

    private static List<string> CollectWarnings(JsonElement root)
    {
        var warnings = new List<string>();
        foreach (var prop in root.EnumerateObject())
        {
            if (EngineUnsupportedKeys.Contains(prop.Name))
            {
                warnings.Add($"'{prop.Name}' is not supported by the WSL container engine and was ignored.");
            }
        }
        return warnings;
    }

    private static DevContainerBuild? ReadBuild(JsonElement root, string configPath, DevContainerVariables vars)
    {
        if (!root.TryGetProperty("build", out var build))
        {
            return null;
        }

        var devcontainerDir = Path.GetDirectoryName(configPath) ?? vars.LocalWorkspaceFolder;
        if (build.ValueKind == JsonValueKind.String)
        {
            return new DevContainerBuild
            {
                Dockerfile = ResolvePath(devcontainerDir, vars.Substitute(build.GetString() ?? "Dockerfile")),
                Context = vars.LocalWorkspaceFolder,
            };
        }

        if (build.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var context = vars.Substitute(GetString(build, "context") ?? "..");
        var dockerfile = vars.SubstituteNullable(GetString(build, "dockerfile"));
        return new DevContainerBuild
        {
            Context = ResolvePath(devcontainerDir, context),
            Dockerfile = string.IsNullOrWhiteSpace(dockerfile) ? null : ResolvePath(devcontainerDir, dockerfile),
            Target = vars.SubstituteNullable(GetString(build, "target")),
            Args = ReadStringMap(build, "args", vars).Select(kv => $"{kv.Key}={kv.Value}").ToList(),
        };
    }

    private static DevContainerCompose? ReadCompose(
        JsonElement root,
        string configPath,
        string workspace,
        DevContainerConfig config,
        DevContainerVariables vars,
        List<string> warnings)
    {
        if (!root.TryGetProperty("dockerComposeFile", out var composeElement))
        {
            return null;
        }

        var service = vars.SubstituteNullable(GetString(root, "service"));
        if (string.IsNullOrWhiteSpace(service))
        {
            warnings.Add("dockerComposeFile requires a service value; Compose dev container import was skipped.");
            return null;
        }

        var configDir = Path.GetDirectoryName(configPath) ?? workspace;
        var files = ReadStringOrArray(composeElement, vars)
            .Select(p => ResolvePath(configDir, p))
            .Where(File.Exists)
            .ToList();
        if (files.Count == 0)
        {
            warnings.Add("No referenced dockerComposeFile could be found; Compose dev container import was skipped.");
            return null;
        }

        var project = new ComposeProject
        {
            Name = "devcontainer_" + config.Id,
        };
        foreach (var file in files)
        {
            var parsed = ComposeImporter.ParseProject(File.ReadAllText(file), baseDirectory: Path.GetDirectoryName(file));
            project = MergeProjects(project, parsed);
            warnings.AddRange(parsed.Warnings);
        }

        project.Name = "devcontainer_" + config.Id;
        var runServices = ReadStringList(root, "runServices", vars);
        if (runServices.Count > 0)
        {
            project.Services.RemoveAll(s => !runServices.Contains(s.Name, StringComparer.Ordinal) && !string.Equals(s.Name, service, StringComparison.Ordinal));
        }

        var primary = project.Services.FirstOrDefault(s => string.Equals(s.Name, service, StringComparison.Ordinal));
        if (primary is null)
        {
            warnings.Add($"Compose service '{service}' was not found; Compose dev container import was skipped.");
            return null;
        }

        ApplyDevContainerToService(primary, config, vars);
        project.ApplyProjectNamespacing();
        return new DevContainerCompose
        {
            DockerComposeFiles = files,
            Service = service,
            RunServices = runServices,
            Project = project,
        };
    }

    private static ComposeProject MergeProjects(ComposeProject baseProject, ComposeProject next)
    {
        baseProject.Services.RemoveAll(s => next.Services.Any(ns => string.Equals(ns.Name, s.Name, StringComparison.Ordinal)));
        baseProject.Services.AddRange(next.Services);
        baseProject.Networks.AddRange(next.Networks.Where(n => !baseProject.Networks.Any(e => string.Equals(e.Name, n.Name, StringComparison.Ordinal))));
        baseProject.Volumes.AddRange(next.Volumes.Where(v => !baseProject.Volumes.Any(e => string.Equals(e.Name, v.Name, StringComparison.Ordinal))));
        baseProject.Secrets.AddRange(next.Secrets.Where(s => !baseProject.Secrets.Any(e => string.Equals(e.Name, s.Name, StringComparison.Ordinal))));
        baseProject.Configs.AddRange(next.Configs.Where(c => !baseProject.Configs.Any(e => string.Equals(e.Name, c.Name, StringComparison.Ordinal))));
        return baseProject;
    }

    private static void ApplyDevContainerToService(ComposeService service, DevContainerConfig config, DevContainerVariables vars)
    {
        service.Options.WorkingDir = config.WorkspaceFolder;
        service.Options.User = string.IsNullOrWhiteSpace(config.ContainerUser) ? config.RemoteUser : config.ContainerUser;
        if (!string.IsNullOrWhiteSpace(config.WorkspaceMount))
        {
            service.Options.Volumes.Add(config.WorkspaceMount);
        }
        service.Options.Volumes.AddRange(config.Mounts.Select(NormalizeMount));
        foreach (var env in config.ContainerEnv)
        {
            service.Options.EnvironmentVariables.RemoveAll(e => e.StartsWith(env.Key + "=", StringComparison.Ordinal));
            service.Options.EnvironmentVariables.Add($"{env.Key}={env.Value}");
        }
        foreach (var port in config.ForwardPorts)
        {
            var mapping = $"{port}:{port}";
            if (!service.Options.PortMappings.Contains(mapping, StringComparer.Ordinal))
            {
                service.Options.PortMappings.Add(mapping);
            }
        }
        ApplyRunArgs(service.Options, config.RunArgs, config.Warnings);
        _ = vars;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string property, DevContainerVariables vars)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in ReadRawStringMap(root, property))
        {
            values[kv.Key] = vars.Substitute(kv.Value);
        }
        return values;
    }

    private static Dictionary<string, string> ReadRawStringMap(JsonElement root, string property)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(property, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            values[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : prop.Value.GetRawText();
        }
        return values;
    }

    private static List<DevContainerFeature> ReadFeatures(JsonElement root, DevContainerVariables vars)
    {
        var features = new List<DevContainerFeature>();
        if (!root.TryGetProperty("features", out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return features;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var option in prop.Value.EnumerateObject())
                {
                    options[option.Name] = vars.Substitute(option.Value.ValueKind == JsonValueKind.String ? option.Value.GetString() ?? string.Empty : option.Value.GetRawText());
                }
            }
            features.Add(new DevContainerFeature { Id = vars.Substitute(prop.Name), Options = options, RawOptions = prop.Value.Clone() });
        }
        return features;
    }

    private static Dictionary<int, DevContainerPortAttributes> ReadPortAttributes(JsonElement root)
    {
        var result = new Dictionary<int, DevContainerPortAttributes>();
        if (!root.TryGetProperty("portsAttributes", out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            var key = prop.Name.Split('/')[0];
            if (!int.TryParse(key, out var port) || prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            result[port] = new DevContainerPortAttributes
            {
                Label = GetString(prop.Value, "label"),
                Protocol = GetString(prop.Value, "protocol"),
                OnAutoForward = GetString(prop.Value, "onAutoForward"),
            };
        }
        return result;
    }

    private static DevContainerLifecycle ReadLifecycle(JsonElement root, DevContainerVariables vars) => new()
    {
        Initialize = ReadCommands(root, "initializeCommand", vars),
        OnCreate = ReadCommands(root, "onCreateCommand", vars),
        UpdateContent = ReadCommands(root, "updateContentCommand", vars),
        PostCreate = ReadCommands(root, "postCreateCommand", vars),
        PostStart = ReadCommands(root, "postStartCommand", vars),
        PostAttach = ReadCommands(root, "postAttachCommand", vars),
    };

    private static List<string> ReadCommands(JsonElement root, string property, DevContainerVariables vars)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return new List<string>();
        }
        return ReadCommandValue(value, vars);
    }

    private static List<string> ReadCommandValue(JsonElement value, DevContainerVariables vars) => value.ValueKind switch
    {
        JsonValueKind.String => [vars.Substitute(value.GetString() ?? string.Empty)],
        JsonValueKind.Array => value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? vars.Substitute(v.GetString() ?? string.Empty) : null).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()!,
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? vars.Substitute(p.Value.GetString() ?? string.Empty) : null).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()!,
        _ => new List<string>(),
    };

    private static List<int> ReadPorts(JsonElement root)
    {
        var ports = new List<int>();
        AddPorts(root, "forwardPorts", ports);
        AddPorts(root, "appPort", ports);
        return ports;
    }

    private static void AddPorts(JsonElement root, string property, List<int> ports)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                AddPort(item, ports);
            }
        }
        else
        {
            AddPort(value, ports);
        }
    }

    private static void AddPort(JsonElement value, List<int> ports)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port > 0)
        {
            ports.Add(port);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            var first = text?.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('/')[0];
            if (int.TryParse(first, out port) && port > 0)
            {
                ports.Add(port);
            }
        }
    }

    private static List<string> ReadStringList(JsonElement root, string property, DevContainerVariables vars)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return new List<string>();
        }
        return ReadStringOrArray(value, vars);
    }

    private static List<string> ReadStringOrArray(JsonElement value, DevContainerVariables vars)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return [vars.Substitute(value.GetString() ?? string.Empty)];
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? vars.Substitute(v.GetString() ?? string.Empty) : null).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()!;
        }
        return new List<string>();
    }

    private static RunContainerOptions ToRunOptions(DevContainerConfig config)
    {
        var options = new RunContainerOptions
        {
            Image = config.EffectiveImage,
            Name = $"devcontainer-{config.Id}",
            Detached = true,
            WorkingDir = config.WorkspaceFolder,
            User = string.IsNullOrWhiteSpace(config.ContainerUser) ? config.RemoteUser : config.ContainerUser,
            Command = "sleep infinity",
        };
        options.Volumes.Add(config.WorkspaceMount);
        options.Volumes.AddRange(config.Mounts.Select(NormalizeMount));
        foreach (var env in config.ContainerEnv)
        {
            options.EnvironmentVariables.Add($"{env.Key}={env.Value}");
        }
        foreach (var port in config.ForwardPorts)
        {
            options.PortMappings.Add($"{port}:{port}");
        }
        ApplyRunArgs(options, config.RunArgs, config.Warnings);
        options.Labels["com.wslcontainerdesktop.kind"] = "devcontainer";
        options.Labels["com.wslcontainerdesktop.devcontainer.id"] = config.Id;
        options.Labels["com.wslcontainerdesktop.devcontainer.name"] = config.Name;
        return options;
    }

    private static void ApplyRunArgs(RunContainerOptions options, IReadOnlyList<string> runArgs, List<string> warnings)
    {
        for (var i = 0; i < runArgs.Count; i++)
        {
            var arg = runArgs[i];
            var value = i + 1 < runArgs.Count ? runArgs[i + 1] : null;
            switch (arg)
            {
                case "--network" when !string.IsNullOrWhiteSpace(value): options.Network = value; i++; break;
                case "--hostname" when !string.IsNullOrWhiteSpace(value): options.Hostname = value; i++; break;
                case "--user" or "-u" when !string.IsNullOrWhiteSpace(value): options.User = value; i++; break;
                case "--workdir" or "-w" when !string.IsNullOrWhiteSpace(value): options.WorkingDir = value; i++; break;
                case "--env" or "-e" when !string.IsNullOrWhiteSpace(value): options.EnvironmentVariables.Add(value); i++; break;
                case "--volume" or "-v" when !string.IsNullOrWhiteSpace(value): options.Volumes.Add(NormalizeMount(value)); i++; break;
                case "--publish" or "-p" when !string.IsNullOrWhiteSpace(value): options.PortMappings.Add(value); i++; break;
                case "--dns" when !string.IsNullOrWhiteSpace(value): options.Dns.Add(value); i++; break;
                case "--dns-search" when !string.IsNullOrWhiteSpace(value): options.DnsSearch.Add(value); i++; break;
                case "--dns-option" when !string.IsNullOrWhiteSpace(value): options.DnsOptions.Add(value); i++; break;
                case "--tmpfs" when !string.IsNullOrWhiteSpace(value): options.Tmpfs.Add(value); i++; break;
                case "--ulimit" when !string.IsNullOrWhiteSpace(value): options.Ulimits.Add(value); i++; break;
                case "--shm-size" when !string.IsNullOrWhiteSpace(value): options.ShmSize = value; i++; break;
                case "--cpus" when !string.IsNullOrWhiteSpace(value): options.CpuLimit = value; i++; break;
                case "--memory" or "-m" when !string.IsNullOrWhiteSpace(value): options.MemoryLimit = value; i++; break;
                case "--gpus" when string.Equals(value, "all", StringComparison.OrdinalIgnoreCase): options.AllGpus = true; i++; break;
                case "--privileged" or "--cap-add" or "--cap-drop" or "--device" or "--sysctl" or "--read-only" or "--init" or "--pid" or "--ipc" or "--mac-address":
                    warnings.Add($"runArgs '{arg}' is not supported by the WSL container engine and was ignored.");
                    if (value is not null && !value.StartsWith("-", StringComparison.Ordinal)) { i++; }
                    break;
            }
        }
    }

    private static string NormalizeMount(string mount)
    {
        const string sourceMarker = "source=";
        const string targetMarker = "target=";
        if (!mount.Contains(sourceMarker, StringComparison.OrdinalIgnoreCase) || !mount.Contains(targetMarker, StringComparison.OrdinalIgnoreCase))
        {
            return mount;
        }
        var parts = mount.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var source = parts.FirstOrDefault(p => p.StartsWith(sourceMarker, StringComparison.OrdinalIgnoreCase))?[sourceMarker.Length..];
        var target = parts.FirstOrDefault(p => p.StartsWith(targetMarker, StringComparison.OrdinalIgnoreCase))?[targetMarker.Length..];
        var readOnly = parts.Any(p => p.Equals("readonly", StringComparison.OrdinalIgnoreCase) || p.Equals("ro", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) ? mount : source + ":" + target + (readOnly ? ":ro" : string.Empty);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    private static string CreateId(string workspace)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspace).ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static DevContainerImportResult Fail(string message, IReadOnlyList<string>? warnings = null) => new()
    {
        ErrorMessage = message,
        Warnings = warnings ?? Array.Empty<string>(),
    };

    private sealed record DevContainerVariables(
        string LocalWorkspaceFolder,
        string ContainerWorkspaceFolder,
        string DevContainerId,
        IReadOnlyDictionary<string, string> ContainerEnv)
    {
        public string Substitute(string value)
        {
            var result = value
                .Replace("${localWorkspaceFolder}", LocalWorkspaceFolder, StringComparison.Ordinal)
                .Replace("${containerWorkspaceFolder}", ContainerWorkspaceFolder, StringComparison.Ordinal)
                .Replace("${localWorkspaceFolderBasename}", Path.GetFileName(LocalWorkspaceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), StringComparison.Ordinal)
                .Replace("${containerWorkspaceFolderBasename}", ContainerWorkspaceFolder.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty, StringComparison.Ordinal)
                .Replace("${devcontainerId}", DevContainerId, StringComparison.Ordinal);
            result = SubstituteScoped(result, "${localEnv:", name => Environment.GetEnvironmentVariable(name) ?? string.Empty);
            result = SubstituteScoped(result, "${containerEnv:", name => ContainerEnv.TryGetValue(name, out var value) ? value : string.Empty);
            return result;
        }

        public string? SubstituteNullable(string? value) => string.IsNullOrEmpty(value) ? value : Substitute(value);

        private static string SubstituteScoped(string value, string marker, Func<string, string> resolve)
        {
            var result = value;
            var start = result.IndexOf(marker, StringComparison.Ordinal);
            while (start >= 0)
            {
                var end = result.IndexOf('}', start + marker.Length);
                if (end < 0)
                {
                    break;
                }
                var name = result[(start + marker.Length)..end];
                var replacement = resolve(name);
                result = result[..start] + replacement + result[(end + 1)..];
                start = result.IndexOf(marker, start + replacement.Length, StringComparison.Ordinal);
            }
            return result;
        }
    }
}
