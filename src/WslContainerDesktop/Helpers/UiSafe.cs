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

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WslContainerDesktop.Helpers;

/// <summary>
/// Helpers for the unavoidable <c>async void</c> event handlers required by framework signatures.
/// An unhandled exception in an <c>async void</c> method would otherwise crash the app; routing the
/// work through <see cref="Run"/> keeps a failed handler contained and logged.
/// </summary>
public static class UiSafe
{
    /// <summary>
    /// Runs <paramref name="action"/> and swallows-with-logging any exception, so a failing UI
    /// event handler never takes the process down. Intended to be the single line inside an
    /// <c>async void</c> handler.
    /// </summary>
    public static async void Run(Func<Task> action, [CallerMemberName] string? caller = null)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Current.Services.GetService<ILoggerFactory>()?
                .CreateLogger("UiHandler")
                .LogError(ex, "UI handler {Handler} failed.", caller ?? "(unknown)");
        }
    }
}
