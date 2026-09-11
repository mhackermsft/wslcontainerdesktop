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
            await ViewModel.RefreshLocalRuntimePresenceAsync();
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
            ConfirmFoundryPreparationAsync(message, ct, "Install runtime only", "Accept terms and install runtime")));

    private void PrepareFoundryInitialModel_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.PrepareInitialModelAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, "Set up Foundry Local and CPU model", "Accept terms and prepare")));

    /// <summary>Quick start entry point: selects Foundry Local, then runs the same single-approval setup.</summary>
    private void SetUpFoundryLocal_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(async () =>
        {
            await ViewModel.SetUpFoundryLocalAsync((message, ct) =>
                ConfirmFoundryPreparationAsync(message, ct, "Set up Foundry Local", "Accept terms and set up"));
            await ViewModel.RefreshLocalRuntimePresenceAsync();
        });

    /// <summary>Removes the Foundry Local runtime only, keeping downloaded models.</summary>
    private void RemoveFoundryLocal_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Remove Foundry Local",
                Content = new TextBlock
                {
                    Text = "This removes the Foundry Local runtime from this PC.\n\n" +
                        "• Downloaded models are kept on disk\n" +
                        "• Shared Windows components stay installed\n" +
                        "• Your other AI providers are unchanged\n\n" +
                        "You can set it up again from Quick start.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            await ViewModel.FoundryLocal.UninstallRuntimeAsync();
            await ViewModel.RefreshLocalRuntimePresenceAsync();
        });

    private void StopFoundryServer_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.StopServerAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, "Stop shared Foundry server", "Stop this server")));

    private void StageFoundryModelFiles_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.StageModelFilesAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, "Model files only — no import or loading", "Accept license and download files")));

    private async Task<bool> ConfirmFoundryPreparationAsync(string message, CancellationToken ct, string title, string action)
    {
            ct.ThrowIfCancellationRequested();
            // Lead with a short, plain-language summary; keep the full terms one click away so the
            // dialog informs rather than overwhelms.
            var summary = new TextBlock
            {
                Text = FoundrySummary(message),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            var details = new Expander
            {
                Header = "Full details and terms",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 320,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        FontSize = 12,
                    },
                },
            };
            var content = new StackPanel { Spacing = 12, Width = 460 };
            content.Children.Add(summary);
            content.Children.Add(details);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = action,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            using var registration = ct.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary;
    }

    /// <summary>Plain-language headline for a preparation dialog; the exact terms stay available below it.</summary>
    private static string FoundrySummary(string message) =>
        message.Contains("qwen2.5-0.5b", StringComparison.OrdinalIgnoreCase)
            ? "This sets up Foundry Local on this PC and prepares a small CPU chat model, then selects it as your AI provider.\n\n" +
              "• Downloads about 878 MB of model files (Apache-2.0 licensed)\n" +
              "• Uses roughly 1.76 GB of disk once prepared\n" +
              "• Keeps any existing Foundry installation, models and settings\n" +
              "• Runs one short local test prompt; your project data is never sent\n\n" +
              "Setup needs the network. Cancelling partway does not undo what already completed."
            : "This changes the local AI runtime on this PC. Review the details below before continuing.";
}
