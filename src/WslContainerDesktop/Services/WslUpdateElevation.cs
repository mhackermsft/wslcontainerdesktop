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

namespace WslContainerDesktop.Services;

/// <summary>
/// Guidance for the administrator (UAC) prompt raised by <c>wsl --update</c>. WSL downloads an MSI
/// and installs it with <c>MsiInstallProduct</c> from a non-elevated process, so Windows Installer
/// asks for elevation. The app runs wsl.exe without a window, so the requesting process is never
/// in the foreground and Windows often shows the prompt only as a flashing shield on the taskbar.
/// The update then appears to hang until the user finds and approves it.
/// </summary>
public static class WslUpdateElevation
{
    /// <summary>One-line note shown before an update starts.</summary>
    public const string BeforeUpdateNote =
        "Windows will ask for administrator permission to install the update.";

    /// <summary>InfoBar title while the update runs and no prompt has been seen yet.</summary>
    public const string ExpectPromptTitle = "Administrator permission needed";

    /// <summary>InfoBar title while the Windows permission prompt is open.</summary>
    public const string WaitingTitle = "Windows is waiting for your permission";

    /// <summary>How to find and approve the prompt, and how to tell it's the WSL update.</summary>
    public const string Guidance =
        "Installing a WSL update needs administrator permission. If no prompt appears on screen, " +
        "select the flashing shield icon on the taskbar. Check that the prompt shows Microsoft " +
        "Corporation as the verified publisher, then choose Yes. The update continues once you " +
        "approve it.";

    /// <summary>Progress text while the prompt is open.</summary>
    public const string WaitingProgress = "Waiting for you to approve the administrator prompt…";

    /// <summary>Progress text after the prompt was approved.</summary>
    public const string InstallingProgress = "Installing the WSL update…";

    /// <summary>Appended to a failure when a prompt was seen, since declining it fails the update.</summary>
    public const string DeclinedHint =
        "If you declined or closed the administrator prompt, select Update again and choose Yes.";

    /// <summary>How often the consent UI is probed while an update runs.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Polls for the Windows consent UI until <paramref name="operation"/> completes, reporting
    /// each open/closed transition through <paramref name="onPromptChanged"/> on the caller's
    /// context. Returns true when a prompt was seen at any point.
    /// </summary>
    /// <param name="operation">The running update.</param>
    /// <param name="onPromptChanged">Receives true when the prompt opens and false when it closes.</param>
    /// <param name="isPromptOpen">Probe override for tests; defaults to <see cref="IsConsentPromptOpen"/>.</param>
    /// <param name="pollInterval">Poll interval override for tests.</param>
    public static async Task<bool> WatchAsync(
        Task operation,
        Action<bool> onPromptChanged,
        Func<bool>? isPromptOpen = null,
        TimeSpan? pollInterval = null)
    {
        isPromptOpen ??= IsConsentPromptOpen;
        var interval = pollInterval ?? PollInterval;
        var seen = false;
        var open = false;

        while (!operation.IsCompleted)
        {
            var now = await Task.Run(isPromptOpen);
            if (now != open)
            {
                open = now;
                onPromptChanged(open);
            }

            seen |= now;
            await Task.WhenAny(operation, Task.Delay(interval));
        }

        if (open)
        {
            onPromptChanged(false);
        }

        return seen;
    }

    /// <summary>
    /// True while the Windows consent UI (<c>consent.exe</c>) is running. Another app's prompt
    /// also counts, which is why <see cref="Guidance"/> asks the user to check the publisher.
    /// </summary>
    public static bool IsConsentPromptOpen()
    {
        try
        {
            var processes = Process.GetProcessesByName("consent");
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length > 0;
        }
        catch (Exception)
        {
            // Probing is best effort; without it the static guidance is still shown.
            return false;
        }
    }
}
