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

    private readonly ISettingsService _settings;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<NotificationService> _logger;

    private bool _registered;

    public event EventHandler<NotificationActivation>? ActivationRequested;

    public NotificationService(ISettingsService settings, DispatcherQueue dispatcher, ILogger<NotificationService> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

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

    public void NotifyImagePull(string reference, bool success, string? error = null)
    {
        if (!IsCategoryEnabled(_settings.NotifyImageEvents))
        {
            return;
        }

        if (success)
        {
            Show("Image pulled", $"Finished pulling {reference}.", "images");
        }
        else
        {
            Show("Pull failed", $"Could not pull {reference}.{FormatError(error)}", "images");
        }
    }

    public void NotifyImageBuild(string tag, bool success, string? error = null)
    {
        if (!IsCategoryEnabled(_settings.NotifyImageEvents))
        {
            return;
        }

        if (success)
        {
            Show("Build complete", $"Finished building {tag}.", "images");
        }
        else
        {
            Show("Build failed", $"Could not build {tag}.{FormatError(error)}", "images");
        }
    }

    public void NotifyContainerExited(string containerName, string containerId)
    {
        if (!IsCategoryEnabled(_settings.NotifyContainerEvents))
        {
            return;
        }

        Show(
            "Container stopped",
            $"\"{containerName}\" is no longer running.",
            "containers",
            new AppNotificationButton("View logs")
                .AddArgument(ActionKey, "logs")
                .AddArgument(IdKey, containerId)
                .AddArgument(PageKey, "containers"));
    }

    public void NotifyEngineDown()
    {
        if (!IsCategoryEnabled(_settings.NotifyEngineEvents))
        {
            return;
        }

        Show("Engine unavailable", "The WSL container engine became unreachable.", "dashboard");
    }

    public void NotifyEngineRecovered()
    {
        if (!IsCategoryEnabled(_settings.NotifyEngineEvents))
        {
            return;
        }

        Show("Engine recovered", "The WSL container engine is reachable again.", "dashboard");
    }

    private bool IsCategoryEnabled(bool categoryEnabled) => _settings.NotificationsEnabled && categoryEnabled;

    private static string FormatError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error.Trim()}";

    private void Show(string title, string body, string page, AppNotificationButton? button = null)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument(PageKey, page)
                .AddText(title)
                .AddText(body);

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
