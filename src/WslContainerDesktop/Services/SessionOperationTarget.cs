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

namespace WslContainerDesktop.Services;

/// <summary>One explicitly chosen session and its guarded operation client.</summary>
internal sealed record SessionOperationTarget(
    WslcSessionSelection Selection, IWslcService Client, IWslcSessionSelectionService Selector)
{
    internal Task VerifyAsync(CancellationToken ct = default) => Selector.VerifyAsync(Selection, ct);
    internal string DisplayName => Selection.Target.Name ??
        UiText.Get("WslEngine_Session_Default", "Default CLI session");

    internal static async Task<SessionOperationTarget?> ChooseAsync(
        IWslcSessionSelectionService? selector, DialogService dialogs, ILogger logger,
        CancellationToken ct = default, bool startDefault = true)
    {
        try
        {
            if (selector is null)
                throw new InvalidOperationException(UiText.Get("SessionOperation_SelectionUnavailable",
                    "Session selection is unavailable. No action was started."));
            var selection = startDefault ? await selector.ChooseAsync(ct) : await selector.ChooseActiveAsync(ct);
            if (selection is null)
                return null;
            if (selection.Generation is null)
                throw new InvalidOperationException(UiText.Get("SessionOperation_NotRunning",
                    "The chosen session is not running. Start it in WSL engine, then try again."));
            await selector.VerifyAsync(selection, ct);
            return new(selection, selector.CreateClient(selection), selector);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsSessionFailure(ex))
        {
            logger.LogWarning(ex, "Could not choose a session for the requested operation.");
            await dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"),
                ex is OperationCanceledException
                    ? UiText.Get("CombinedSession_UnexpectedCancellation", "The engine cancelled the request unexpectedly.")
                    : ex.Message);
            return null;
        }
    }

    internal static async Task<WslcSessionSelection?> ChooseForLaterAsync(
        IWslcSessionSelectionService? selector, DialogService dialogs, ILogger logger)
        => await ChooseSelectionAsync(selector, dialogs, logger, activeOnly: false, default);

    internal static async Task<WslcSessionSelection?> ChooseActiveAsync(
        IWslcSessionSelectionService? selector, DialogService dialogs, ILogger logger, CancellationToken ct = default)
        => await ChooseSelectionAsync(selector, dialogs, logger, activeOnly: true, ct);

    private static async Task<WslcSessionSelection?> ChooseSelectionAsync(
        IWslcSessionSelectionService? selector, DialogService dialogs, ILogger logger, bool activeOnly, CancellationToken ct)
    {
        try
        {
            if (selector is null)
                throw new InvalidOperationException(UiText.Get("SessionOperation_SelectionUnavailable",
                    "Session selection is unavailable. No action was started."));
            var selection = activeOnly ? await selector.ChooseActiveAsync(ct) : await selector.ChooseForLaterAsync(ct);
            if (selection is not null)
            {
                if (activeOnly) await selector.VerifyAsync(selection, ct);
                else await selector.VerifyForLaterAsync(selection, ct);
            }
            ct.ThrowIfCancellationRequested();
            return selection;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsSessionFailure(ex))
        {
            logger.LogWarning(ex, "Could not choose a session for the saved operation.");
            await dialogs.ShowLocalizedMessageAsync(UiText.Get("CombinedSession_ResourceActionFailed", "Resource action failed"),
                ex is OperationCanceledException
                    ? UiText.Get("CombinedSession_UnexpectedCancellation", "The engine cancelled the request unexpectedly.")
                    : ex.Message);
            return null;
        }
    }

    internal static bool IsSessionFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or
            System.Text.Json.JsonException or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException;
}
