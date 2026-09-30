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

/// <summary>The kind of resource a port-forward targets.</summary>
public enum PortForwardTargetKind
{
    /// <summary>Represents the pod option.</summary>
    Pod,
    /// <summary>Represents the service option.</summary>
    Service,
}

/// <summary>An active `kubectl port-forward` session managed by the app.</summary>
public sealed class PortForward
{
    /// <summary>Returns the forward description so list rows announce it to screen readers instead of the type.</summary>
    public override string ToString() => Display;

    /// <summary>Gets or sets the id.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Gets or sets the kind.</summary>
    public PortForwardTargetKind Kind { get; init; }
    /// <summary>Gets or sets the namespace.</summary>
    public string Namespace { get; init; } = "default";
    /// <summary>Gets or sets the target name.</summary>
    public string TargetName { get; init; } = string.Empty;
    /// <summary>Gets or sets the local port.</summary>
    public int LocalPort { get; init; }
    /// <summary>Gets or sets the remote port.</summary>
    public int RemotePort { get; init; }

    /// <summary>kubectl target argument, e.g. "service/demo-nginx" or "pod/my-pod".</summary>
    public string TargetRef =>
        (Kind == PortForwardTargetKind.Service ? "service/" : "pod/") + TargetName;

    /// <summary>Gets the local url.</summary>
    public string LocalUrl => $"http://localhost:{LocalPort}";

    /// <summary>Gets the display.</summary>
    public string Display => $"localhost:{LocalPort} -> {TargetRef}:{RemotePort} ({Namespace})";
}
