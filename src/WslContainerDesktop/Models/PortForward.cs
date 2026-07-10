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
    Pod,
    Service,
}

/// <summary>An active `kubectl port-forward` session managed by the app.</summary>
public sealed class PortForward
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public PortForwardTargetKind Kind { get; init; }
    public string Namespace { get; init; } = "default";
    public string TargetName { get; init; } = string.Empty;
    public int LocalPort { get; init; }
    public int RemotePort { get; init; }

    /// <summary>kubectl target argument, e.g. "service/demo-nginx" or "pod/my-pod".</summary>
    public string TargetRef =>
        (Kind == PortForwardTargetKind.Service ? "service/" : "pod/") + TargetName;

    public string LocalUrl => $"http://localhost:{LocalPort}";

    public string Display => $"localhost:{LocalPort} -> {TargetRef}:{RemotePort} ({Namespace})";
}
