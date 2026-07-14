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
}

/// <summary>A single <c>depends_on</c> edge from one service to another, with its gating condition.</summary>
public sealed class ComposeDependency
{
    public string ServiceName { get; set; } = string.Empty;

    public DependencyCondition Condition { get; set; } = DependencyCondition.ServiceStarted;
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
    /// Health probe derived from the compose <c>healthcheck:</c>, if any. The
    /// <see cref="HealthCheckConfig.ContainerName"/> is bound to the resolved container name at up.
    /// </summary>
    public HealthCheckConfig? Health { get; set; }
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

    /// <summary>The deterministic container name the supervisor assigns to a service (<c>project_service</c>).</summary>
    public string ContainerNameFor(string serviceName) => $"{Name}_{serviceName}";
}
