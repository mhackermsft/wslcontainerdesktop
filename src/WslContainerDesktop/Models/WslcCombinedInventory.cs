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

/// <summary>Session-qualified resources and completeness for a combined inventory.</summary>
public sealed class WslcCombinedInventory
{
    /// <summary>A resource together with the session generation that owned the observation.</summary>
    public sealed record Resource<T>(WslcSessionInfo Session, T Value);

    /// <summary>A published port together with its container and owning session.</summary>
    public sealed record Endpoint(WslcSessionInfo Session, ContainerInfo Container, PortMapping Port);

    /// <summary>The source of an incomplete session observation.</summary>
    public enum FailureKind { Session, Containers, Images, Volumes, Networks }

    /// <summary>An unavailable part of an inventory; raw engine diagnostics are retained separately.</summary>
    public sealed record Failure(WslcSessionInfo Session, FailureKind Kind, string? Detail, bool TimedOut,
        bool UnexpectedCancellation = false);

    /// <summary>Container rows, including identical IDs from different sessions.</summary>
    public IReadOnlyList<Resource<ContainerInfo>> Containers { get; private init; } = [];
    /// <summary>Image rows, including identical image IDs from different sessions.</summary>
    public IReadOnlyList<Resource<ImageInfo>> Images { get; private init; } = [];
    /// <summary>Volume rows qualified by owning session.</summary>
    public IReadOnlyList<Resource<VolumeInfo>> Volumes { get; private init; } = [];
    /// <summary>Network rows qualified by owning session.</summary>
    public IReadOnlyList<Resource<NetworkInfo>> Networks { get; private init; } = [];
    /// <summary>Published ports of running containers with known port metadata.</summary>
    public IReadOnlyList<Endpoint> Endpoints { get; private init; } = [];
    /// <summary>Per-session failures that make the displayed inventory incomplete.</summary>
    public IReadOnlyList<Failure> Failures { get; private init; } = [];
    /// <summary>Whether all requested session inventories were read successfully.</summary>
    public bool IsComplete { get; private init; }
    /// <summary>Whether all running containers have known published-port metadata.</summary>
    public bool EndpointsComplete { get; private init; }

    /// <summary>Projects resource rows without merging identities or interpreting failures as empty inventories.</summary>
    public static WslcCombinedInventory Create(IReadOnlyList<WslcSessionInventory> sessions,
        WslcCombinedInventory? previous = null)
    {
        var containers = new List<Resource<ContainerInfo>>();
        var images = new List<Resource<ImageInfo>>();
        var volumes = new List<Resource<VolumeInfo>>();
        var networks = new List<Resource<NetworkInfo>>();
        var endpoints = new List<Endpoint>();
        var failures = new List<Failure>();
        var containersComplete = true;
        var portsComplete = true;

        foreach (var inventory in sessions)
        {
            if (inventory.SessionChanged)
            {
                failures.Add(new Failure(inventory.Session, FailureKind.Session, null, false));
                containersComplete = false;
                continue;
            }

            AddPart(inventory.Session, inventory.Containers, FailureKind.Containers, containers, failures);
            AddPart(inventory.Session, inventory.Images, FailureKind.Images, images, failures, previous?.Images);
            AddPart(inventory.Session, inventory.Volumes, FailureKind.Volumes, volumes, failures, previous?.Volumes);
            AddPart(inventory.Session, inventory.Networks, FailureKind.Networks, networks, failures, previous?.Networks);
            if (!inventory.Containers.IsComplete)
            {
                containersComplete = false;
                continue;
            }

            foreach (var container in inventory.Containers.Items!.Where(c => c.State == ContainerState.Running))
            {
                if (!container.PortsKnown)
                {
                    portsComplete = false;
                    continue;
                }
                endpoints.AddRange(container.Ports.Where(port => port.HostPort > 0)
                    .Select(port => new Endpoint(inventory.Session, container, port)));
            }
        }

        return new WslcCombinedInventory
        {
            Containers = containers,
            Images = ReuseUnchanged(images, previous?.Images),
            Volumes = ReuseUnchanged(volumes, previous?.Volumes),
            Networks = ReuseUnchanged(networks, previous?.Networks),
            Endpoints = endpoints,
            Failures = failures,
            IsComplete = failures.Count == 0,
            EndpointsComplete = containersComplete && portsComplete,
        };
    }

    private static void AddPart<T>(WslcSessionInfo session, WslcInventoryPart<T> part, FailureKind kind,
        List<Resource<T>> resources, List<Failure> failures, IReadOnlyList<Resource<T>>? previous = null)
    {
        if (!part.IsComplete)
        {
            failures.Add(new Failure(session, kind, part.Error, part.TimedOut, part.UnexpectedCancellation));
            return;
        }
        foreach (var item in part.Items!)
        {
            var old = previous is not null && resources.Count < previous.Count ? previous[resources.Count] : null;
            resources.Add(old is not null && old.Session == session && ReferenceEquals(old.Value, item)
                ? old : new Resource<T>(session, item));
        }
    }

    private static IReadOnlyList<Resource<T>> ReuseUnchanged<T>(List<Resource<T>> current,
        IReadOnlyList<Resource<T>>? previous)
    {
        if (previous is null || current.Count != previous.Count) return current;
        for (var index = 0; index < current.Count; index++)
            if (!ReferenceEquals(current[index], previous[index])) return current;
        return previous;
    }
}
