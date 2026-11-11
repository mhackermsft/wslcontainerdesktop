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

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pending = new();
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action callback)
        {
            _pending.Enqueue(callback);
            return true;
        }

        public void Drain()
        {
            while (_pending.TryDequeue(out var callback))
                callback();
        }
    }
}

// Only the dialog boundary is replaced: the source-linked VM and generated commands are real.
namespace Microsoft.UI.Xaml
{
    /// <summary>Minimal WinUI text-wrapping enum used by source-linked dialog view-model tests.</summary>
    public enum TextWrapping { Wrap }
}

namespace Microsoft.UI.Xaml.Controls
{
    /// <summary>Minimal WinUI dialog result enum used by dialog service tests.</summary>
    public enum ContentDialogResult { None, Primary, Secondary }
    public enum ContentDialogButton { Close }
    public sealed class ContentDialog
    {
        public string Title { get; set; } = "";
        public object? Content { get; set; }
        public string PrimaryButtonText { get; set; } = "";
        public string SecondaryButtonText { get; set; } = "";
        public string CloseButtonText { get; set; } = "";
        public ContentDialogButton DefaultButton { get; set; }
    }

    /// <summary>Small stand-in for WinUI <c>TextBlock</c> used when dialogs create explanatory content.</summary>
    public sealed class TextBlock
    {
        public string Text { get; set; } = "";
        public Microsoft.UI.Xaml.TextWrapping TextWrapping { get; set; }
    }
}

namespace WslContainerDesktop.Dialogs
{
    /// <summary>Captures the compose compatibility preview that a real dialog would show to the user.</summary>
    public sealed class ComposePreviewDialog
    {
        public ComposePreviewDialog(ComposeCompatibilityPreview preview) => Preview = preview;
        public ComposeCompatibilityPreview Preview { get; }
    }

    /// <summary>Placeholder import dialog that prevents tests from opening real UI.</summary>
    public sealed class ImportComposeDialog
    {
        public string Yaml => throw new NotSupportedException();
        public string BaseDirectory => throw new NotSupportedException();
    }

    /// <summary>Stores the default value supplied to a simple input dialog without rendering WinUI.</summary>
    public sealed class SimpleInputDialog
    {
        public SimpleInputDialog(string title, string label, string value) => Value = value;
        public string Value { get; set; }
        public void SetSession(WslContainerDesktop.Services.WslcSessionSelection selection,
            string titleKey, string titleFallback, string labelKey, string labelFallback,
            string placeholderKey, string placeholderFallback) { }
    }

    public sealed class CreateNetworkDialog
    {
        public object Title { get; set; } = string.Empty;
        public string NetworkName { get; set; } = "";
        public string? Driver => null;
        public IReadOnlyList<string> DriverOptions => [];
        public Dictionary<string, string> Labels => [];
        public string? Subnet => null;
        public string? Gateway => null;
        public string? IpRange => null;
        public bool InternalNetwork => false;
        public void SetSession(WslContainerDesktop.Services.WslcSessionSelection selection) => Title = $"Create network in {selection.DisplayName}";
    }

    public sealed class PullImageDialog
    {
        public object Title { get; set; } = string.Empty;
        public PullImageDialog(object registries, WslContainerDesktop.Services.IRegistryCatalogService catalog) { }
        public string Reference { get; set; } = "";
        public bool AllTags { get; set; }
        public void SetSession(WslContainerDesktop.Services.WslcSessionSelection selection) => Title = $"Pull image into {selection.DisplayName}";
    }

    public sealed class PushImageDialog
    {
        public object Title { get; set; } = string.Empty;
        public PushImageDialog(object registries, string reference,
            Func<RegistryEntry, Task<(RegistryLoginState State, string? User)>> checkSignIn) => Reference = reference;
        public bool OpenRegistriesRequested { get; set; }
        public string Reference { get; set; }
        public bool AllTags { get; set; }
    }

