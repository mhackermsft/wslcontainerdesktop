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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WslContainerDesktop.Services;

/// <summary>
/// Centralizes ContentDialog display. The active window sets <see cref="XamlRoot"/>
/// so view models can raise dialogs without holding a reference to the window.
/// </summary>
public sealed class DialogService
{
    private XamlRoot? _xamlRoot;
    /// <summary>Dispatcher captured from the active XAML root so callers can marshal dialog work to the UI thread.</summary>
    public Microsoft.UI.Dispatching.DispatcherQueue? Dispatcher { get; private set; }
    /// <summary>Current window root required by WinUI before a <c>ContentDialog</c> can be shown.</summary>
    public XamlRoot? XamlRoot
    {
        get => _xamlRoot;
        set
        {
            _xamlRoot = value;
            Dispatcher = value is null ? null : Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
    }

    // ContentDialog content renders in a separate popup scope that does NOT inherit the app-level
    // TextControlPlaceholderForeground override declared in App.xaml, so hint text in dialog
    // TextBoxes would otherwise render at the framework's brighter default. Copy those dim
    // placeholder brushes into each dialog's own resource scope (which its content DOES inherit)
    // so placeholders match the rest of the app.
    private static readonly string[] PlaceholderBrushKeys =
    {
        "TextControlPlaceholderForeground",
        "TextControlPlaceholderForegroundPointerOver",
        "TextControlPlaceholderForegroundFocused",
        "ComboBoxPlaceHolderForeground",
        "ComboBoxPlaceHolderForegroundPointerOver",
        "ComboBoxPlaceHolderForegroundFocused",
        "ComboBoxPlaceHolderForegroundPressed",
        "ComboBoxPlaceHolderForegroundFocusedPressed",
    };

    private static void ApplyDimPlaceholders(ContentDialog dialog)
    {
        var appResources = Application.Current.Resources;
        foreach (var key in PlaceholderBrushKeys)
        {
            if (appResources.TryGetValue(key, out var brush))
            {
                dialog.Resources[key] = brush;
            }
        }
    }

    /// <summary>Shows an informational dialog with selectable wrapped text, if a window is available.</summary>
    public Task ShowMessageAsync(string title, string message) =>
        ShowMessageCoreAsync(UiText.Translate(title), UiText.TranslateLines(message));

    /// <summary>Shows already localized text or raw engine output without translating its contents.</summary>
    public Task ShowLocalizedMessageAsync(string title, string message) => ShowMessageCoreAsync(title, message);

    private async Task ShowMessageCoreAsync(string title, string message)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                MaxHeight = 480,
            },
            CloseButtonText = UiText.Get("Common_Text0485", "OK"),
            XamlRoot = XamlRoot,
        };

        ApplyDimPlaceholders(dialog);
        await dialog.ShowAsync();
    }

    /// <summary>Shows a yes/cancel confirmation dialog and returns true only for the primary button.</summary>
    public Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "Yes", string closeText = "Cancel") =>
        ShowConfirmCoreAsync(UiText.Translate(title), UiText.TranslateLines(message), UiText.Translate(primaryText), UiText.Translate(closeText));

    /// <summary>Shows already localized wording without translating interpolated values.</summary>
    public Task<bool> ShowLocalizedConfirmAsync(string title, string message, string primaryText, string closeText) =>
        ShowConfirmCoreAsync(title, message, primaryText, closeText);

    /// <summary>Shows keyed confirmation wording that refreshes in place on a language change.</summary>
    public Task<bool> ShowLocalizedConfirmAsync(Func<(string Title, string Message, string Primary, string Close)> text)
    {
        var initial = text();
        return ShowConfirmCoreAsync(initial.Title, initial.Message, initial.Primary, initial.Close, text);
    }

    private async Task<bool> ShowConfirmCoreAsync(string title, string message, string primaryText, string closeText,
        Func<(string Title, string Message, string Primary, string Close)>? text = null)
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ApplyDimPlaceholders(dialog);
        void Refresh(object? sender, EventArgs args)
        {
            if (text is null) return;
            var value = text();
            dialog.Title = value.Title;
            body.Text = value.Message;
            dialog.PrimaryButtonText = value.Primary;
            dialog.CloseButtonText = value.Close;
        }
        if (text is not null) UiText.LanguageChanged += Refresh;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { if (text is not null) UiText.LanguageChanged -= Refresh; }
    }

    /// <summary>Shows a caller-built dialog, wiring the correct XamlRoot.</summary>
    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = XamlRoot;
        ApplyDimPlaceholders(dialog);
        return await dialog.ShowAsync();
    }
}
