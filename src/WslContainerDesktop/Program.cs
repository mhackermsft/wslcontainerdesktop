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

using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WslContainerDesktop;

/// <summary>
/// Custom entry point that enforces a single running instance. A second launch
/// redirects its activation to the already-running instance (which then shows and
/// foregrounds its window) and exits immediately.
/// </summary>
public static class Program
{
    private const string InstanceKey = "WslContainerDesktop.SingleInstance";

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        XamlCheckProcessRequirements();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var isPrimary = DecideRedirection();
        if (!isPrimary)
        {
            // Another instance handled the activation; this process is done.
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Returns true if this process should become the primary (UI) instance;
    /// false if it redirected to an existing instance and should exit.
    /// </summary>
    private static bool DecideRedirection()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (keyInstance.IsCurrent)
        {
            // We are the primary instance. Listen for future redirected activations.
            keyInstance.Activated += OnActivated;
            return true;
        }

        // Redirect this activation to the primary instance and exit.
        RedirectActivationTo(args, keyInstance);
        return false;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        // A second instance asked us to come forward.
        App.Current?.BringToForeground();
    }

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance target)
    {
        // Redirect synchronously with a short wait so the primary instance has time
        // to process the activation before this process exits.
        var redirectSemaphore = new SemaphoreSlim(0, 1);
        Task.Run(async () =>
        {
            try
            {
                await target.RedirectActivationToAsync(args);
            }
            finally
            {
                redirectSemaphore.Release();
            }
        });

        redirectSemaphore.Wait(TimeSpan.FromSeconds(5));
    }
}
