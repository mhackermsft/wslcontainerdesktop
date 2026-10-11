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

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WslContainerDesktop.Services;

/// <summary>
/// Wraps the Windows App SDK <see cref="AppNotificationManager"/> to push toasts and
/// surface toast clicks as <see cref="ActivationRequested"/> events. The app has package
/// identity, so no separate COM activator registration is required.
/// </summary>
public sealed class NotificationService : INotificationService
{
    // Argument keys carried by every toast so a click can route back into the app.
    private const string PageKey = "page";
    private const string ActionKey = "action";
    private const string IdKey = "id";

    /// <summary>Toast action verb that starts installing the available update.</summary>
    public const string UpdateAction = "update";

    /// <summary>Toast action verb that opens the available release's page.</summary>
    public const string ReleaseNotesAction = "release-notes";

    private readonly ISettingsService _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<NotificationService> _logger;

    private bool _registered;

    /// <summary>Raised on the UI dispatcher when a toast body or action button is clicked.</summary>
    public event EventHandler<NotificationActivation>? ActivationRequested;

    /// <summary>Creates a notification wrapper tied to the UI dispatcher used for navigation callbacks.</summary>
    public NotificationService(ISettingsService settings, DispatcherQueue dispatcher, ILogger<NotificationService> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Registers the packaged app with Windows App SDK notifications.</summary>
    public void Register()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Notifications are a convenience; never let registration failures crash startup.
            _logger.LogWarning(ex, "Failed to register for app notifications.");
        }
    }

    /// <summary>Unregisters notification callbacks during shutdown.</summary>
    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to unregister app notifications.");
        }
        finally
        {
            _registered = false;
        }
    }

    /// <summary>Shows an image-pull completion or failure toast when that category is enabled.</summary>
    public void NotifyImagePull(string reference, bool success, string? error = null)
    {
        if (!IsCategoryEnabled(_settings.NotifyImageEvents))
        {
            return;
        }

        if (success)
        {
            Show(UiText.Get("Common_Text0249", "Image pulled"), UiText.Get("Common_Text0250", "Finished pulling {0}.", reference), "images");
        }
        else
        {
            Show(UiText.Get("Common_Text0103", "Pull failed"), UiText.Get("Common_Text0251", "Could not pull {0}.{1}", reference, FormatError(error)), "images");
        }
    }

    /// <summary>Shows an image-build completion or failure toast when that category is enabled.</summary>
    public void NotifyImageBuild(string tag, bool success, string? error = null)
    {
        if (!IsCategoryEnabled(_settings.NotifyImageEvents))
        {
            return;
        }

        if (success)
        {
            Show(UiText.Get("Common_Text0252", "Build complete"), UiText.Get("Common_Text0253", "Finished building {0}.", tag), "images");
        }
        else
        {
            Show(UiText.Get("Common_Text0254", "Build failed"), UiText.Get("Common_Text0255", "Could not build {0}.{1}", tag, FormatError(error)), "images");
        }
    }

    /// <summary>Shows a toast for an exited container with a button that routes to logs.</summary>
    public void NotifyContainerExited(string containerName, string containerId)
    {
        if (!IsCategoryEnabled(_settings.NotifyContainerEvents))
        {
            return;
        }

        Show(
            UiText.Get("Common_Text0256", "Container stopped"),
            UiText.Get("Common_Text0257", "\"{0}\" is no longer running.", containerName),
            "containers",
            new AppNotificationButton(UiText.Get("Common_Text0258", "View logs"))
                .AddArgument(ActionKey, "logs")
                .AddArgument(IdKey, containerId)
                .AddArgument(PageKey, "containers"));
    }

    /// <summary>Shows a toast when the WSL container engine becomes unreachable.</summary>
    public void NotifyEngineDown()
    {
        if (!IsCategoryEnabled(_settings.NotifyEngineEvents))
        {
            return;
        }

        Show(UiText.Get("Common_Text0259", "Engine unavailable"), UiText.Get("Common_Text0260", "The WSL container engine became unreachable."), "dashboard");
    }

    /// <summary>Shows a toast when engine connectivity recovers.</summary>
    public void NotifyEngineRecovered()
    {
        if (!IsCategoryEnabled(_settings.NotifyEngineEvents))
        {
            return;
        }

        Show(UiText.Get("Common_Text0261", "Engine recovered"), UiText.Get("Common_Text0262", "The WSL container engine is reachable again."), "dashboard");
    }

    /// <summary>Shows an update toast with either an install or release-notes action.</summary>
    public void NotifyUpdateAvailable(string version, bool canInstall)
    {
        if (!_settings.NotificationsEnabled)
        {
            return;
        }

        Show(
            UiText.Get("Common_Text0263", "Update available"),
            canInstall
                ? UiText.Get("Common_Text0264", "WSL Container Desktop {0} is ready to install. The app closes, updates and reopens.", version)
                : UiText.Get("Common_Text0265", "WSL Container Desktop {0} is available on GitHub.", version),
            page: null,
            new AppNotificationButton(canInstall ? UiText.Get("Common_Text0266", "Update now") : UiText.Get("Common_Text0267", "View release"))
                .AddArgument(ActionKey, canInstall ? UpdateAction : ReleaseNotesAction));
    }

    /// <summary>Combines the master notification switch with a per-category switch.</summary>
    private bool IsCategoryEnabled(bool categoryEnabled) => _settings.NotificationsEnabled && categoryEnabled;

    /// <summary>Formats optional engine error text for a toast body.</summary>
    private static string FormatError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error.Trim()}";

    /// <summary>Builds and displays a toast, adding routing arguments for page navigation or buttons.</summary>
    private void Show(string title, string body, string? page, AppNotificationButton? button = null)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(UiText.Translate(title))
                .AddText(UiText.Translate(body));

            // Without a page, clicking the toast body only brings the app forward.
            if (page is not null)
            {
                builder.AddArgument(PageKey, page);
            }

            if (button is not null)
            {
                builder.AddButton(button);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show notification '{Title}'.", title);
        }
    }

    /// <summary>Handles Windows toast activation and re-raises it on the UI dispatcher.</summary>
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Fires on a background thread; marshal onto the UI thread before navigating.
        var activation = ParseActivation(args);
        _dispatcher.TryEnqueue(() => ActivationRequested?.Invoke(this, activation));
    }

    /// <summary>
    /// Translates a toast's activation arguments into a <see cref="NotificationActivation"/>.
    /// Used both for live clicks and for cold-start activation handled by the app.
    /// </summary>
    public static NotificationActivation ParseActivation(AppNotificationActivatedEventArgs args)
    {
        var arguments = args.Arguments;
        string? Get(string key) => arguments is not null && arguments.TryGetValue(key, out var v) ? v : null;

        return new NotificationActivation
        {
            Page = Get(PageKey),
            Action = Get(ActionKey),
            TargetId = Get(IdKey),
        };
    }
}