    public sealed class RunContainerDialog
    {
        public object Title { get; set; } = string.Empty;
        public RunContainerDialog(WslContainerDesktop.Services.IWslcService engine, object registries,
            WslContainerDesktop.Services.IRunProfileStore profiles, string image) { }
        public RunContainerOptions? Options => null;
    }

    public sealed class BuildImageDialog
    {
        public object Title { get; set; } = string.Empty;
        public BuildImageDialog(object registries) { }
        public string ContextPath { get; set; } = "";
        public string ImageTag { get; set; } = "";
        public string? Dockerfile { get; set; }
        public void SetSession(WslContainerDesktop.Services.WslcSessionSelection selection) => Title = $"Build image in {selection.DisplayName}";
    }

    public sealed class RegistryLoginDialog
    {
        private readonly string _name;
        public RegistryLoginDialog(string name, string? username, bool dockerHub) { _name = name; Username = username ?? ""; }
        public object Title { get; set; } = "";
        public string Username { get; set; }
        public string Password { get; set; } = "";
        public void SetSession(WslContainerDesktop.Services.WslcSessionSelection selection) => Title = $"Sign in to {_name} using session {selection.DisplayName}";
    }

    public sealed class AddRegistryDialog
    {
        public RegistryEntry? Registry { get; set; }
        public bool LoginNow { get; set; }
        public string Password { get; set; } = "";
    }

    public sealed class AddAcrDialog
    {
        public AddAcrDialog(WslContainerDesktop.Services.IAzureCliService azure) { }
        public RegistryEntry? Registry { get; set; }
        public string? Token { get; set; }
        public string TokenUsername { get; set; } = "";
    }

    /// <summary>Holds a compose operation request that tests can inspect instead of showing the real services dialog.</summary>
    public sealed class ComposeServicesDialog
    {
        public ComposeServicesDialog(ComposeProject project) { }
        public ComposeOperationRequest Request { get; set; } = new();
        public string OperationLabel => "Apply";
        public bool SaveReplicaOverrides { get; set; }
    }
}

namespace WslContainerDesktop.Services
{
    /// <summary>Test dialog service that returns controlled results and exposes message-display synchronization points.</summary>
    public sealed class DialogService
    {
        public Microsoft.UI.Dispatching.DispatcherQueue? Dispatcher { get; set; }
        public TaskCompletionSource MessageShown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DismissMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<object, Microsoft.UI.Xaml.Controls.ContentDialogResult>? OnShowDialog { get; set; }
        public int ConfirmCount { get; private set; }
        public bool ConfirmResult { get; set; } = true;
        public string? LastMessage { get; private set; }
        public string? LastConfirmation { get; private set; }
        public Action? OnConfirm { get; set; }
        public Func<Task<bool>>? ConfirmOverride { get; set; }

        public Task<Microsoft.UI.Xaml.Controls.ContentDialogResult> ShowDialogAsync(object dialog) =>
            Task.FromResult(OnShowDialog?.Invoke(dialog) ?? throw new NotSupportedException());

        public Task<bool> ShowConfirmAsync(string title, string message, string primaryText)
        {
            ConfirmCount++;
            LastConfirmation = message;
            OnConfirm?.Invoke();
            return ConfirmOverride?.Invoke() ?? Task.FromResult(ConfirmResult);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            LastMessage = message;
            MessageShown.TrySetResult();
            return DismissMessage.Task;
        }

        public Task ShowLocalizedMessageAsync(string title, string message) => ShowMessageAsync(title, message);
        public Task<bool> ShowLocalizedConfirmAsync(string title, string message, string primaryText, string closeText) =>
            ShowConfirmAsync(title, message, primaryText);
        public Task<bool> ShowLocalizedConfirmAsync(Func<(string Title, string Message, string Primary, string Close)> text)
        {
            var value = text();
            return ShowConfirmAsync(value.Title, value.Message, value.Primary);
        }
    }
}
