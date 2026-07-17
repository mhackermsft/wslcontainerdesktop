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

/// <summary>Imports the MVP subset of the Dev Container spec into app run options. Never throws.</summary>
public sealed class DevContainerImporter(ILogger<DevContainerImporter> logger) : IDevContainerImporter
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "image", "build", "workspaceFolder", "workspaceMount", "forwardPorts",
        "appPort", "remoteUser", "containerUser", "containerEnv", "features",
        "postCreateCommand", "postStartCommand",
    };

    private static readonly HashSet<string> KnownIgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "customizations", "remoteEnv", "mounts", "runArgs", "initializeCommand",
        "onCreateCommand", "updateContentCommand", "postAttachCommand", "portsAttributes",
        "otherPortsAttributes", "dockerComposeFile", "service", "runServices", "overrideCommand",
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
            var workspaceFolder = SubstituteRequired(GetString(root, "workspaceFolder") ?? $"/workspaces/{repoName}", workspace, $"/workspaces/{repoName}", id);
            var warnings = CollectWarnings(root);

            var config = new DevContainerConfig
            {
                Id = id,
                Name = SubstituteRequired(GetString(root, "name") ?? repoName, workspace, workspaceFolder, id),
                WorkspacePath = workspace,
                DevContainerJsonPath = configPath,
                WorkspaceFolder = workspaceFolder,
                WorkspaceMount = SubstituteRequired(GetString(root, "workspaceMount") ?? $"{workspace}:{workspaceFolder}", workspace, workspaceFolder, id),
                RemoteUser = Substitute(GetString(root, "remoteUser"), workspace, workspaceFolder, id),
                ContainerUser = Substitute(GetString(root, "containerUser"), workspace, workspaceFolder, id),
                PostCreateCommand = ReadCommand(root, "postCreateCommand", workspace, workspaceFolder, id),
                PostStartCommand = ReadCommand(root, "postStartCommand", workspace, workspaceFolder, id),
                Warnings = warnings,
            };

            config.Image = Substitute(GetString(root, "image"), workspace, workspaceFolder, id);
            config.Build = ReadBuild(root, configPath, workspace, workspaceFolder, id);
            if (string.IsNullOrWhiteSpace(config.Image) && config.Build is null)
            {
                warnings.Add("No image or build was found; import needs one of them for the MVP.");
                return Fail("devcontainer.json must define image or build for the MVP.", warnings);
            }

            config.ContainerEnv = ReadStringMap(root, "containerEnv", workspace, workspaceFolder, id);
            config.ForwardPorts = ReadPorts(root).Distinct().Order().ToList();
            config.Features = ReadFeatures(root, warnings);
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
            if (KnownIgnoredKeys.Contains(prop.Name))
            {
                warnings.Add($"{prop.Name} is parsed for preview but ignored by the Phase 1 MVP.");
            }
            else if (!SupportedKeys.Contains(prop.Name))
            {
                warnings.Add($"{prop.Name} is not supported by the Phase 1 MVP and was ignored.");
            }
        }

        if (root.TryGetProperty("dockerComposeFile", out _))
        {
            warnings.Add("dockerComposeFile-based dev containers are deferred to Phase 2; import as Compose for now.");
        }

        return warnings;
    }

    private static DevContainerBuild? ReadBuild(JsonElement root, string configPath, string workspace, string containerWorkspace, string id)
    {
        if (!root.TryGetProperty("build", out var build))
        {
            return null;
        }

        var devcontainerDir = Path.GetDirectoryName(configPath) ?? workspace;
        if (build.ValueKind == JsonValueKind.String)
        {
            return new DevContainerBuild
            {
                Dockerfile = ResolvePath(devcontainerDir, SubstituteRequired(build.GetString() ?? "Dockerfile", workspace, containerWorkspace, id)),
                Context = workspace,
            };
        }

        if (build.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var context = Substitute(GetString(build, "context") ?? "..", workspace, containerWorkspace, id) ?? "..";
        var dockerfile = Substitute(GetString(build, "dockerfile"), workspace, containerWorkspace, id);
        var result = new DevContainerBuild
        {
            Context = ResolvePath(devcontainerDir, context),
            Dockerfile = string.IsNullOrWhiteSpace(dockerfile) ? null : ResolvePath(devcontainerDir, dockerfile),
            Target = Substitute(GetString(build, "target"), workspace, containerWorkspace, id),
            Args = ReadStringMap(build, "args", workspace, containerWorkspace, id).Select(kv => $"{kv.Key}={kv.Value}").ToList(),
        };
        return result;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string property, string workspace, string containerWorkspace, string id)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(property, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString()
                : prop.Value.GetRawText();
            values[prop.Name] = Substitute(value, workspace, containerWorkspace, id) ?? string.Empty;
        }

        return values;
    }

    private static Dictionary<string, JsonElement> ReadFeatures(JsonElement root, List<string> warnings)
    {
        var features = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("features", out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return features;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            features[prop.Name] = prop.Value.Clone();
        }

        if (features.Count > 0)
        {
            warnings.Add($"{features.Count} Feature(s) were parsed but not installed; Feature resolution is deferred.");
        }

        return features;
    }

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

    private static string? ReadCommand(JsonElement root, string property, string workspace, string containerWorkspace, string id)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }

        var command = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join(" && ", value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.Object => string.Join(" && ", value.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null).Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => null,
        };
        return Substitute(command, workspace, containerWorkspace, id);
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
        foreach (var env in config.ContainerEnv)
        {
            options.EnvironmentVariables.Add($"{env.Key}={env.Value}");
        }

        foreach (var port in config.ForwardPorts)
        {
            options.PortMappings.Add($"{port}:{port}");
        }

        options.Labels["com.wslcontainerdesktop.kind"] = "devcontainer";
        options.Labels["com.wslcontainerdesktop.devcontainer.id"] = config.Id;
        options.Labels["com.wslcontainerdesktop.devcontainer.name"] = config.Name;
        return options;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Substitute(string? value, string workspace, string containerWorkspace, string id)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = value.Replace("${localWorkspaceFolder}", workspace, StringComparison.Ordinal)
            .Replace("${containerWorkspaceFolder}", containerWorkspace, StringComparison.Ordinal)
            .Replace("${devcontainerId}", id, StringComparison.Ordinal);

        const string marker = "${localEnv:";
        var start = result.IndexOf(marker, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = result.IndexOf('}', start + marker.Length);
            if (end < 0)
            {
                break;
            }

            var variable = result[(start + marker.Length)..end];
            var env = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
            result = result[..start] + env + result[(end + 1)..];
            start = result.IndexOf(marker, start + env.Length, StringComparison.Ordinal);
        }

        return result;
    }

    private static string SubstituteRequired(string value, string workspace, string containerWorkspace, string id) =>
        Substitute(value, workspace, containerWorkspace, id) ?? string.Empty;

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
}
