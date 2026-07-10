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
/// The single source of truth for the marker protocol shared between the k3s status probe shell
/// scripts (producer) and their C# parsing (consumer). Keeping the tokens and the script/section
/// helpers together stops the two sides from silently drifting apart.
/// </summary>
internal static class K8sStatusProtocol
{
    public const string StateNotInstalled = "@@STATE=notinstalled";
    public const string StateStopped = "@@STATE=stopped";
    public const string StateRunning = "@@STATE=running";

    public const string NodesMarker = "@@NODES";
    public const string PodsMarker = "@@PODS";

    /// <summary>
    /// Builds a single-invocation probe: emits an install/service state marker, and when running,
    /// emits <paramref name="dataMarker"/> followed by the JSON output of <paramref name="kubectlListCommand"/>.
    /// Done in one shell so we only pay wsl.exe cold-start/distro-attach once.
    /// </summary>
    public static string BuildProbeScript(string dataMarker, string kubectlListCommand) =>
        $"if [ ! -f /usr/local/bin/k3s-uninstall.sh ]; then echo '{StateNotInstalled}'; exit 0; fi; " +
        "a=$(systemctl is-active k3s 2>/dev/null || true); " +
        $"if [ \"$a\" != active ]; then echo '{StateStopped}'; exit 0; fi; " +
        $"echo '{StateRunning}'; echo '{dataMarker}'; {kubectlListCommand} 2>/dev/null";

    /// <summary>Returns the text following <paramref name="marker"/>, or empty if the marker is absent.</summary>
    public static string SectionAfter(string output, string marker)
    {
        var idx = output.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? output[(idx + marker.Length)..] : string.Empty;
    }

    public static bool Contains(string output, string marker) =>
        output.Contains(marker, StringComparison.Ordinal);
}
