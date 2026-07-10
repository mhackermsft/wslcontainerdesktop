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

using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Installs, upgrades, uninstalls, and starts/stops the k3s service inside the host WSL distro,
/// and resolves version tags from the k3s release channels. The installer script is downloaded to
/// a file and integrity-verified before it runs (see <see cref="RunInstallerAsync"/>).
/// </summary>
public sealed class K8sInstaller
{
    private readonly WslRootShell _shell;

    public K8sInstaller(WslRootShell shell)
    {
        _shell = shell;
    }

    public Task<K3sInstallResult> InstallAsync(string? expectedInstallerHash, Action<string> onOutput, CancellationToken ct = default) =>
        RunInstallerAsync(version: null, expectedInstallerHash, onOutput, ct);

    public Task<K3sInstallResult> UpgradeAsync(string? version, string? expectedInstallerHash, Action<string> onOutput, CancellationToken ct = default) =>
        // Re-running the install script performs an in-place upgrade: it swaps the k3s
        // binary and restarts the service, preserving cluster data and workloads. Omitting
        // INSTALL_K3S_VERSION tracks the latest stable channel; setting it pins a version.
        RunInstallerAsync(version, expectedInstallerHash, onOutput, ct);

    /// <summary>
    /// Downloads the get.k3s.io installer to a temp file, prints and verifies its SHA-256, and
    /// only executes it when it matches <paramref name="expectedHash"/> (or when no pin was
    /// supplied). Downloading to a file first avoids the "curl | sh" hazard where a truncated
    /// download can execute a partial script, and lets us pin the installer logic while leaving
    /// the k3s version a free parameter so install and upgrade behave exactly as before.
    /// </summary>
    private async Task<K3sInstallResult> RunInstallerAsync(
        string? version, string? expectedHash, Action<string> onOutput, CancellationToken ct)
    {
        string? actualHash = null;
        var mismatch = false;

        // Intercept internal markers so they never reach the user-visible operation log.
        void Filter(string line)
        {
            if (line.StartsWith("@@INSTALLER_SHA=", StringComparison.Ordinal))
            {
                actualHash = line["@@INSTALLER_SHA=".Length..].Trim().ToLowerInvariant();
                return;
            }

            if (line.StartsWith("@@INSTALLER_HASH_MISMATCH", StringComparison.Ordinal))
            {
                mismatch = true;
                return;
            }

            if (line.StartsWith("@@DOWNLOAD_FAILED", StringComparison.Ordinal))
            {
                onOutput("Failed to download the k3s installer from https://get.k3s.io. Check your network and try again.");
                return;
            }

            onOutput(line);
        }

        var expected = string.IsNullOrWhiteSpace(expectedHash) ? string.Empty : expectedHash.Trim().ToLowerInvariant();
        var runLine = string.IsNullOrWhiteSpace(version)
            ? "INSTALL_K3S_SKIP_SELINUX_RPM=true sh \"$TMP\""
            : $"INSTALL_K3S_VERSION={WslRootShell.ShellEscape(version)} INSTALL_K3S_SKIP_SELINUX_RPM=true sh \"$TMP\"";

        // Single root shell: download -> hash -> verify -> run the SAME file -> clean up.
        var script =
            "TMP=$(mktemp) || { echo '@@DOWNLOAD_FAILED'; exit 4; }; " +
            "if ! curl -sfL https://get.k3s.io -o \"$TMP\"; then echo '@@DOWNLOAD_FAILED'; rm -f \"$TMP\"; exit 4; fi; " +
            "ACTUAL=$(sha256sum \"$TMP\" | awk '{print $1}'); " +
            "echo \"@@INSTALLER_SHA=$ACTUAL\"; " +
            $"EXPECTED={WslRootShell.ShellEscape(expected)}; " +
            "if [ -n \"$EXPECTED\" ] && [ \"$ACTUAL\" != \"$EXPECTED\" ]; then echo '@@INSTALLER_HASH_MISMATCH'; rm -f \"$TMP\"; exit 3; fi; " +
            $"{runLine}; rc=$?; rm -f \"$TMP\"; exit $rc";

        var result = await _shell.RunStreamingAsync(script, Filter, ct).ConfigureAwait(false);
        return new K3sInstallResult
        {
            Result = result,
            InstallerHash = actualHash,
            HashMismatch = mismatch,
        };
    }

    public async Task<string?> GetInstalledVersionAsync(CancellationToken ct = default)
    {
        var r = await _shell.RunAsync("k3s --version 2>/dev/null | head -n1", ct).ConfigureAwait(false);
        if (!r.Success)
        {
            return null;
        }

        // Line looks like: "k3s version v1.36.2+k3s1 (01b6f04a)"
        var match = Regex.Match(r.StandardOutput, @"v\d+\.\d+\.\d+\+k3s\d+");
        return match.Success ? match.Value : null;
    }

    public Task<string?> GetLatestStableVersionAsync(CancellationToken ct = default) =>
        GetChannelVersionAsync("stable", ct);

    public async Task<string?> GetChannelVersionAsync(string channel, CancellationToken ct = default)
    {
        // The channel server 302-redirects to the GitHub release for the channel's current tag.
        var r = await _shell.RunAsync(
            $"curl -s -o /dev/null -w '%{{redirect_url}}' https://update.k3s.io/v1-release/channels/{WslRootShell.ShellEscape(channel)}", ct)
            .ConfigureAwait(false);
        if (!r.Success)
        {
            return null;
        }

        var match = Regex.Match(r.StandardOutput, @"v\d+\.\d+\.\d+\+k3s\d+");
        return match.Success ? match.Value : null;
    }

    public Task<CommandResult> UninstallAsync(Action<string> onOutput, CancellationToken ct = default) =>
        _shell.RunStreamingAsync(
            "if [ -f /usr/local/bin/k3s-uninstall.sh ]; then /usr/local/bin/k3s-uninstall.sh; else echo 'k3s already removed'; fi",
            onOutput, ct);

    public Task<CommandResult> StartAsync(CancellationToken ct = default) =>
        _shell.RunAsync("systemctl start k3s", ct);

    public Task<CommandResult> StopAsync(CancellationToken ct = default) =>
        _shell.RunAsync("systemctl stop k3s", ct);
}
