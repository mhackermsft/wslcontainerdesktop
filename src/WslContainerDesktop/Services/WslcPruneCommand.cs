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

using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

public enum WslcPruneTarget
{
    Containers,
    Images,
    Volumes,
    Networks,
}

/// <summary>Arguments to run, or an error explaining why pruning must not be attempted.</summary>
public sealed record WslcPruneSelection(IReadOnlyList<string>? Arguments, string? Error);

/// <summary>
/// Chooses the <c>prune</c> command line for the engine in a capability snapshot. Engines that
/// advertise <c>--force</c> ask for confirmation without it, and the app has already confirmed with
/// the user, so the flag is passed. Engines without the flag (the 2.9.9 baseline) never prompt and
/// reject it, so they run the plain command. When support is unknown nothing runs: guessing wrong
/// either fails on an unknown argument or silently prunes nothing.
/// </summary>
internal static class WslcPruneCommand
{
    internal static WslcPruneSelection Select(WslcPruneTarget target, WslcCapabilities capabilities)
    {
        var (feature, arguments, noun) = target switch
        {
            WslcPruneTarget.Containers => (WslcFeature.ContainerPruneForce, new List<string> { "container", "prune" }, "stopped containers"),
            WslcPruneTarget.Images => (WslcFeature.ImagePruneForce, new List<string> { "image", "prune" }, "dangling images"),
            WslcPruneTarget.Volumes => (WslcFeature.VolumePruneForce, new List<string> { "volume", "prune", "--all" }, "unused volumes"),
            WslcPruneTarget.Networks => (WslcFeature.NetworkPruneForce, new List<string> { "network", "prune" }, "unused networks"),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        var capability = capabilities[feature];
        switch (capability.Support)
        {
            case WslcCapabilitySupport.Supported:
                arguments.Add("--force");
                return new(arguments, null);
            case WslcCapabilitySupport.Unsupported:
                return new(arguments, null);
            default:
                return new(null,
                    $"Nothing was pruned: could not determine whether this WSLC engine needs 'prune --force' to remove {noun} " +
                    $"without an interactive prompt. {capability.Diagnostic}".TrimEnd());
        }
    }
}
