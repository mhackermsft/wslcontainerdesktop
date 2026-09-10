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

public enum ComposeServiceChange { Unchanged, Changed, Missing, Incompatible }
public enum ComposeServiceAction { Keep, Start, Create, Recreate, Restart, Stop, Remove, Blocked }
public enum ComposeLifecycleOperation { Up, Restart, Stop, Down }
public enum ComposeImagePolicy { Missing, Always, Never, Build }
public enum ComposeImageAction { None, Build, Pull }

public sealed record ComposeOperationRequest
{
    public ComposeLifecycleOperation Operation { get; init; } = ComposeLifecycleOperation.Up;
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    public bool Build { get; init; }
    public bool ForceRecreate { get; init; }
    public IReadOnlyDictionary<string, int> Replicas { get; init; } = new Dictionary<string, int>();
}

public sealed record ComposeServicePlan(
    ComposeService Service,
    string ContainerName,
    string Fingerprint,
    ComposeServiceChange Change,
    ComposeServiceAction Action,
    string Reason,
    string? ContainerId)
{
    public int InstanceIndex { get; init; } = 1;
    public string InstanceKey => InstanceIndex == 1 ? Service.Name : $"{Service.Name}#{InstanceIndex}";
    public int DesiredReplicas { get; init; } = 1;
    public string? StorageWarning => DesiredReplicas > 1 && Service.Options.Volumes.Any(v => v.Contains(":/", StringComparison.Ordinal))
        ? "Named volumes and bind sources are shared by all replicas; ensure the application supports concurrent access. Anonymous volumes remain instance-local."
        : null;
    public string? ImageId { get; init; }
    public ComposeImageAction ImageAction { get; init; } = ComposeImageAction.None;
}

public sealed record ComposeReconciliationPlan(IReadOnlyList<ComposeServicePlan> Services)
{
    public ComposeReconciliationPlan() : this(Array.Empty<ComposeServicePlan>()) { }

    public bool CanApply => Services.All(service => service.Action != ComposeServiceAction.Blocked);
}

/// <summary>Last successfully applied configuration, independent of subsequent desired edits.</summary>
public sealed class ComposeAppliedService
{
    public int InstanceIndex { get; set; } = 1;
    [JsonIgnore]
    public string InstanceKey => InstanceIndex == 1 ? Service.Name : $"{Service.Name}#{InstanceIndex}";
    public string ContainerId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string? ImageId { get; set; }
    public bool ManuallyStopped { get; set; }
    public ComposeService Service { get; set; } = new();
    public List<ComposeNetwork> Networks { get; set; } = new();
    public List<ComposeVolume> Volumes { get; set; } = new();
}
