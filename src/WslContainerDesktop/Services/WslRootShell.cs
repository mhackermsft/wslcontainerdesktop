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

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Shared plumbing for running privileged commands inside the k3s host WSL distro via
/// <c>wsl.exe -u root -e sh -c "…"</c>. Centralizes the <see cref="ProcessStartInfo"/> setup,
/// UTF-8 handling, and shell-argument escaping used by the Kubernetes collaborators
/// (<see cref="K8sInstaller"/>, <see cref="K8sResourceClient"/>, <see cref="PortForwardManager"/>).
/// </summary>
public sealed class WslRootShell
{
    private readonly ISettingsService _settings;

    public WslRootShell(ISettingsService settings)
    {
        _settings = settings;
    }

    private string? Distro => string.IsNullOrWhiteSpace(_settings.WslDistro) ? null : _settings.WslDistro;

    /// <summary>The configured distro name, or "default" when none is pinned (for display only).</summary>
    public string DistroLabel => Distro ?? "default";

    /// <summary>Builds a start info that runs <paramref name="bashCommand"/> as root in the host distro.</summary>
    public ProcessStartInfo BaseStartInfo(string bashCommand)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Force wsl.exe to emit UTF-8 rather than UTF-16LE.
        psi.Environment["WSL_UTF8"] = "1";

        if (Distro is not null)
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(Distro);
        }

        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("root");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(bashCommand);

        return psi;
    }

    public Task<CommandResult> RunAsync(string bashCommand, CancellationToken ct) =>
        ProcessExecutor.RunAsync(
            BaseStartInfo(bashCommand),
            launchErrorContext: "Could not launch wsl.exe.",
            ct: ct);

    public Task<CommandResult> RunWithStdinAsync(string bashCommand, string stdin, CancellationToken ct) =>
        ProcessExecutor.RunAsync(
            BaseStartInfo(bashCommand),
            stdin: stdin,
            launchErrorContext: "Could not launch wsl.exe.",
            ct: ct);

    public Task<CommandResult> RunStreamingAsync(string bashCommand, Action<string> onOutput, CancellationToken ct) =>
        ProcessExecutor.RunAsync(
            BaseStartInfo(bashCommand),
            onLine: onOutput,
            launchErrorContext: "Could not launch wsl.exe.",
            ct: ct);

    // ---- Shell-argument escaping ---------------------------------------

    /// <summary>Single-quotes a value for safe interpolation into an <c>sh -c</c> command line.</summary>
    public static string ShellEscape(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Validates a resource kind (a closed, app-supplied set such as "pod"/"deployment") against a
    /// conservative allow-list and returns it shell-escaped. Escaping alone already neutralizes
    /// shell metacharacters; the allow-list is defense in depth so an unexpected value can never
    /// be interpolated into the root shell. A malformed kind is replaced with an empty argument
    /// that kubectl rejects harmlessly rather than being passed through.
    /// </summary>
    public static string SafeKind(string kind) =>
        !string.IsNullOrWhiteSpace(kind) && Regex.IsMatch(kind, @"^[A-Za-z][A-Za-z0-9.\-]*$")
            ? ShellEscape(kind)
            : "''";

    /// <summary>Returns the `-n ns` or `--all-namespaces` selector for a list query.</summary>
    public static string NsSelector(string? ns) =>
        string.IsNullOrWhiteSpace(ns) || ns == "All namespaces"
            ? "--all-namespaces"
            : $"-n {ShellEscape(ns)}";

    /// <summary>Returns the ` -n ns` argument for a namespaced object, or empty for cluster-scoped.</summary>
    public static string NsArg(string ns) =>
        string.IsNullOrWhiteSpace(ns) ? string.Empty : $" -n {ShellEscape(ns)}";
}
