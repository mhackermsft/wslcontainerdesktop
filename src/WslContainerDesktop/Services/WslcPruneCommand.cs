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

namespace WslContainerDesktop.Services;

/// <summary>
/// Lists the resource groups that can be passed to the <c>wslc</c> prune command.
/// </summary>
public enum WslcPruneTarget
{
    /// <summary>
    /// Selects the containers resource group for <c>wslc</c> pruning.
    /// </summary>
    Containers,
    /// <summary>
    /// Selects the images resource group for <c>wslc</c> pruning.
    /// </summary>
    Images,
    /// <summary>
    /// Selects the volumes resource group for <c>wslc</c> pruning.
    /// </summary>
    Volumes,
    /// <summary>
    /// Selects the networks resource group for <c>wslc</c> pruning.
    /// </summary>
    Networks,
}

/// <summary>Arguments to run, or an error explaining why pruning must not be attempted.</summary>
public sealed record WslcPruneSelection(IReadOnlyList<string>? Arguments, string? Error);

/// <summary>Chooses the non-interactive <c>prune --force</c> command line after the app has confirmed with the user.</summary>
internal static class WslcPruneCommand
{
    /// <summary>
    /// Builds validated prune arguments for the selected resource targets.
    /// </summary>
    internal static WslcPruneSelection Select(WslcPruneTarget target)
    {
        var arguments = target switch
        {
            WslcPruneTarget.Containers => new List<string> { "container", "prune" },
            WslcPruneTarget.Images => new List<string> { "image", "prune" },
            WslcPruneTarget.Volumes => new List<string> { "volume", "prune", "--all" },
            WslcPruneTarget.Networks => new List<string> { "network", "prune" },
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        arguments.Add("--force");
        return new(arguments, null);
    }
}
