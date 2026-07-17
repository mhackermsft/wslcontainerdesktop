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


using System.Text.Json;

namespace WslContainerDesktop.Models;

/// <summary>Parsed MVP representation of a <c>.devcontainer/devcontainer.json</c>.</summary>
public sealed class DevContainerConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string DevContainerJsonPath { get; set; } = string.Empty;
    public string? Image { get; set; }
    public DevContainerBuild? Build { get; set; }
    public string WorkspaceFolder { get; set; } = string.Empty;
    public string WorkspaceMount { get; set; } = string.Empty;
    public List<int> ForwardPorts { get; set; } = new();
    public string? RemoteUser { get; set; }
    public string? ContainerUser { get; set; }
    public Dictionary<string, string> ContainerEnv { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? PostCreateCommand { get; set; }
    public string? PostStartCommand { get; set; }
    public List<string> Warnings { get; set; } = new();
    public RunContainerOptions RunOptions { get; set; } = new();
    public string LifecycleLog { get; set; } = string.Empty;

    public string EffectiveImage => !string.IsNullOrWhiteSpace(Image) ? Image! : DevContainerImageTag(Id);

    public static string DevContainerImageTag(string id) => $"wslcontainerdesktop-devcontainer:{id}";
}

public sealed class DevContainerBuild
{
    public string Context { get; set; } = string.Empty;
    public string? Dockerfile { get; set; }
    public List<string> Args { get; set; } = new();
    public string? Target { get; set; }
}

public sealed class DevContainerImportResult
{
    public DevContainerConfig? Config { get; init; }
    public RunContainerOptions? Options { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
    public bool Success => Config is not null && Options is not null && string.IsNullOrWhiteSpace(ErrorMessage);
}
