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

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>Shows one host-script decision on the UI thread; missing or unavailable UI declines.</summary>
public sealed class DevContainerHostCommandPresenter(DialogService dialogs) : IDevContainerHostCommandPresenter
{
    public async Task<bool> ConfirmAsync(DevContainerHostCommandReview review, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = ct.Register(() => completion.TrySetResult(false));
        var dispatcher = dialogs.Dispatcher;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            Helpers.UiSafe.Run(() => ShowAsync(review, completion, dispatcher, ct))))
            completion.TrySetResult(false);
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ShowAsync(DevContainerHostCommandReview review, TaskCompletionSource<bool> completion,
        DispatcherQueue dispatcher, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) { completion.TrySetResult(false); return; }
            var dialog = new HostCommandDialog(review);
            using var cancellation = ct.Register(() => dispatcher.TryEnqueue(() => dialog.Hide()));
            var result = await dialogs.ShowDialogAsync(dialog);
            completion.TrySetResult(!ct.IsCancellationRequested && result == ContentDialogResult.Primary);
        }
        catch (Exception)
        {
            // A busy/unavailable dialog must fail closed, without logging potentially sensitive scripts.
            completion.TrySetResult(false);
        }
    }

    private sealed class HostCommandDialog : ContentDialog
    {
        public HostCommandDialog(DevContainerHostCommandReview review)
        {
            Title = "Allow Windows host commands?";
            PrimaryButtonText = "Run on Windows";
            CloseButtonText = "Cancel";
            DefaultButton = ContentDialogButton.Close;
            AutomationProperties.SetAutomationId(this, "DevContainerHostCommandReview");

            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "These initializeCommand scripts run on your Windows host with your user privileges, " +
                               "NOT inside the container. They can read, change, or delete your files and run other programs. " +
                               "Only allow commands you trust. Approval applies only to this start or rebuild.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = "Windows workspace (working directory):" },
                    new TextBlock
                    {
                        Text = review.DisplayWorkspacePath, TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true, FlowDirection = FlowDirection.LeftToRight,
                    },
                    new TextBlock
                    {
                        Text = @"Escaped display: backslashes appear as \\, and invisible controls/format characters " +
                               @"as \uXXXX or \UXXXXXXXX. Ordinary script line breaks are preserved. The original text executes.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock { Text = "Commands, in execution order (cmd.exe /d /c):" },
                    new ScrollViewer
                    {
                        MaxHeight = 320,
                        Content = new TextBlock
                        {
                            Text = string.Join("\n\n", review.DisplayCommands.Select((command, index) => $"Command {index + 1}:\n{command}")),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            FlowDirection = FlowDirection.LeftToRight,
                        },
                    },
                },
            };
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("PrimaryButton") is FrameworkElement allow)
                AutomationProperties.SetAutomationId(allow, "DevContainerHostCommandAllow");
            if (GetTemplateChild("CloseButton") is FrameworkElement cancel)
                AutomationProperties.SetAutomationId(cancel, "DevContainerHostCommandCancel");
        }
    }
}
