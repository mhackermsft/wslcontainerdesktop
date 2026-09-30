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

using System.Text.Json.Serialization;

namespace WslContainerDesktop.Models;

/// <summary>Values that describe compose service change states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeServiceChange { Unchanged, Changed, Missing, Incompatible }
/// <summary>Values that describe compose service action states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeServiceAction { Keep, Start, Create, Recreate, Restart, Stop, Remove, Blocked }
/// <summary>Values that describe compose lifecycle operation states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeLifecycleOperation { Up, Restart, Stop, Down }
/// <summary>Values that describe compose image policy states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeImagePolicy { Missing, Always, Never, Build }
/// <summary>Values that describe compose image action states or choices in WSL Container Desktop workflows.</summary>
public enum ComposeImageAction { None, Build, Pull }

/// <summary>Immutable or init-only data model that carries compose operation request information between services and view models.</summary>
public sealed record ComposeOperationRequest
{
    /// <summary>Gets or sets the operation.</summary>
    public ComposeLifecycleOperation Operation { get; init; } = ComposeLifecycleOperation.Up;
    /// <summary>Gets or sets the services.</summary>
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    /// <summary>Gets or sets a value indicating whether the build flag is set.</summary>
    public bool Build { get; init; }
    /// <summary>Gets or sets a value indicating whether the force recreate flag is set.</summary>
    public bool ForceRecreate { get; init; }
    /// <summary>Gets or sets the replicas.</summary>
    public IReadOnlyDictionary<string, int> Replicas { get; init; } = new Dictionary<string, int>();

    /// <summary>Delete anonymous volumes along with each removed container, like
    /// <c>docker compose down --volumes</c>. Named and external volumes are unaffected.</summary>
    public bool RemoveAnonymousVolumes { get; init; }
}

/// <summary>Immutable or init-only data model that carries compose service plan information between services and view models.</summary>
public sealed record ComposeServicePlan(
    ComposeService Service,
    string ContainerName,
    string Fingerprint,
    ComposeServiceChange Change,
    ComposeServiceAction Action,
    string Reason,
    string? ContainerId)
{
    /// <summary>Gets or sets the instance index.</summary>
    public int InstanceIndex { get; init; } = 1;
    /// <summary>Gets the instance key.</summary>
    public string InstanceKey => InstanceIndex == 1 ? Service.Name : $"{Service.Name}#{InstanceIndex}";
    /// <summary>Gets or sets the desired replicas.</summary>
    public int DesiredReplicas { get; init; } = 1;
    /// <summary>Gets the storage warning.</summary>
    public string? StorageWarning => DesiredReplicas > 1 && Service.Options.Volumes.Any(v => v.Contains(":/", StringComparison.Ordinal))
        ? "Named volumes and bind sources are shared by all replicas; ensure the application supports concurrent access. Anonymous volumes remain instance-local."
        : null;
    /// <summary>Gets or sets the image id.</summary>
    public string? ImageId { get; init; }
    /// <summary>Gets or sets the image action.</summary>
    public ComposeImageAction ImageAction { get; init; } = ComposeImageAction.None;
    /// <summary>Gets or sets the backend.</summary>
    public ComposeExecutionBackend Backend { get; init; } = ComposeExecutionBackend.Unknown;
    /// <summary>Gets or sets the network support.</summary>
    public WslcCapabilitySupport? NetworkSupport { get; init; }
    /// <summary>Gets or sets the health owner.</summary>
    public ComposePolicyOwner HealthOwner { get; init; } = ComposePolicyOwner.Unknown;
    /// <summary>Gets or sets the compatibility warning.</summary>
    public string? CompatibilityWarning { get; init; }
}

/// <summary>Immutable or init-only data model that carries compose reconciliation plan information between services and view models.</summary>
public sealed record ComposeReconciliationPlan(IReadOnlyList<ComposeServicePlan> Services)
{
    /// <summary>Creates a new &lt;c&gt;ComposeReconciliationPlan&lt;/c&gt; and wires the state used by the dialog or model.</summary>
    public ComposeReconciliationPlan() : this(Array.Empty<ComposeServicePlan>()) { }

    /// <summary>Gets a value indicating whether this value can apply.</summary>
    public bool CanApply => Services.All(service => service.Action != ComposeServiceAction.Blocked);
}

/// <summary>Last successfully applied configuration, independent of subsequent desired edits.</summary>
public sealed class ComposeAppliedService
{
    /// <summary>Gets or sets the instance index.</summary>
    public int InstanceIndex { get; set; } = 1;
    /// <summary>Gets the instance key.</summary>
    [JsonIgnore]
    public string InstanceKey => InstanceIndex == 1 ? Service.Name : $"{Service.Name}#{InstanceIndex}";
    /// <summary>Gets or sets the container id.</summary>
    public string ContainerId { get; set; } = string.Empty;
    /// <summary>Gets or sets the fingerprint.</summary>
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>Gets or sets the image id.</summary>
    public string? ImageId { get; set; }
    /// <summary>Gets or sets a value indicating whether the manually stopped flag is set.</summary>
    public bool ManuallyStopped { get; set; }
    /// <summary>Gets or sets the service.</summary>
    public ComposeService Service { get; set; } = new();
    /// <summary>Gets or sets the networks.</summary>
    public List<ComposeNetwork> Networks { get; set; } = new();
    /// <summary>Gets or sets the volumes.</summary>
    public List<ComposeVolume> Volumes { get; set; } = new();
}
