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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.Dialogs;
using WslContainerDesktop.ViewModels;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views;

/// <summary>Page for WSL container engine status, storage location, and WSL session maintenance actions.</summary>
public sealed partial class WslEnginePage : Page
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public WslEnginePage()
    {
        ViewModel = App.Current.Services.GetRequiredService<WslEngineViewModel>();
        Automation = App.Current.Services.GetRequiredService<AutomationReviewViewModel>();
        InitializeComponent();
    }

    /// <summary>WSL engine view model bound by the page.</summary>
    public WslEngineViewModel ViewModel { get; }
    /// <summary>Saved automatic actions that need session assignment or attention.</summary>
    public AutomationReviewViewModel Automation { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiText.LanguageChanged += OnUiLanguageChanged;
        ViewModel.ManagedSessions.Changed += OnManagedSessionsChanged;
        ViewModel.RefreshLocalizedText();
        UiSafe.Run(ViewModel.RefreshAsync);
    }

    private void OnManagedSessionsChanged(object? sender, EventArgs e) =>
        UiSafe.Run(ViewModel.RefreshSessionListAsync);

    private void CreateSession_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (!ViewModel.CanManageSessions) return;
        var dialogs = App.Current.Services.GetRequiredService<DialogService>();
        var dialog = new SimpleInputDialog(
            UiText.Get("ManagedSession_CreateTitle", "Create a session"),
            UiText.Get("ManagedSession_NameLabel", "Session name (visible to others on this PC; do not include secrets)"),
            UiText.Get("ManagedSession_NamePlaceholder", "For example, development"));
        if (await dialogs.ShowDialogAsync(dialog) == ContentDialogResult.Primary && ViewModel.CanManageSessions)
        {
            ViewModel.StopProgress.ClearCompleted();
            ViewModel.Removal.ClearCompleted();
            await RunSessionActionAsync(() => ViewModel.ManagedSessions.CreateAsync(dialog.Value));
        }
    });

    private void StartSession_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (!ViewModel.CanManageSessions) return;
        if (sender is FrameworkElement { Tag: SessionListItem { Definition: { } definition } })
        {
            ViewModel.StopProgress.ClearCompleted();
            ViewModel.Removal.ClearCompleted();
            await RunSessionActionAsync(() => ViewModel.ManagedSessions.StartSavedAsync(definition));
        }
    });

    private void StopSession_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (!ViewModel.CanManageSessions) return;
        if (sender is not FrameworkElement { Tag: SessionListItem { Generation: { } generation } item })
            return;
        var dialogs = App.Current.Services.GetRequiredService<DialogService>();
        var message = item.IsDefault && item.Definition is null
            ? UiText.Get("ManagedSession_StopDefaultConfirmation",
                "Stop the default CLI session? Its containers stop and its data is kept. This app pauses its automatic actions for that session until you select Start. Another app may start it independently. Reopening this app may also start it and containers configured for automatic start.")
        : item.Definition is null
            ? UiText.Get("ManagedSession_StopExternalConfirmation",
                "Stop {0}? Its containers will stop, and another app may be relying on it or start it again. Its data is kept.",
                item.DisplayName)
            : UiText.Get("ManagedSession_StopOwnedConfirmation",
                "Stop {0}? Its containers will stop and its data will be kept. The session starts again when the app opens.",
                item.DisplayName);
        if (await dialogs.ShowLocalizedConfirmAsync(
                UiText.Get("ManagedSession_StopTitle", "Stop session"),
                message,
                UiText.Get("ManagedSession_StopButton", "Stop session"),
                UiText.Get("Workload_Text_Cancel_19766e", "Cancel")))
        {
            if (!ViewModel.CanManageSessions || !ViewModel.StopProgress.TryBegin(item.DisplayName, item.IsDefault))
                return;
            ViewModel.Removal.ClearCompleted();
            string? failure = null;
            var completed = false;
            try
            {
                await Task.Yield();
                await RunSessionActionAsync(async () =>
                {
                    if (item.Definition is { } definition)
                        await ViewModel.ManagedSessions.StopAsync(definition);
                    else
                        await ViewModel.ManagedSessions.StopExternalAsync(generation);
                    completed = true;
                }, ex => failure = ex.Message, showDialog: false);
            }
            finally
            {
                ViewModel.StopProgress.Complete(completed ? null : failure ??
                    UiText.Get("ManagedSession_ActionFailed", "Session action failed"));
            }
        }
    });

    private void ResumeDefaultSession_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (!ViewModel.CanManageSessions ||
            sender is not FrameworkElement { Tag: SessionListItem { CanResumeDefault: true } item } ||
            !ViewModel.StopProgress.TryBegin(item.DisplayName, defaultSession: true, starting: true))
            return;
        ViewModel.Removal.ClearCompleted();
        string? failure = null;
        var completed = false;
        try
        {
            await Task.Yield();
            await RunSessionActionAsync(async () =>
            {
                await ViewModel.ManagedSessions.ResumeDefaultAsync();
                completed = true;
            }, ex => failure = ex.Message, showDialog: false);
        }
        finally
        {
            ViewModel.StopProgress.Complete(completed ? null : failure ??
                UiText.Get("ManagedSession_ActionFailed", "Session action failed"));
        }
    });

    private void RemoveSession_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (!ViewModel.CanManageSessions) return;
        if (sender is not FrameworkElement { Tag: SessionListItem { Definition: { } definition } item })
            return;
        var dialogs = App.Current.Services.GetRequiredService<DialogService>();
        var dialog = new ContentDialog
        {
            Title = UiText.Get("ManagedSession_RemoveTitle", "Remove session"),
            Content = new TextBlock
            {
                Text = UiText.Get("ManagedSession_RemoveConfirmation",
                    "Remove {0}? Its app-owned instance will stop if running; a session created by another app will not be stopped. Delete data permanently removes its containers, images, volumes, and stored files; keep data leaves them on disk. Automatic-start history for this session will be forgotten when its owner can be identified.",
                    item.DisplayName),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = UiText.Get("ManagedSession_DeleteData", "Delete data"),
            SecondaryButtonText = UiText.Get("ManagedSession_KeepData", "Keep data"),
            CloseButtonText = UiText.Get("Workload_Text_Cancel_19766e", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        var choice = await dialogs.ShowDialogAsync(dialog);
        if (choice == ContentDialogResult.None)
            return;
        if (!ViewModel.CanManageSessions || !ViewModel.Removal.TryBegin(item.DisplayName))
            return;
        ViewModel.StopProgress.ClearCompleted();
        string? failure = null;
        string? path = null;
        try
        {
            await Task.Yield();
            await RunSessionActionAsync(async () =>
                path = await ViewModel.ManagedSessions.RemoveAsync(definition, choice == ContentDialogResult.Primary),
                error => failure = error.Message);
        }
        finally
        {
            ViewModel.Removal.Complete(failure ?? (path is null
                ? UiText.Get("ManagedSession_ActionFailed", "Session action failed") : null));
        }
        if (path is not null && choice == ContentDialogResult.Secondary)
            await dialogs.ShowLocalizedMessageAsync(UiText.Get("ManagedSession_DataKeptTitle", "Session data kept"),
                UiText.Get("ManagedSession_DataKept",
                    "The session data remains at {0}. The app will not reopen it automatically, and uninstalling the app may remove it.", path));
    });

    private async Task RunSessionActionAsync(Func<Task> action, Action<Exception>? failed = null, bool showDialog = true)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or
                                   ArgumentException or System.Text.Json.JsonException or
                                   System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or
                                   TimeoutException or OperationCanceledException)
        {
            failed?.Invoke(ex);
            App.Current.Services.GetRequiredService<ILoggerFactory>().CreateLogger<WslEnginePage>()
                .LogWarning(ex, "A session management action failed.");
            if (showDialog)
                await App.Current.Services.GetRequiredService<DialogService>().ShowLocalizedMessageAsync(
                    UiText.Get("ManagedSession_ActionFailed", "Session action failed"), ex.Message);
        }
    }

    private void AssignAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AutomationReviewItem item })
            UiSafe.Run(() => Automation.AssignAsync(item, ViewModel.SelectedAssignmentSession));
    }

    private void RetryAutomation_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(Automation.RetryAsync);

    private void ForgetTracking_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanManageSessions && sender is FrameworkElement { Tag: AutomationTrackingIssue issue })
            UiSafe.Run(() => Automation.ForgetTrackingAsync(issue));
    }

    private void ChangeStorageLocation_Click(object sender, RoutedEventArgs e) => UiSafe.Run(async () =>
    {
        if (!ViewModel.CanChangeStorage) return;
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.Current.MainWindow!.AppWindow.Id);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && ViewModel.CanChangeStorage)
        {
            await ViewModel.ChangeStorageLocationAsync(folder.Path);
        }
    });
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        UiText.LanguageChanged -= OnUiLanguageChanged;
        ViewModel.ManagedSessions.Changed -= OnManagedSessionsChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        ViewModel.RefreshLocalizedText();
        Bindings.Update();
    }
}
