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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Marshals service/assistant callers to the UI thread; absent UI always declines.</summary>
public sealed class ComposeReviewPresenter(DialogService dialogs) : IComposeReviewPresenter
{
    public async Task<bool> ConfirmAsync(ComposeCompatibilityPreview preview, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = ct.Register(() => completion.TrySetResult(false));
        var dispatcher = dialogs.Dispatcher;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            Helpers.UiSafe.Run(() => ShowAsync(preview, completion, dispatcher, ct))))
            completion.TrySetResult(false);
        return await completion.Task.ConfigureAwait(false);
    }

    // UI callback is exception-contained, including cancellation while a dialog is open.
    private async Task ShowAsync(ComposeCompatibilityPreview preview, TaskCompletionSource<bool> completion,
        DispatcherQueue dispatcher, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) { completion.TrySetResult(false); return; }
            var dialog = new ComposePreviewDialog(preview);
            using var registration = ct.Register(() => dispatcher.TryEnqueue(() => dialog.Hide()));
            var result = await dialogs.ShowDialogAsync(dialog);
            completion.TrySetResult(!ct.IsCancellationRequested && preview.CanApply && result == ContentDialogResult.Primary);
        }
        catch (Exception)
        {
            // No raw dialog/engine context is logged: unavailable/busy presentation fails closed.
            completion.TrySetResult(false);
        }
    }
}
