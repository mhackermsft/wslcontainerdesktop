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

using WslContainerDesktop.Models;

// Only the dialog boundary is replaced: the source-linked VM and generated commands are real.
namespace Microsoft.UI.Xaml.Controls
{
    public enum ContentDialogResult { None, Primary, Secondary }
}

namespace WslContainerDesktop.Dialogs
{
    public sealed class ImportComposeDialog
    {
        public string Yaml => throw new NotSupportedException();
        public string BaseDirectory => throw new NotSupportedException();
    }

    public sealed class SimpleInputDialog
    {
        public SimpleInputDialog(string title, string label, string value) => Value = value;
        public string Value { get; set; }
    }

    public sealed class ComposeServicesDialog
    {
        public ComposeServicesDialog(ComposeProject project) { }
        public ComposeOperationRequest Request => throw new NotSupportedException();
        public string OperationLabel => throw new NotSupportedException();
    }
}

namespace WslContainerDesktop.Services
{
    public sealed class DialogService
    {
        public TaskCompletionSource MessageShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DismissMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Microsoft.UI.Xaml.Controls.ContentDialogResult> ShowDialogAsync(object dialog) =>
            throw new NotSupportedException();

        public Task<bool> ShowConfirmAsync(string title, string message, string primaryText) =>
            Task.FromResult(true);

        public Task ShowMessageAsync(string title, string message)
        {
            MessageShown.TrySetResult();
            return DismissMessage.Task;
        }
    }
}
