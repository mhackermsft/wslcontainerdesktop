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

using WslContainerDesktop.Services;

namespace WslContainerDesktop.Views;

/// <summary>Page for app preferences, updates, startup behavior, and local or remote AI provider setup.</summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>Initializes the page/control and resolves its view model from the app service provider.</summary>
    public SettingsPage()
    {
        ViewModel = App.Current.Services.GetRequiredService<SettingsViewModel>();
        Updates = App.Current.Services.GetRequiredService<AppUpdateViewModel>();
        InitializeComponent();
        ViewModel.ThemeChangeRequested += (_, theme) => App.Current.MainWindow?.ApplyTheme(theme);
    }

    /// <summary>Settings view model bound by the page.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Update view model shared with the shell so Settings can trigger update checks.</summary>
    public AppUpdateViewModel Updates { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LanguageChangeRequested += OnLanguageChangeRequested;
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.LanguageChangeRequested -= OnLanguageChangeRequested;
        base.OnNavigatedFrom(e);
    }

    private void OnLanguageChangeRequested(object? sender, string language) =>
        App.Current.MainWindow?.ApplyLanguage(language, refreshUi: true);

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
                Title = UiText.Get("Common_Text0510", "Connect to existing Foundry Local"),
                Content = new ScrollViewer
                {
                    Content = new TextBlock { Text = UiText.TranslateLines(message), TextWrapping = TextWrapping.Wrap },
                    MaxHeight = 450,
                },
                PrimaryButtonText = UiText.Get("Common_Text0511", "Use this endpoint"),
                CloseButtonText = UiText.Get("Common_Text0094", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }));

    private void InstallFoundryRuntime_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.InstallRuntimeAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, UiText.Get("Common_Text0512", "Install runtime only"), UiText.Get("Common_Text0513", "Accept terms and install runtime"))));

    private void PrepareFoundryInitialModel_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.PrepareInitialModelAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, UiText.Get("Common_Text0514", "Set up Foundry Local and CPU model"), UiText.Get("Common_Text0515", "Accept terms and prepare"))));

    /// <summary>Quick start entry point: selects Foundry Local, then runs the same single-approval setup.</summary>
    private void SetUpFoundryLocal_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(async () =>
        {
            await ViewModel.SetUpFoundryLocalAsync((message, ct) =>
                ConfirmFoundryPreparationAsync(message, ct, UiText.Get("Common_Text0516", "Set up Foundry Local"), UiText.Get("Common_Text0517", "Accept terms and set up")));
            await ViewModel.RefreshLocalRuntimePresenceAsync();
        });

    /// <summary>Removes the Foundry Local runtime only, keeping downloaded models.</summary>
    private void RemoveFoundryLocal_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = UiText.Get("Common_Text0518", "Remove Foundry Local"),
                Content = new TextBlock
                {
                    Text = UiText.Get("Common_Text0519", "This removes the Foundry Local runtime from this PC.\n\n") +
                        UiText.Get("Common_Text0520", "• Downloaded models are kept on disk\n") +
                        UiText.Get("Common_Text0521", "• Shared Windows components stay installed\n") +
                        UiText.Get("Common_Text0522", "• Your other AI providers are unchanged\n\n") +
                        UiText.Get("Common_Text0523", "You can set it up again from Quick start."),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = UiText.Get("Common_Remove", "Remove"),
                CloseButtonText = UiText.Get("Common_Text0094", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            await ViewModel.FoundryLocal.UninstallRuntimeAsync();
            await ViewModel.RefreshLocalRuntimePresenceAsync();
        });

    private void StopFoundryServer_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.StopServerAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, UiText.Get("Common_Text0524", "Stop shared Foundry server"), UiText.Get("Common_Text0525", "Stop this server"))));

    private void StageFoundryModelFiles_Click(object sender, RoutedEventArgs e) =>
        UiSafe.Run(() => ViewModel.FoundryLocal.StageModelFilesAsync((message, ct) =>
            ConfirmFoundryPreparationAsync(message, ct, UiText.Get("Common_Text0526", "Model files only — no import or loading"), UiText.Get("Common_Text0527", "Accept license and download files"))));

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
                Header = UiText.Get("Common_Text0528", "Full details and terms"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new ScrollViewer
                {
                    MaxHeight = 320,
                    Content = new TextBlock
                    {
                        Text = UiText.TranslateLines(message),
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
                Title = UiText.Translate(title),
                Content = content,
                PrimaryButtonText = UiText.Translate(action),
                CloseButtonText = UiText.Get("Common_Text0094", "Cancel"),
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
            ? UiText.Get("Common_Text0529", "This sets up Foundry Local on this PC and prepares a small CPU chat model, then selects it as your AI provider.\n\n") +
              UiText.Get("Common_Text0530", "• Downloads about 878 MB of model files (Apache-2.0 licensed)\n") +
              UiText.Get("Common_Text0531", "• Uses roughly 1.76 GB of disk once prepared\n") +
              UiText.Get("Common_Text0532", "• Keeps any existing Foundry installation, models and settings\n") +
              UiText.Get("Common_Text0533", "• Runs one short local test prompt; your project data is never sent\n\n") +
              UiText.Get("Common_Text0534", "Setup needs the network. Cancelling partway does not undo what already completed.")
            : UiText.Get("Common_Text0535", "This changes the local AI runtime on this PC. Review the details below before continuing.");
}
