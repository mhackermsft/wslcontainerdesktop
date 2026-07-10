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
using Windows.ApplicationModel;

namespace WslContainerDesktop.Services;

/// <summary>Outcome of trying to change the run-at-login setting.</summary>
public enum StartupToggleResult
{
    /// <summary>The change was applied.</summary>
    Applied,

    /// <summary>The user disabled startup in Task Manager; only they can re-enable it there.</summary>
    BlockedByUser,

    /// <summary>An administrator policy controls this setting.</summary>
    BlockedByPolicy,

    /// <summary>The startup task could not be found or the API failed.</summary>
    Unavailable,
}

/// <summary>
/// Wraps the packaged-app StartupTask API to run the app automatically at sign-in. Uses the
/// task declared in the app manifest, so the state stays in sync with Windows' Startup apps
/// control and survives package updates.
/// </summary>
public sealed class StartupService(ILogger<StartupService> logger)
{
    private const string TaskId = "WslContainerDesktopStartupTask";

    /// <summary>Whether run-at-login is currently enabled (including enabled-by-policy).</summary>
    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read startup task state.");
            return false;
        }
    }

    /// <summary>
    /// Whether the app can change this setting itself. When false (disabled or enabled by the
    /// user in Task Manager, or by policy), the toggle should be shown but explain the situation.
    /// </summary>
    public async Task<bool> CanToggleAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.Disabled;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query whether the startup task can be toggled.");
            return false;
        }
    }

    /// <summary>Enables or disables run-at-login, returning the concrete outcome.</summary>
    public async Task<StartupToggleResult> SetEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);

            if (enabled)
            {
                var newState = await task.RequestEnableAsync();
                return newState switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupToggleResult.Applied,
                    StartupTaskState.DisabledByUser => StartupToggleResult.BlockedByUser,
                    StartupTaskState.DisabledByPolicy => StartupToggleResult.BlockedByPolicy,
                    _ => StartupToggleResult.Unavailable,
                };
            }

            switch (task.State)
            {
                case StartupTaskState.DisabledByPolicy:
                case StartupTaskState.EnabledByPolicy:
                    return StartupToggleResult.BlockedByPolicy;
                case StartupTaskState.DisabledByUser:
                    return StartupToggleResult.BlockedByUser;
                default:
                    task.Disable();
                    return StartupToggleResult.Applied;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to {Action} the startup task.", enabled ? "enable" : "disable");
            return StartupToggleResult.Unavailable;
        }
    }
}

