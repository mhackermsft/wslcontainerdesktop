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

namespace WslContainerDesktop.Models;

/// <summary>
/// Compose <c>restart:</c> policy. The desktop app (not <c>wslc</c>) enforces these, so they only
/// apply while the app is running — see <see cref="Services.ComposeProjectSupervisor"/>.
/// </summary>
public enum RestartPolicyKind
{
    /// <summary><c>no</c> — never auto-restart (the default).</summary>
    No = 0,

    /// <summary><c>on-failure</c> — restart only when the container exits non-zero.</summary>
    OnFailure = 1,

    /// <summary><c>always</c> — always restart the container if it stops.</summary>
    Always = 2,

    /// <summary><c>unless-stopped</c> — like <c>always</c>, but not after a user-initiated stop.</summary>
    UnlessStopped = 3,
}

/// <summary>
/// The condition a <c>depends_on</c> edge must satisfy before a dependent service is started.
/// </summary>
public enum DependencyCondition
{
    /// <summary><c>service_started</c> — the dependency's container has been created/started.</summary>
    ServiceStarted = 0,

    /// <summary><c>service_healthy</c> — the dependency has passed its health check.</summary>
    ServiceHealthy = 1,

    /// <summary><c>service_completed_successfully</c> — the dependency's container ran and exited 0.</summary>
    ServiceCompletedSuccessfully = 2,
}

/// <summary>A single <c>depends_on</c> edge from one service to another, with its gating condition.</summary>
public sealed class ComposeDependency
{
    public string ServiceName { get; set; } = string.Empty;

    public DependencyCondition Condition { get; set; } = DependencyCondition.ServiceStarted;
}

/// <summary>
/// A service <c>build:</c> section. When present the supervisor builds an image from
/// <see cref="Context"/> (resolved against the compose file's folder) and tags it before running.
/// </summary>
public sealed class ComposeBuildConfig
{
    /// <summary>Build context directory (absolute after import resolves it against the compose folder).</summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>Path to the Dockerfile relative to the context, or null for the default <c>Dockerfile</c>.</summary>
    public string? Dockerfile { get; set; }

    /// <summary>Build-time variables as <c>KEY=VALUE</c> strings (maps to repeated <c>--build-arg</c>).</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Target build stage for a multi-stage Dockerfile (maps to <c>--target</c>), or null.</summary>
    public string? Target { get; set; }

    /// <summary>Image metadata labels applied at build time (maps to repeated <c>--label</c>).</summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Build without the layer cache (compose <c>build.no_cache</c>, maps to <c>--no-cache</c>).</summary>
    public bool NoCache { get; set; }

    /// <summary>Always attempt to pull a newer base image (compose <c>build.pull</c> / <c>pull_policy: build</c>, maps to <c>--pull</c>).</summary>
    public bool Pull { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Context);
}

/// <summary>
/// A service reference to a project <c>secret</c> or <c>config</c>. Because <c>wslc</c> has no secret
/// store, the supervisor bind-mounts the source file read-only at <see cref="Target"/>.
/// </summary>
public sealed class ComposeFileMount
{
    /// <summary>The project-level secret/config name this reference points at.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Absolute mount path inside the container (defaults applied by the importer).</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>A top-level named <c>network:</c> the project declares (and the app creates on up).</summary>
public sealed class ComposeNetwork
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Network driver (compose <c>driver:</c>, maps to <c>network create --driver</c>).</summary>
    public string? Driver { get; set; }

    /// <summary>Driver-specific options as <c>KEY=VALUE</c> (maps to repeated <c>--opt</c>).</summary>
    public List<string> DriverOpts { get; set; } = new();

    /// <summary>Network metadata labels (maps to repeated <c>--label</c>).</summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When true the network is expected to already exist and is not created.</summary>
    public bool External { get; set; }
}

/// <summary>A top-level named <c>volume:</c> the project declares (and the app creates on up).</summary>
public sealed class ComposeVolume
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Volume driver (compose <c>driver:</c>, maps to <c>volume create --driver</c>).</summary>
    public string? Driver { get; set; }

    /// <summary>Driver-specific options as <c>KEY=VALUE</c> (maps to repeated <c>--opt</c>).</summary>
    public List<string> DriverOpts { get; set; } = new();

    /// <summary>Volume metadata labels (maps to repeated <c>--label</c>).</summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When true the volume is expected to already exist and is not created.</summary>
    public bool External { get; set; }
}

/// <summary>
/// A top-level <c>secret</c> or <c>config</c> definition. Only file-backed sources are supported;
/// the supervisor bind-mounts <see cref="File"/> read-only into referencing services.
/// </summary>
public sealed class ComposeSecret
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute host path to the source file (resolved against the compose folder on import).</summary>
    public string? File { get; set; }

    /// <summary>When true the secret/config is external (unmanaged); the app cannot materialize it.</summary>
    public bool External { get; set; }
}

