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
/// Describes what the app should do when the user clicks a toast (or one of its
/// buttons). Parsed from the notification's activation arguments.
/// </summary>
public sealed class NotificationActivation
{
    /// <summary>Nav tag of the page to open (e.g. "images", "containers", "dashboard").</summary>
    public string? Page { get; init; }

    /// <summary>Optional action verb carried by a toast button (e.g. "logs").</summary>
    public string? Action { get; init; }

    /// <summary>Optional target identifier (e.g. a container id) for the action.</summary>
    public string? TargetId { get; init; }
}

/// <summary>
/// Raises Windows toast notifications for noteworthy engine/app events and routes
/// toast clicks back into the app. All methods are safe no-ops when notifications are
/// disabled (globally muted or by category) or when the platform is unavailable.
/// </summary>
public interface INotificationService
{
    /// <summary>Registers the app with the notification platform and starts listening for clicks.</summary>
    void Register();

    /// <summary>Unregisters from the notification platform (call at shutdown).</summary>
    void Unregister();

    /// <summary>Raised (on the UI thread) when the user activates a toast.</summary>
    event EventHandler<NotificationActivation>? ActivationRequested;

    /// <summary>Toast for an image pull that finished or failed.</summary>
    void NotifyImagePull(string reference, bool success, string? error = null);

    /// <summary>Toast for an image build that finished or failed.</summary>
    void NotifyImageBuild(string tag, bool success, string? error = null);

    /// <summary>Toast for a container that exited/crashed unexpectedly.</summary>
    void NotifyContainerExited(string containerName, string containerId);

    /// <summary>Toast for the engine becoming unavailable.</summary>
    void NotifyEngineDown();

    /// <summary>Toast for the engine recovering after being unavailable.</summary>
    void NotifyEngineRecovered();
}
