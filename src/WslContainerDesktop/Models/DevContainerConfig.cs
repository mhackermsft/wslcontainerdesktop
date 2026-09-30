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

/// <summary>Parsed representation of a <c>.devcontainer/devcontainer.json</c>.</summary>
public sealed class DevContainerConfig
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the workspace path.</summary>
    public string WorkspacePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the dev container json path.</summary>
    public string DevContainerJsonPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the image.</summary>
    public string? Image { get; set; }
    /// <summary>Gets or sets the build.</summary>
    public DevContainerBuild? Build { get; set; }
    /// <summary>Gets or sets the compose.</summary>
    public DevContainerCompose? Compose { get; set; }
    /// <summary>Gets or sets the workspace folder.</summary>
    public string WorkspaceFolder { get; set; } = string.Empty;
    /// <summary>Gets or sets the workspace mount.</summary>
    public string WorkspaceMount { get; set; } = string.Empty;
    /// <summary>Gets or sets the mounts.</summary>
    public List<string> Mounts { get; set; } = new();
    /// <summary>Gets or sets the run args.</summary>
    public List<string> RunArgs { get; set; } = new();
    /// <summary>Gets or sets the forward ports.</summary>
    public List<int> ForwardPorts { get; set; } = new();
    /// <summary>Gets or sets the ports attributes.</summary>
    public Dictionary<int, DevContainerPortAttributes> PortsAttributes { get; set; } = new();
    /// <summary>Gets or sets the remote user.</summary>
    public string? RemoteUser { get; set; }
    /// <summary>Gets or sets the container user.</summary>
    public string? ContainerUser { get; set; }
    /// <summary>Gets or sets a value indicating whether the update remote user uid flag is set.</summary>
    public bool UpdateRemoteUserUid { get; set; }
    /// <summary>Gets or sets the user env probe.</summary>
    public string? UserEnvProbe { get; set; }
    /// <summary>Gets or sets the container env.</summary>
    public Dictionary<string, string> ContainerEnv { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets or sets the remote env.</summary>
    public Dictionary<string, string> RemoteEnv { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Gets or sets the features.</summary>
    public List<DevContainerFeature> Features { get; set; } = new();
    /// <summary>Gets or sets the override feature install order.</summary>
    public List<string> OverrideFeatureInstallOrder { get; set; } = new();
    /// <summary>Gets or sets the lifecycle.</summary>
    public DevContainerLifecycle Lifecycle { get; set; } = new();
    /// <summary>Gets or sets the warnings.</summary>
    public List<string> Warnings { get; set; } = new();
    /// <summary>Gets or sets the run options.</summary>
    public RunContainerOptions RunOptions { get; set; } = new();
    /// <summary>Gets or sets the lifecycle log.</summary>
    public string LifecycleLog { get; set; } = string.Empty;
    /// <summary>Gets or sets the compose lifecycle progress.</summary>
    public DevContainerComposeLifecycleProgress? ComposeLifecycleProgress { get; set; }

    /// <summary>Gets a value indicating whether the app uses s compose.</summary>
    public bool UsesCompose => Compose is not null;
    /// <summary>Gets the effective image.</summary>
    public string EffectiveImage => !string.IsNullOrWhiteSpace(Image) ? Image! : DevContainerImageTag(Id);

    /// <summary>Performs the base image tag helper used by this model or dialog.</summary>
    /// <param name="id">The id value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static string BaseImageTag(string id) => $"wslcontainerdesktop-devcontainer-base:{id}";
    /// <summary>Performs the dev container image tag helper used by this model or dialog.</summary>
    /// <param name="id">The id value supplied by the caller.</param>
    /// <returns>The requested value for the caller.</returns>
    public static string DevContainerImageTag(string id) => $"wslcontainerdesktop-devcontainer:{id}";
}

/// <summary>Model object that stores dev container build information used by services, view models, or dialogs.</summary>
public sealed class DevContainerBuild
{
    /// <summary>Gets or sets the context.</summary>
    public string Context { get; set; } = string.Empty;
    /// <summary>Gets or sets the dockerfile.</summary>
    public string? Dockerfile { get; set; }
    /// <summary>Gets or sets the args.</summary>
    public List<string> Args { get; set; } = new();
    /// <summary>Gets or sets the target.</summary>
    public string? Target { get; set; }
}

/// <summary>Model object that stores dev container compose information used by services, view models, or dialogs.</summary>
public sealed class DevContainerCompose
{
    /// <summary>Gets or sets the docker compose files.</summary>
    public List<string> DockerComposeFiles { get; set; } = new();
    /// <summary>Gets or sets the service.</summary>
    public string Service { get; set; } = string.Empty;
    /// <summary>Gets or sets the run services.</summary>
    public List<string> RunServices { get; set; } = new();
    /// <summary>Gets or sets the project.</summary>
    public ComposeProject Project { get; set; } = new();
}

/// <summary>Model object that stores dev container feature information used by services, view models, or dialogs.</summary>
public sealed class DevContainerFeature
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Gets or sets the options.</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gets or sets the raw options.</summary>
    public JsonElement RawOptions { get; set; }
}

/// <summary>Model object that stores dev container port attributes information used by services, view models, or dialogs.</summary>
public sealed class DevContainerPortAttributes
{
    /// <summary>Gets or sets the label.</summary>
    public string? Label { get; set; }
    /// <summary>Gets or sets the protocol.</summary>
    public string? Protocol { get; set; }
    /// <summary>Gets or sets the on auto forward.</summary>
    public string? OnAutoForward { get; set; }
}

/// <summary>Model object that stores dev container lifecycle information used by services, view models, or dialogs.</summary>
public sealed class DevContainerLifecycle
{
    /// <summary>Gets or sets the initialize.</summary>
    public List<string> Initialize { get; set; } = new();
    /// <summary>Gets or sets the on create.</summary>
    public List<string> OnCreate { get; set; } = new();
    /// <summary>Gets or sets the update content.</summary>
    public List<string> UpdateContent { get; set; } = new();
    /// <summary>Gets or sets the post create.</summary>
    public List<string> PostCreate { get; set; } = new();
    /// <summary>Gets or sets the post start.</summary>
    public List<string> PostStart { get; set; } = new();
    /// <summary>Gets or sets the post attach.</summary>
    public List<string> PostAttach { get; set; } = new();

    /// <summary>Enumerates the container-creation lifecycle commands in the order the devcontainer spec runs them.</summary>
    /// <returns>The lifecycle step names paired with their configured commands.</returns>
    public IEnumerable<(string Step, IReadOnlyList<string> Commands)> ContainerCreateSteps()
    {
        yield return ("onCreateCommand", OnCreate);
        yield return ("updateContentCommand", UpdateContent);
        yield return ("postCreateCommand", PostCreate);
    }
}

/// <summary>Model object that stores dev container import result information used by services, view models, or dialogs.</summary>
public sealed class DevContainerImportResult
{
    /// <summary>Gets or sets the config.</summary>
    public DevContainerConfig? Config { get; init; }
    /// <summary>Gets or sets the options.</summary>
    public RunContainerOptions? Options { get; init; }
    /// <summary>Gets or sets the warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Gets a value indicating whether the success flag is set.</summary>
    public bool Success => Config is not null && Options is not null && string.IsNullOrWhiteSpace(ErrorMessage);
}