/// <summary>
/// One service within a <see cref="ComposeProject"/>: the container run options plus the
/// orchestration metadata (dependencies, restart policy, health check) the supervisor enforces.
/// </summary>
public sealed class ComposeService
{
    /// <summary>The compose service key (e.g. <c>web</c>, <c>db</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The container run options derived from the service definition.</summary>
    public RunContainerOptions Options { get; set; } = new();

    /// <summary>Services this one depends on, with their gating conditions.</summary>
    public List<ComposeDependency> DependsOn { get; set; } = new();

    /// <summary>The service's restart policy, enforced in-app by the supervisor.</summary>
    public RestartPolicyKind Restart { get; set; } = RestartPolicyKind.No;

    /// <summary>
    /// Profiles this service belongs to (compose <c>profiles:</c>). A service with profiles only
    /// starts when one of its profiles is in <see cref="ComposeProject.ActiveProfiles"/>; a service
    /// with no profiles always starts (matching <c>docker compose</c>'s default).
    /// </summary>
    public List<string> Profiles { get; set; } = new();

    /// <summary>
    /// Grace period in seconds to wait for the container to stop before it is killed
    /// (compose <c>stop_grace_period</c>, applied via <c>wslc stop -t</c>). Null uses the engine default.
    /// </summary>
    public int? StopGracePeriodSeconds { get; set; }

    /// <summary>
    /// Build configuration (compose <c>build:</c>). When set and the image is missing, the supervisor
    /// builds the image before running the service.
    /// </summary>
    public ComposeBuildConfig? Build { get; set; }

    /// <summary>Secret references (compose service <c>secrets:</c>), bind-mounted read-only at up.</summary>
    public List<ComposeFileMount> Secrets { get; set; } = new();

    /// <summary>Config references (compose service <c>configs:</c>), bind-mounted read-only at up.</summary>
    public List<ComposeFileMount> Configs { get; set; } = new();

    /// <summary>
    /// Health probe derived from the compose <c>healthcheck:</c>, if any. The
    /// <see cref="HealthCheckConfig.ContainerName"/> is bound to the resolved container name at up.
    /// </summary>
    public HealthCheckConfig? Health { get; set; }

    /// <summary>
    /// Extra <c>/etc/hosts</c> entries (compose <c>extra_hosts:</c>), each as <c>host:ip</c>. Because
    /// <c>wslc run</c> has no <c>--add-host</c> flag, the supervisor appends these to the container's
    /// <c>/etc/hosts</c> via <c>exec</c> right after it starts.
    /// </summary>
    public List<string> ExtraHosts { get; set; } = new();
}

/// <summary>
/// A parsed, persistable compose project: a named set of services with a dependency graph.
/// The desktop app owns the project as a unit (up/down together) and supervises it while running.
/// </summary>
public sealed class ComposeProject
{
    /// <summary>Label key used to tag every container the app runs as part of a project.</summary>
    public const string ProjectLabel = "com.wsldesktop.project";

    /// <summary>Label key identifying which service within the project a container belongs to.</summary>
    public const string ServiceLabel = "com.wsldesktop.service";

    /// <summary>Project name (compose top-level <c>name:</c> or the imported file/folder name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The services that make up this project.</summary>
    public List<ComposeService> Services { get; set; } = new();

    /// <summary>
    /// Profiles enabled for this project. Empty means only profile-less services start (the
    /// <c>docker compose</c> default when no <c>--profile</c>/<c>COMPOSE_PROFILES</c> is given).
    /// </summary>
    public List<string> ActiveProfiles { get; set; } = new();

    /// <summary>Top-level named networks the project declares; created on up (unless external).</summary>
    public List<ComposeNetwork> Networks { get; set; } = new();

    /// <summary>Top-level named volumes the project declares; created on up (unless external).</summary>
    public List<ComposeVolume> Volumes { get; set; } = new();

    /// <summary>Top-level file-backed secrets, bind-mounted into referencing services.</summary>
    public List<ComposeSecret> Secrets { get; set; } = new();

    /// <summary>Top-level file-backed configs, bind-mounted into referencing services.</summary>
    public List<ComposeSecret> Configs { get; set; } = new();

    /// <summary>The deterministic container name the supervisor assigns to a service (<c>project_service</c>).</summary>
    public string ContainerNameFor(string serviceName) => $"{Name}_{serviceName}";

    /// <summary>
    /// Human-readable warnings collected during import about compose keys that are not supported and
    /// were ignored (e.g. <c>privileged</c>, <c>cap_add</c>, multi-network attach). Surfaced to the
    /// user at import time; not persisted with the project.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> Warnings { get; set; } = new();
}
