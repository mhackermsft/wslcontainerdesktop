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
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslContainerDesktop.Helpers;
using WslContainerDesktop.ViewModels;

namespace WslContainerDesktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        ViewModel.ThemeChangeRequested += (_, theme) => App.Current.MainWindow?.ApplyTheme(theme);
    }

    public SettingsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiSafe.Run(async () =>
        {
            await ViewModel.LoadVersionAsync();
            await ViewModel.LoadStartupStateAsync();
            ViewModel.LoadAiSecretState();
            await ViewModel.LoadGitHubCopilotModelsAsync();
            await ViewModel.LoadOllamaModelsAsync();
        });
    }

    private void SaveAiApiKey_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveAiApiKey(AiApiKeyBox.Password);
        AiApiKeyBox.Password = string.Empty;
    }

    private void LocalAiFeedbackBar_CloseButtonClick(InfoBar sender, object args) =>
        ViewModel.DismissLocalAiFeedbackCommand.Execute(null);

    private void ProviderFeedbackBar_CloseButtonClick(InfoBar sender, object args) =>
        ViewModel.DismissProviderFeedbackCommand.Execute(null);

    private void UseFoundryEndpoint_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.UseDiscoveredEndpointAsync(async message =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Connect to existing Foundry Local",
                Content = new ScrollViewer
                {
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    MaxHeight = 450,
                },
                PrimaryButtonText = "Use this endpoint",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }));

    private void InstallFoundryRuntime_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.InstallRuntimeAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, "Runtime only — model setup remains blocked", "Accept terms and install runtime")));

    private void StageFoundryModelFiles_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.StageModelFilesAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, "Model files only — no import or loading", "Accept license and download files")));

    private async Task<bool> ConfirmFoundryPreparationAsync(string message, CancellationToken ct, string title, string action)
    {
            ct.ThrowIfCancellationRequested();
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new ScrollViewer
                {
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    MaxHeight = 450,
                },
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary;
    }
}
